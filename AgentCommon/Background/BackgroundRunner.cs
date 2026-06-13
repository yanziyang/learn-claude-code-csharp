using System.Collections.Concurrent;

namespace AgentCommon.Background;

public sealed class BackgroundTask
{
    public string BgId { get; }
    public string ToolUseId { get; }
    public string Command { get; }
    public Task<string> Running { get; }
    public DateTime StartedAt { get; }

    public BackgroundTask(string bgId, string toolUseId, string command, Task<string> running)
    {
        BgId = bgId;
        ToolUseId = toolUseId;
        Command = command;
        Running = running;
        StartedAt = DateTime.UtcNow;
    }
}

public sealed class BackgroundRunner
{
    private readonly ConcurrentDictionary<string, BackgroundTask> _tasks = new();
    private int _counter = 0;

    public string Start(string toolUseId, string command, Func<string> work)
    {
        var id = $"bg_{Interlocked.Increment(ref _counter):D4}";
        var task = Task.Run(work);
        _tasks[id] = new BackgroundTask(id, toolUseId, command, task);
        return id;
    }

    public IReadOnlyList<BackgroundTask> DrainCompleted()
    {
        var done = new List<BackgroundTask>();
        foreach (var (id, bt) in _tasks)
        {
            if (bt.Running.IsCompleted)
            {
                if (_tasks.TryRemove(id, out var removed))
                {
                    done.Add(removed);
                }
            }
        }
        return done;
    }

    public IReadOnlyCollection<BackgroundTask> Pending()
    {
        return _tasks.Values.Where(t => !t.Running.IsCompleted).ToList();
    }

    public string FormatNotifications(IReadOnlyList<BackgroundTask> done)
    {
        if (done.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var t in done)
        {
            string summary;
            try
            {
                var result = t.Running.Result;
                summary = result.Length > 200 ? result[..200] : result;
            }
            catch (Exception ex)
            {
                summary = $"Error: {ex.GetType().Name}: {ex.Message}";
            }
            sb.AppendLine($"<task_notification>");
            sb.AppendLine($"  <task_id>{t.BgId}</task_id>");
            sb.AppendLine($"  <status>completed</status>");
            sb.AppendLine($"  <command>{t.Command}</command>");
            sb.AppendLine($"  <summary>{summary}</summary>");
            sb.AppendLine($"</task_notification>");
        }
        return sb.ToString();
    }
}
