using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCommon.Tools;

namespace AgentCommon.Cron;

public sealed class CronJob
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("cron")]
    public string Cron { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("recurring")]
    public bool Recurring { get; set; } = true;

    [JsonPropertyName("durable")]
    public bool Durable { get; set; } = true;
}

public sealed class CronScheduler : IDisposable
{
    private readonly string _durablePath;
    private readonly ConcurrentDictionary<string, CronJob> _jobs = new();
    private readonly ConcurrentQueue<CronJob> _queue = new();
    private readonly ConcurrentDictionary<string, string> _lastFired = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string>? _onLog;

    public CronScheduler(string workDir, Action<string>? onLog = null)
    {
        _durablePath = Path.Combine(workDir, ".scheduled_tasks.json");
        _onLog = onLog;
        LoadDurable();
        var t = new Thread(Loop) { IsBackground = true, Name = "cron-scheduler" };
        t.Start();
        _onLog?.Invoke("\u001b[35m[cron] scheduler thread started\u001b[0m");
    }

    public string Schedule(string cron, string prompt, bool recurring = true, bool durable = true)
    {
        var err = ValidateCron(cron);
        if (err is not null) return $"Error: {err}";
        var job = new CronJob
        {
            Id = $"cron_{Random.Shared.Next(0, 1_000_000):D6}",
            Cron = cron,
            Prompt = prompt,
            Recurring = recurring,
            Durable = durable,
        };
        _jobs[job.Id] = job;
        if (durable) SaveDurable();
        _onLog?.Invoke($"\u001b[35m[cron register] {job.Id} '{cron}' → {Trunc(prompt, 40)}\u001b[0m");
        return $"Scheduled {job.Id}: '{cron}' → {Trunc(prompt, 60)}";
    }

    public string Cancel(string jobId)
    {
        if (!_jobs.TryRemove(jobId, out _))
            return $"Error: Job {jobId} not found";
        SaveDurable();
        _onLog?.Invoke($"\u001b[31m[cron cancel] {jobId}\u001b[0m");
        return $"Cancelled {jobId}";
    }

    public IReadOnlyList<CronJob> List() => _jobs.Values.OrderBy(j => j.Id).ToList();

    public IReadOnlyList<CronJob> DrainQueue()
    {
        var fired = new List<CronJob>();
        while (_queue.TryDequeue(out var j)) fired.Add(j);
        return fired;
    }

