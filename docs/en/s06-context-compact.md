# s06: Context Compact

`s01 > s02 > s03 > s04 > s05 > [ s06 ] | s07 > s08 > s09 > s10 > s11 > s12`

> *"Context will fill up; you need a way to make room"* -- four-layer compression strategy for infinite sessions.
>
> **Harness layer**: Compression -- clean memory for infinite sessions.

## Problem

The context window is finite. A single `read_file` on a 1000-line file costs ~4000 tokens. After reading 30 files and running 20 bash commands, you hit 100,000+ tokens. The agent cannot work on large codebases without compression.

## Solution

Four layers, increasing in aggressiveness (`AgentCommon/Compact/ContextCompactor.cs`):

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

## How It Works

1. **L3 -- ToolResultBudget**: Persist oversized tool results to disk, replace in-context with a placeholder.

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
        var path = PersistToDisk(block.Content);   // write to .task_outputs/
        block.Content = $"[See {path} for full output]";
        total -= block.Content.Length;
    }
}
```

2. **L1 + L2 -- Snip + MicroCompact**: Run before each LLM call.

```csharp
public void PrepareBeforeLlm(List<Message> messages)
{
    ToolResultBudget(messages);
    SnipCompact(messages);    // drop middle when count > maxMessages
    MicroCompact(messages);   // replace old tool_results with placeholders
}
```

3. **L4 -- CompactHistory**: When the model reports `prompt_too_long`, save the full transcript to disk and ask the LLM to summarize.

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

4. The loop integrates all four layers automatically through `AgentHarness.RunAsync`.

```csharp
// In AgentHarness.RunAsync — called every iteration:
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

Transcripts preserve full history on disk. Nothing is truly lost -- just moved out of active context.

## What Changed From s05

| Component      | Before (s05)     | After (s06)                |
|----------------|------------------|----------------------------|
| Tools          | 5                | 5 (unchanged)              |
| Context mgmt   | None             | Four-layer compression     |
| Micro-compact  | None             | Old results -> placeholders|
| Auto-compact   | None             | Reactive path on overflow  |
| Transcripts    | None             | Saved to .transcripts/     |

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s08_context_compact
```

1. `Read every C# file in the AgentCommon/ directory one by one` (watch L2 micro-compact replace old results)
2. `Keep reading files until compression triggers automatically`
3. `Force a long context to overflow and observe the L4 reactive compact`
