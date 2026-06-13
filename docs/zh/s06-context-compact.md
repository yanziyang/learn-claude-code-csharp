# s06: Context Compact (上下文压缩)

`s01 > s02 > s03 > s04 > s05 > [ s06 ] | s07 > s08 > s09 > s10 > s11 > s12`

> *"上下文总会满, 要有办法腾地方"* -- 四层压缩策略, 换来无限会话。
>
> **Harness 层**: 压缩 -- 干净的记忆, 无限的会话。

## 问题

上下文窗口是有限的。读一个 1000 行的文件就吃掉 ~4000 token; 读 30 个文件、跑 20 条命令, 轻松突破 100k token。不压缩, Agent 根本没法在大项目里干活。

## 解决方案

四层压缩, 激进程度递增 (`AgentCommon/Compact/ContextCompactor.cs`):

```
Every turn:
+------------------+
| Tool call result |
+------------------+
        |
        v
[L3: ToolResultBudget]    (silent, every turn)
  Persist large tool_results to .task_outputs/tool-results/
  Replace in-context copy with a placeholder + file path
        |
        v
[L1: SnipCompact]         (silent, every turn)
  Drop middle messages when count > 50
        |
        v
[L2: MicroCompact]        (silent, every turn)
  Replace tool_result > 3 turns old with "[Previous: used {tool_name}]"
        |
        v
[Check: tokens > 50000?]
   |               |
   no              yes
   |               |
   v               v
continue    [L4: CompactHistory]
            Save transcript to .transcripts/
            LLM summarizes conversation.
            Replace all messages with [summary] + tail.
```

## 工作原理

1. **L3 -- ToolResultBudget**: 将过大的 tool_result 持久化到磁盘, 上下文中的副本替换为占位符。

```csharp
private void ToolResultBudget(List<Message> messages)
{
    var last = messages[^1];
    if (last.Role != "user") return;
    var blocks = last.Content.OfType<ToolResultBlock>().ToList();
    if (blocks.Count == 0) return;

    long total = blocks.Sum(b => b.Content?.Length ?? 0);
    if (total <= _toolResultBudgetBytes) return;

    var ranked = blocks.OrderByDescending(b => b.Content?.Length ?? 0).ToList();
    foreach (var block in ranked)
    {
        if (total <= _toolResultBudgetBytes) break;
        var path = PersistToDisk(block.Content);   // 写入 .task_outputs/
        block.Content = $"[See {path} for full output]";
        total -= block.Content.Length;
    }
}
```

2. **L1 + L2 -- Snip + MicroCompact**: 每次 LLM 调用前执行。

```csharp
public void PrepareBeforeLlm(List<Message> messages)
{
    ToolResultBudget(messages);
    SnipCompact(messages);    // count > maxMessages 时切掉中间
    MicroCompact(messages);   // 旧 tool_result 替换为占位符
}
```

3. **L4 -- CompactHistory**: 模型返回 `prompt_too_long` 时, 把完整 transcript 写入磁盘, 让 LLM 做摘要。

```csharp
public async Task<List<Message>> EmergencyAsync(List<Message> messages, CancellationToken ct = default)
{
    var transcriptPath = WriteTranscript(messages);
    _onLog?.Invoke($"[transcript saved: {transcriptPath}]");
    var summary = await SummarizeHistoryAsync(messages, ct);

    var tailStart = Math.Max(0, messages.Count - 5);
    return new List<Message> { Message.UserText($"[Reactive compact]\n\n{summary}") }
        .Concat(messages.Skip(tailStart))
        .ToList();
}
```

4. 循环通过 `AgentHarness.RunAsync` 自动整合全部四层。

```csharp
// AgentHarness.RunAsync 内 -- 每次迭代调用:
Compactor.PrepareBeforeLlm(messages);

try
{
    response = await Client.CreateMessageAsync(systemPrompt, messages, ...);
}
catch (InvalidOperationException ex) when (IsPromptTooLong(ex))
{
    messages.Clear();
    messages.AddRange(await Compactor.EmergencyAsync(messages, ct));
    response = await Client.CreateMessageAsync(systemPrompt, messages, ...);
}
```

完整历史通过 transcript 保存在磁盘上。信息没有真正丢失, 只是移出了活跃上下文。

## 相对 s05 的变更

| 组件           | 之前 (s05)       | 之后 (s06)                     |
|----------------|------------------|--------------------------------|
| Tools          | 5                | 5 (unchanged)                  |
| 上下文管理     | 无               | Four-layer compression         |
| Micro-compact  | 无               | Old results -> placeholders    |
| Auto-compact   | 无               | Reactive path on overflow      |
| Transcripts    | 无               | Saved to .transcripts/         |

## 试一试

```sh
cd learn-claude-code-csharp
dotnet run --project s08_context_compact
```

试试这些 prompt (英文 prompt 对 LLM 效果更好, 也可以用中文):

1. `Read every C# file in the AgentCommon/ directory one by one` (观察 L2 micro-compact 替换旧结果)
2. `Keep reading files until compression triggers automatically`
3. `Force a long context to overflow and observe the L4 reactive compact`
