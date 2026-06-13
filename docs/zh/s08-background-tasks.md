# s08: Background Tasks (后台任务)

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > [ s08 ] s09 > s10 > s11 > s12`

> *"慢操作丢后台, agent 继续想下一步"* -- 后台任务跑命令, 完成后注入通知。
>
> **Harness 层**: 后台执行 -- 模型继续思考, harness 负责等待。

## 问题

有些命令要跑好几分钟: `dotnet build`、`dotnet test`、`docker build`。阻塞式循环下模型只能干等。用户说 "装依赖, 顺便建个配置文件", Agent 却只能一个一个来。

## 解决方案

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

## 工作原理

1. `BackgroundRunner` 用线程安全的通知队列追踪任务 (`AgentCommon/Background/BackgroundRunner.cs`)。

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

2. `bash` 工具可通过 `run_in_background=true` 把命令派发到后台。

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

3. 每次 LLM 调用前, 循环排空已完成的后台任务, 把通知注入上下文。

```csharp
// OnBeforeLlmCall 钩子内 (接线见 s20):
var done = background.DrainCompleted();
if (done.Count > 0)
    messages.Add(Message.UserText(background.FormatNotifications(done)));
```

4. `FormatNotifications` 把排空结果渲染成模型能解析的 XML。

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

循环保持单线程。只有子进程 I/O 被并行化。

## 相对 s07 的变更

| 组件           | 之前 (s07)       | 之后 (s08)                         |
|----------------|------------------|------------------------------------|
| Tools          | 8                | 同样工具; bash 新增 `run_in_background` |
| 执行方式       | 仅阻塞           | 阻塞 + 后台任务                    |
| 通知机制       | 无               | 每轮排空的队列                     |
| 并发           | 无               | `Task.Run` workers                 |

## 试一试

```sh
cd learn-claude-code-csharp
dotnet run --project s13_background_tasks
```

试试这些 prompt (英文 prompt 对 LLM 效果更好, 也可以用中文):

1. `Run "sleep 5 && echo done" in the background, then create a file while it runs`
2. `Start 3 background tasks: "sleep 2", "sleep 4", "sleep 6". Check their status.`
3. `Run dotnet build in the background and keep working on other things`
