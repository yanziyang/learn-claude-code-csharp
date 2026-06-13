# s08: Background Tasks

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > [ s08 ] s09 > s10 > s11 > s12`

> *"遅い操作はバックグラウンドへ、エージェントは次を考え続ける"* -- バックグラウンドタスクがコマンド実行、完了後に通知を注入。
>
> **Harness 層**: バックグラウンド実行 -- モデルが考え続ける間、Harness が待つ。

## 問題

一部のコマンドは数分かかる: `dotnet build`、`dotnet test`、`docker build`。ブロッキングループでは、モデルはサブプロセスの完了を待って座っている。ユーザーが「依存関係をインストールして、その間にconfigファイルを作って」と言っても、エージェントは並列ではなく逐次的に処理する。

## 解決策

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

## 仕組み

1. `BackgroundRunner` がスレッドセーフな通知キューでタスクを追跡する (`AgentCommon/Background/BackgroundRunner.cs`)。

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

2. `bash` ツールは `run_in_background=true` を渡すことでコマンドをバックグラウンドにディスパッチできる。

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

3. エージェントループは各LLM呼び出しの前に完了済みバックグラウンドタスクをドレインし、通知を注入する。

```csharp
// OnBeforeLlmCall フック内 (配線は s20 参照):
var done = background.DrainCompleted();
if (done.Count > 0)
    messages.Add(Message.UserText(background.FormatNotifications(done)));
```

4. `FormatNotifications` がドレイン結果をモデルが解釈可能な XML にレンダリングする。

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

ループはシングルスレッドのまま。サブプロセスI/Oだけが並列化される。

## s07からの変更点

| Component      | Before (s07)     | After (s08)                |
|----------------|------------------|----------------------------|
| Tools          | 8                | 同じツール; bash が `run_in_background` を取得 |
| Execution      | Blocking only    | Blocking + background tasks|
| Notification   | None             | Queue drained per loop     |
| Concurrency    | None             | `Task.Run` workers         |

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s13_background_tasks
```

1. `Run "sleep 5 && echo done" in the background, then create a file while it runs`
2. `Start 3 background tasks: "sleep 2", "sleep 4", "sleep 6". Check their status.`
3. `Run dotnet build in the background and keep working on other things`
