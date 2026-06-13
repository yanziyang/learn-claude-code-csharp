using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCommon.Tools;

namespace AgentCommon.Tasks;

public sealed class TaskRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending"; // pending | in_progress | completed

    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    [JsonPropertyName("blockedBy")]
    public List<string> BlockedBy { get; set; } = new();
}

public sealed class TaskStore
{
    private readonly string _tasksDir;

    public TaskStore(string workDir)
    {
        _tasksDir = Path.Combine(workDir, ".tasks");
        Directory.CreateDirectory(_tasksDir);
    }

    public string TasksDir => _tasksDir;

    private string PathFor(string id) => Path.Combine(_tasksDir, $"{id}.json");

    public TaskRecord Load(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) throw new InvalidOperationException($"Task not found: {id}");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TaskRecord>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("Failed to load task");
    }

    public void Save(TaskRecord t)
    {
        File.WriteAllText(PathFor(t.Id), JsonSerializer.Serialize(t, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    public IReadOnlyList<TaskRecord> List()
    {
        var tasks = new List<TaskRecord>();
        foreach (var f in Directory.EnumerateFiles(_tasksDir, "task_*.json"))
        {
            try
            {
                tasks.Add(JsonSerializer.Deserialize<TaskRecord>(File.ReadAllText(f), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                })!);
            }
            catch { /* skip */ }
        }
        return tasks;
    }

    public bool CanStart(TaskRecord task)
    {
        foreach (var depId in task.BlockedBy)
        {
            var p = PathFor(depId);
            if (!File.Exists(p)) return false;
            var dep = Load(depId);
            if (dep.Status != "completed") return false;
        }
        return true;
    }

    public TaskRecord Create(string subject, string description = "", IEnumerable<string>? blockedBy = null)
    {
        var id = $"task_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(0, 9999):D4}";
        var task = new TaskRecord
        {
            Id = id,
            Subject = subject,
            Description = description,
            Status = "pending",
            BlockedBy = blockedBy?.ToList() ?? new(),
        };
        Save(task);
        return task;
    }

    public (bool ok, string message) Claim(string id, string owner = "agent")
    {
        var task = Load(id);
        if (task.Status != "pending")
            return (false, $"Task {id} is {task.Status}, cannot claim");
        if (!CanStart(task))
        {
            var deps = task.BlockedBy.Where(d => !File.Exists(PathFor(d)) || Load(d).Status != "completed").ToList();
            return (false, $"Blocked by: [{string.Join(", ", deps)}]");
        }
        task.Owner = owner;
        task.Status = "in_progress";
        Save(task);
        return (true, $"Claimed {task.Id} ({task.Subject})");
    }

    public (bool ok, string message, IReadOnlyList<TaskRecord> unblocked) Complete(string id)
    {
        var task = Load(id);
        if (task.Status != "in_progress")
            return (false, $"Task {id} is {task.Status}, cannot complete", Array.Empty<TaskRecord>());
        task.Status = "completed";
        Save(task);
        var unblocked = List()
            .Where(t => t.Status == "pending" && t.BlockedBy.Count > 0 && CanStart(t))
            .ToList();
        return (true, $"Completed {task.Id} ({task.Subject})", unblocked);
    }
}

public static class TaskTools
{
    public static void Register(ToolRegistry tools, TaskStore store, Action<string>? onLog = null)
    {
        tools.Register("create_task", "Create a new task in the task graph.",
            SchemaBuilder.Object("Create a new task in the task graph.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["subject"] = ("string", "Short title of the task", true),
                    ["description"] = ("string", "Detailed description of the task", false),
                    ["blockedBy"] = ("string", "JSON array of task IDs this task depends on", false),
                }),
            input =>
            {
                try
                {
                    var subject = input.GetProperty("subject").GetString() ?? "";
                    var desc = input.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    List<string> blockedBy = new();
                    if (input.TryGetProperty("blockedBy", out var b) && b.ValueKind == JsonValueKind.String)
                    {
                        var s = b.GetString() ?? "[]";
                        blockedBy = JsonSerializer.Deserialize<List<string>>(s) ?? new();
                    }
                    else if (input.TryGetProperty("blockedBy", out var ba) && ba.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in ba.EnumerateArray())
                        {
                            var v = el.GetString();
                            if (v is not null) blockedBy.Add(v);
                        }
                    }
                    var task = store.Create(subject, desc, blockedBy);
                    return $"Created {task.Id}: {task.Subject}";
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });

        tools.Register("list_tasks", "List all tasks in the task graph.",
            SchemaBuilder.Object("List all tasks in the task graph.",
                new Dictionary<string, (string, string, bool)>()),
            _ => JsonSerializer.Serialize(store.List(), new JsonSerializerOptions { WriteIndented = true }));

        tools.Register("get_task", "Get full details of a single task.",
            SchemaBuilder.Object("Get full details of a single task.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["id"] = ("string", "Task ID", true),
                }),
            input =>
            {
                var id = input.GetProperty("id").GetString() ?? "";
                try
                {
                    return JsonSerializer.Serialize(store.Load(id), new JsonSerializerOptions { WriteIndented = true });
                }
                catch (Exception ex) { return $"Error: {ex.Message}"; }
            });

        tools.Register("claim_task", "Claim a pending task to start working on it.",
            SchemaBuilder.Object("Claim a pending task to start working on it.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["id"] = ("string", "Task ID", true),
                    ["owner"] = ("string", "Owner name (default: agent)", false),
                }),
            input =>
            {
                var id = input.GetProperty("id").GetString() ?? "";
                var owner = input.TryGetProperty("owner", out var o) ? o.GetString() ?? "agent" : "agent";
                var (ok, msg) = store.Claim(id, owner);
                onLog?.Invoke(ok ? $"  \u001b[36m[claim] {msg}\u001b[0m" : $"  \u001b[31m[claim-fail] {msg}\u001b[0m");
                return msg;
            });

        tools.Register("complete_task", "Mark an in-progress task as completed.",
            SchemaBuilder.Object("Mark an in-progress task as completed.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["id"] = ("string", "Task ID", true),
                }),
            input =>
            {
                var id = input.GetProperty("id").GetString() ?? "";
                var (ok, msg, unblocked) = store.Complete(id);
                if (ok)
                {
                    onLog?.Invoke($"  \u001b[32m[complete] {msg}\u001b[0m");
                    if (unblocked.Count > 0)
                    {
                        var unblockMsg = "Unblocked: " + string.Join(", ", unblocked.Select(t => t.Subject));
                        onLog?.Invoke($"  \u001b[33m[unblocked] {unblockMsg}\u001b[0m");
                        return msg + "\n" + unblockMsg;
                    }
                }
                return msg;
            });
    }
}
