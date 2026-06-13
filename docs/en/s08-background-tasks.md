# s08: Background Tasks

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > [ s08 ] s09 > s10 > s11 > s12`

> *"Run slow operations in the background; the agent keeps thinking"* -- background tasks run commands, inject notifications on completion.
>
> **Harness layer**: Background execution -- the model thinks while the harness waits.

## Problem

Some commands take minutes: `dotnet build`, `dotnet test`, `docker build`. With a blocking loop, the model sits idle waiting. If the user asks "install dependencies and while that runs, create the config file," the agent does them sequentially, not in parallel.

## Solution

```
Main thread                Background threads
+-----------------+        +-----------------+
| agent loop      |        | Task.Run(work)  |
| ...             |        | ...             |
| [LLM call] <---+------- | enqueue(result) |
|  ^drain queue   |        +-----------------+
+-----------------+

Timeline:
Agent --[spawn A]--[spawn B]--[other work]----
             |          |
             v          v
          [A runs]   [B runs]      (parallel)
             |          |
             +-- results injected before next LLM call --+
```

## How It Works

1. `BackgroundRunner` tracks tasks with a thread-safe notification queue (`AgentCommon/Background/BackgroundRunner.cs`).

```csharp
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
}
```

2. The `bash` tool can dispatch a command into the background by passing `run_in_background=true`.

```csharp
if (runInBackground && background is not null)
{
    var bgId = background.Start(toolUseId, cmd, () =>
    {
        var r = BashRunner.Run(cmd, workDir);
        return (r.StdOut + r.StdErr).Trim();
    });
    return $"<background-task id=\"{bgId}\" status=\"running\"/>\n" +
           $"Command dispatched in background. You will be notified on completion.";
}
```

3. The agent loop drains completed background tasks before each LLM call and injects notifications.

```csharp
// In an OnBeforeLlmCall hook (see s20 for the wiring):
var done = background.DrainCompleted();
if (done.Count > 0)
    messages.Add(Message.UserText(background.FormatNotifications(done)));
```

4. `FormatNotifications` renders the drained results as XML the model can parse.

```csharp
public string FormatNotifications(IReadOnlyList<BackgroundTask> done)
{
    var sb = new StringBuilder();
    foreach (var t in done)
    {
        var summary = SafeResult(t);
        sb.AppendLine("<task_notification>");
        sb.AppendLine($"  <task_id>{t.BgId}</task_id>");
        sb.AppendLine($"  <status>completed</status>");
        sb.AppendLine($"  <command>{t.Command}</command>");
        sb.AppendLine($"  <summary>{summary}</summary>");
        sb.AppendLine("</task_notification>");
    }
    return sb.ToString();
}
```

The loop stays single-threaded. Only subprocess I/O is parallelized.

## What Changed From s07

| Component      | Before (s07)     | After (s08)                |
|----------------|------------------|----------------------------|
| Tools          | 8                | Same tools; bash gains `run_in_background` |
| Execution      | Blocking only    | Blocking + background tasks|
| Notification   | None             | Queue drained per loop     |
| Concurrency    | None             | `Task.Run` workers         |

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s13_background_tasks
```

1. `Run "sleep 5 && echo done" in the background, then create a file while it runs`
2. `Start 3 background tasks: "sleep 2", "sleep 4", "sleep 6". Check their status.`
3. `Run dotnet build in the background and keep working on other things`