    private void Loop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(1000);
                var now = DateTime.Now;
                var marker = now.ToString("yyyy-MM-dd HH:mm");
                foreach (var job in _jobs.Values.ToList())
                {
                    try
                    {
                        if (!CronMatches(job.Cron, now)) continue;
                        if (_lastFired.TryGetValue(job.Id, out var prev) && prev == marker) continue;

                        _queue.Enqueue(job);
                        _lastFired[job.Id] = marker;
                        _onLog?.Invoke($"\u001b[35m[cron fire] {job.Id} → {Trunc(job.Prompt, 40)}\u001b[0m");

                        if (!job.Recurring)
                        {
                            _jobs.TryRemove(job.Id, out _);
                            SaveDurable();
                        }
                    }
                    catch (Exception ex)
                    {
                        _onLog?.Invoke($"\u001b[31m[cron error] {job.Id}: {ex.Message}\u001b[0m");
                    }
                }
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"\u001b[31m[cron loop] {ex.GetType().Name}: {ex.Message}\u001b[0m");
            }
        }
    }

    private void SaveDurable()
    {
        try
        {
            var durable = _jobs.Values.Where(j => j.Durable).ToList();
            File.WriteAllText(_durablePath, JsonSerializer.Serialize(durable, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _onLog?.Invoke($"\u001b[31m[cron save] {ex.Message}\u001b[0m");
        }
    }

    private void LoadDurable()
    {
        if (!File.Exists(_durablePath)) return;
        try
        {
            var jobs = JsonSerializer.Deserialize<List<CronJob>>(File.ReadAllText(_durablePath));
            if (jobs is null) return;
            var loaded = 0;
            foreach (var j in jobs)
            {
                if (ValidateCron(j.Cron) is not null) continue;
                _jobs[j.Id] = j;
                loaded++;
            }
            if (loaded > 0) _onLog?.Invoke($"\u001b[35m[cron] loaded {loaded} durable job(s)\u001b[0m");
        }
        catch { /* ignore */ }
    }

    public void Dispose() => _cts.Cancel();

    public static bool CronMatches(string cronExpr, DateTime dt)
    {
        var fields = cronExpr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return false;
        var minute = fields[0];
        var hour = fields[1];
        var dom = fields[2];
        var month = fields[3];
        var dow = fields[4];
        var dowVal = (dt.DayOfWeek == DayOfWeek.Sunday) ? 0 : (int)dt.DayOfWeek;

        if (!FieldMatches(minute, dt.Minute, 0, 59)) return false;
        if (!FieldMatches(hour, dt.Hour, 0, 23)) return false;
        if (!FieldMatches(month, dt.Month, 1, 12)) return false;
        var domOk = FieldMatches(dom, dt.Day, 1, 31);
        var dowOk = FieldMatches(dow, dowVal, 0, 6);

        var domFree = dom == "*";
        var dowFree = dow == "*";
        if (domFree && dowFree) return true;
        if (domFree) return dowOk;
        if (dowFree) return domOk;
        return domOk || dowOk;
    }

    public static string? ValidateCron(string cronExpr)
    {
        var fields = cronExpr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return $"Expected 5 fields, got {fields.Length}";
        var bounds = new (int lo, int hi)[] { (0, 59), (0, 23), (1, 31), (1, 12), (0, 6) };
        var names = new[] { "minute", "hour", "day-of-month", "month", "day-of-week" };
        for (var i = 0; i < 5; i++)
        {
            var err = ValidateField(fields[i], bounds[i].lo, bounds[i].hi);
            if (err is not null) return $"{names[i]}: {err}";
        }
        return null;
    }

    private static string? ValidateField(string field, int lo, int hi)
    {
        if (field == "*") return null;
        if (field.StartsWith("*/"))
        {
            if (!int.TryParse(field[2..], out var s) || s <= 0) return $"Invalid step: {field}";
            return null;
        }
        if (field.Contains(','))
        {
            foreach (var p in field.Split(','))
            {
                var err = ValidateField(p.Trim(), lo, hi);
                if (err is not null) return err;
            }
            return null;
        }
        if (field.Contains('-'))
        {
            var parts = field.Split('-', 2);
            if (!int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b))
                return $"Invalid range: {field}";
            if (a < lo || a > hi || b < lo || b > hi || a > b) return $"Range {field} out of bounds";
            return null;
        }
        if (!int.TryParse(field, out var v)) return $"Invalid field: {field}";
        if (v < lo || v > hi) return $"Value {v} out of bounds";
        return null;
    }

    private static bool FieldMatches(string field, int value, int lo, int hi)
    {
        if (field == "*") return true;
        if (field.StartsWith("*/"))
        {
            if (!int.TryParse(field[2..], out var s) || s <= 0) return false;
            return value % s == 0;
        }
        if (field.Contains(','))
        {
            return field.Split(',').Any(p => FieldMatches(p.Trim(), value, lo, hi));
        }
        if (field.Contains('-'))
        {
            var parts = field.Split('-', 2);
            if (!int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b))
                return false;
            return value >= a && value <= b;
        }
        if (!int.TryParse(field, out var v)) return false;
        return v == value;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}

public static class CronTools
{
    public static void Register(ToolRegistry tools, CronScheduler scheduler)
    {
        tools.Register("schedule_cron", "Schedule a recurring or one-shot prompt using a 5-field cron expression.",
            SchemaBuilder.Object("Schedule a recurring or one-shot prompt using a 5-field cron expression.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["cron"] = ("string", "5-field cron expression: minute hour day-of-month month day-of-week", true),
                    ["prompt"] = ("string", "Prompt to inject when the cron fires", true),
                    ["recurring"] = ("boolean", "Repeat (default true)", false),
                    ["durable"] = ("boolean", "Persist to disk so it survives restart (default true)", false),
                }),
            input =>
            {
                var cron = input.GetProperty("cron").GetString() ?? "";
                var prompt = input.GetProperty("prompt").GetString() ?? "";
                var recurring = !(input.TryGetProperty("recurring", out var r) && r.ValueKind == JsonValueKind.False);
                var durable = !(input.TryGetProperty("durable", out var d) && d.ValueKind == JsonValueKind.False);
                return scheduler.Schedule(cron, prompt, recurring, durable);
            });

        tools.Register("list_crons", "List all registered cron jobs.",
            SchemaBuilder.Object("List all registered cron jobs.",
                new Dictionary<string, (string, string, bool)>()),
            _ =>
            {
                var jobs = scheduler.List();
                if (jobs.Count == 0) return "No cron jobs. Use schedule_cron to add one.";
                return string.Join("\n", jobs.Select(j =>
                    $"- {j.Id} '{j.Cron}' → {j.Prompt} [{(j.Recurring ? "recurring" : "one-shot")}{(j.Durable ? ", durable" : "")}]"));
            });

        tools.Register("cancel_cron", "Cancel a cron job by ID.",
            SchemaBuilder.Object("Cancel a cron job by ID.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["id"] = ("string", "Cron job ID", true),
                }),
            input =>
            {
                var id = input.GetProperty("id").GetString() ?? "";
                return scheduler.Cancel(id);
            });
    }
}
