# s08: Context Compact — Context Will Fill Up, Have a Way to Make Room

[中文](README.md) · [English](README.en.md) · [日本語](README.ja.md)

s01 → s02 → s03 → s04 → s05 → s06 → s07 → `s08` → [s09](../s09_memory/) → s10 → ... → s20
> *"Context will fill up — have a way to make room"* — Four-layer compression pipeline: cheap first, expensive last.
>
> **Harness Layer**: Compression — clean memory, unlimited sessions.

---

## The Problem

The agent is running along, then freezes.

It has bash, read, write — all the capabilities it needs. But it read a 1000-line file (~4000 tokens), then read 30 more files, ran 20 commands. Every command's output, every file's contents, all pile up in the `messages` list.

The context window is finite. Once full, the API outright rejects the call: `prompt_too_long`.

Without compression, an agent simply cannot work on large projects.

---

## The Solution

![Compact Overview](images/compact-overview.en.svg)

The hook structure, skill loading, and sub-Agent from s07 are preserved, with some tools omitted to focus on compaction. The core change: insert three pre-processors (0 API calls) before each LLM call, trigger an LLM summary (1 API call) when tokens still exceed the threshold, and emergency-trim if the API throws an error.

Core design: cheap first, expensive last.

---

## How It Works

![Four-layer compression pipeline](images/compaction-layers.en.svg)

### L1: snip_compact — Trim Irrelevant Old Conversation

The agent ran 80 turns of conversation, accumulating 160 `messages`. The very first "help me create hello.py" is barely relevant to current work, yet it still occupies space.

Message count exceeds 50 → keep the first 3 (initial context) and the last 47 (current work), trim the middle; the only extra boundary rule is that `assistant(tool_use)` must not be separated from the following `user(tool_result)`:

```csharp
private void SnipCompact(List<Message> messages)
{
    if (messages.Count <= _maxMessages) return;
    var keepHead = _keepHead;
    var tailStart = messages.Count - _keepTail;

    if (keepHead > 0 && keepHead < messages.Count && MessageHasToolUse(messages[keepHead - 1]))
    {
        while (keepHead < messages.Count && IsToolResultMessage(messages[keepHead]))
        {
            keepHead++;
        }
    }
    if (tailStart > 0 && tailStart < messages.Count
        && IsToolResultMessage(messages[tailStart])
        && MessageHasToolUse(messages[tailStart - 1]))
    {
        tailStart--;
    }
    if (keepHead >= tailStart) return;

    var snipped = tailStart - keepHead;
    messages.RemoveRange(keepHead, snipped);
    messages.Insert(keepHead, Message.UserText($"[snipped {snipped} messages]"));
}
```

Messages are still trimmed directly; this just adds one boundary guard. `tool_result` content within remaining messages still keeps accumulating — message #34 may still hold 30KB of old file contents. → L2.

### L2: micro_compact — Placeholder for Old Tool Results

![Old results placeholder](images/micro-compact.en.svg)

The agent read 10 files consecutively. The full contents of reads 1–7 are still sitting in context, no longer needed, but hogging large amounts of space.

Keep only the 3 most recent `tool_result` entries intact; replace older ones with a one-line placeholder:

```csharp
private const int KEEP_RECENT_TOOL_RESULTS = 3;

private void MicroCompact(List<Message> messages)
{
    var allResults = new List<(int mi, int bi, ToolResultBlock block)>();
    for (var mi = 0; mi < messages.Count; mi++)
    {
        var m = messages[mi];
        if (m.Role != "user") continue;
        for (var bi = 0; bi < m.Content.Count; bi++)
        {
            if (m.Content[bi] is ToolResultBlock tr) allResults.Add((mi, bi, tr));
        }
    }
    if (allResults.Count <= _keepRecentToolResults) return;

    foreach (var (mi, bi, block) in allResults.Take(allResults.Count - _keepRecentToolResults))
    {
        if ((block.Content?.Length ?? 0) > 120)
        {
            var replaced = new ToolResultBlock(
                block.ToolUseId,
                "[Earlier tool result compacted. Re-run if needed.]",
                block.IsError);
            messages[mi].Content[bi] = replaced;
        }
    }
}
```

Old results are cleared, but a single new result can be 500KB — one `cat` of a large file can max out the context. → L3.

### L3: tool_result_budget — Persist Large Results to Disk

![Large results to disk](images/layer1-budget.en.svg)

The model read 5 large files in one go; all `tool_result` blocks in the last user message total 500KB.

Sum the size of all `tool_result` blocks in the last user message. If over 200KB → sort by size, starting from the largest, persist to `.task_outputs/tool-results/`, keeping only a `<persisted-output>` marker + a 2000-character preview in context. The model sees the marker and knows the full content is on disk, re-reading it when needed.

```csharp
private void ToolResultBudget(List<Message> messages)
{
    if (messages.Count == 0) return;
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
        var content = block.Content ?? "";
        if (content.Length <= _persistThreshold) continue;
        var tid = string.IsNullOrEmpty(block.ToolUseId) ? Guid.NewGuid().ToString("n") : block.ToolUseId;
        var persisted = PersistLargeOutput(tid, content);
        var idx = last.Content.IndexOf(block);
        if (idx >= 0)
        {
            last.Content[idx] = new ToolResultBlock(block.ToolUseId, persisted, block.IsError);
            total = last.Content.OfType<ToolResultBlock>().Sum(b => b.Content?.Length ?? 0);
        }
    }
}
```

The first three layers are all plain-text / structural operations — 0 API calls — but they cannot "understand" conversation content. Context may still be too large. → L4.

### L4: compact_history — Full LLM Summary

![Full LLM summary](images/auto-compact.en.svg)

All three previous layers have run, but after 30 minutes of continuous work on a huge project, tokens still exceed the threshold.

Three-step process:

1. **Save transcript**: Write the full conversation to `.transcripts/` in JSONL format. The transcript preserves a recoverable record, but the model's active context only contains the summary. For the model's current reasoning, the details are no longer in context. The teaching code does not provide a transcript retrieval tool.
2. **LLM generates summary**: Send conversation history to the LLM, asking it to preserve key information: current goals, important findings, modified files, remaining work, user constraints, etc.
3. **Replace message list**: All old messages are replaced with a single summary. The teaching version only keeps the summary; the real Claude Code re-attaches some recent files, plans, agent/skill/tool context after compaction.

```csharp
public async Task<List<Message>> CompactHistoryAsync(List<Message> messages, CancellationToken ct = default)
{
    var transcriptPath = WriteTranscript(messages);  // Save full conversation first
    var summary = await SummarizeHistoryAsync(messages, ct);  // LLM generates summary
    return new List<Message> { Message.UserText($"[Compacted]\n\n{summary}") };
}
```

**Circuit breaker**: After 3 consecutive failures, stop retrying to prevent an infinite loop wasting API calls.

### Reactive: reactive_compact

Sometimes the API still returns `prompt_too_long` (413) — when context grows faster than compression triggers.

This triggers **reactive_compact**: more aggressive than compact_history, it retreats from the tail, but still avoids leaving an orphaned `tool_result`.

```csharp
public async Task<List<Message>> EmergencyAsync(List<Message> messages, CancellationToken ct = default)
{
    var transcriptPath = WriteTranscript(messages);
    var summary = await SummarizeHistoryAsync(messages, ct);

    var tailStart = Math.Max(0, messages.Count - 5);
    if (tailStart > 0 && tailStart < messages.Count
        && IsToolResultMessage(messages[tailStart])
        && MessageHasToolUse(messages[tailStart - 1]))
    {
        tailStart -= 1;
    }
    return new List<Message> { Message.UserText($"[Reactive compact]\n\n{summary}") }
        .Concat(messages.Skip(tailStart))
        .ToList();
}
```

Reactive compact has a retry limit (default 1). If it still fails, an exception is raised instead of looping forever. Full error recovery is deferred to s11.

### Putting It All Together

```csharp
public async Task<LlmResponse> RunAsync(List<Message> messages, CancellationToken ct = default)
{
    // L1, L2, L3: 0 API calls
    Hooks.FireBeforeLlmCall(messages);
    Compactor.PrepareBeforeLlm(messages);

    var systemPrompt = SystemPromptProvider?.Invoke() ?? "";
    LlmResponse response;
    try
    {
        response = await Client.CreateMessageAsync(
            systemPrompt, messages, Tools.AllSpecs().ToList(), ct: ct);
    }
    catch (InvalidOperationException ex) when (IsPromptTooLong(ex))
    {
        // emergency
        messages.Clear();
        messages.AddRange(await Compactor.EmergencyAsync(messages, ct));
        response = await Client.CreateMessageAsync(
            systemPrompt, messages, Tools.AllSpecs().ToList(), ct: ct);
        // retry limit exceeded, raise exception
    }

    messages.Add(Message.Assistant(response.Content));

    if (response.StopReason != "tool_use") return response;

    // tool execution
    var results = new List<ToolResultBlock>();
    foreach (var block in response.Content.OfType<ToolUseBlock>())
    {
        OnLog?.Invoke($"\u001b[33m> {block.Name}\u001b[0m");
        var blocked = Hooks.FirePreToolUse(block);
        var output = blocked ?? Tools.Invoke(block.Name, block.Input);
        Hooks.FirePostToolUse(block, output);
        OnLog?.Invoke(output.Length > 200 ? output[..200] : output);
        results.Add(new ToolResultBlock(block.Id, output));
    }

    messages.Add(Message.UserToolResults(results));
    return response;
}
```

**The order must not be swapped.** L3 (budget) runs before L2 (micro) because micro replaces old large tool_results with one-line placeholders — budget must persist the full content before that happens. This is why CC source puts `applyToolResultBudget` first.

---

## Changes From s07

| Component | Before (s07) | After (s08) |
|-----------|-------------|-------------|
| Context management | None (context grows unbounded) | Four-layer compression pipeline + emergency |
| New functions | — | snip_compact, micro_compact, tool_result_budget, compact_history, reactive_compact |
| Tools | bash, read_file, write_file, edit_file, glob, todo_write, task, load_skill (8) | 8 + compact (9) |
| Loop | LLM call → tool execution | Three pre-processors before each turn + threshold-triggered compact_history |
| Design principle | — | Cheap first, expensive last |

---

## Try It

```sh
cd learn-claude-code
dotnet run --project s08_context_compact
```

Try these prompts:

1. `Read the file README.md, then read Program.cs, then read s01_agent_loop/README.md` (read multiple files consecutively, observe L2 compressing old results)
2. `Read every file in s08_context_compact/` (read a large amount of content at once, observe L3 persisting to disk)
3. Chat for 20+ turns, observe whether `[auto compact]` or `[reactive compact]` appears

What to watch for: After each tool execution, are old `tool_result` entries compressed? When tokens exceed the threshold after extended conversation, is summarization triggered automatically?

---

## What's Next

Context compression lets an agent run for a long time without crashing. But after each compression, the preferences and constraints the user told it are also lost. Can we let the agent selectively remember important things?

s09 Memory → three subsystems: choosing what to remember, extracting key information, consolidating and organizing. Across compressions, across sessions.

<details>
<summary>Deep Dive Into CC Source Code</summary>

> The following is based on analysis of CC source code `compact.ts`, `autoCompact.ts`, `microCompact.ts`, and `query.ts`.

### Execution Order Comparison

The teaching version labels layers L1/L2/L3/L4 for pedagogical clarity, but actual execution order does not match the numbering:

| Dimension | Teaching Version | Claude Code |
|-----------|-----------------|-------------|
| Execution order | budget → snip → micro → auto | budget → snip → micro → collapse → auto (`query.ts:379-468`) |
| snip_compact | Keep head 3 + tail 47 | CC only enables on main thread; implementation not in open-source repo (`HISTORY_SNIP` feature gate), but interface is visible: `snipCompactIfNeeded(messages)` → `{ messages, tokensFreed, boundaryMessage? }`, also exposes `SnipTool` for model-initiated snipping. Teaching version's 3/47 are simplified parameters |
| micro_compact | Text placeholder replacement | Two paths: time-based clears content directly, cached uses API `cache_edits` (legacy path removed) |
| micro_compact whitelist | By position (most recent 3) | time-based triggers by time threshold; cached triggers by count (`microCompact.ts`) |
| tool_result_budget | 200KB characters | 200,000 characters (`toolLimits.ts:49`) |
| compact_history threshold | Character count estimate | Precise tokens: `contextWindow - maxOutputTokens - 13_000` |
| Summary requirements | 5 categories of info | 9 sections + `<analysis>`/`<summary>` dual tags |
| Compression prompt | Simple prompt | Double-ended hard guardrails forbidding tool calls |
| PTL retry | Yes (simplified) | `truncateHeadForPTLRetry()` retreats by message groups (`compact.ts:243-290`) |
| Post-compaction recovery | None (teaching version only keeps summary) | Auto re-read recent files, plans, agent/skill/tool context |
| Circuit breaker | 3 times | 3 times (`autoCompact.ts:70`) |
| Reactive retry | 1 time | CC has more granular tiered retries |

### Execution Order Details

The real order in CC source `query.ts`:

1. `applyToolResultBudget` (L379): persist large results first, ensuring full content is saved
2. `snipCompact` (L403): trim middle messages
3. `microcompact` (L414): old result placeholders
4. `contextCollapse` (L441): independent context management system (not in teaching version)
5. `autoCompact` (L454): LLM full summary

The teaching version's budget → snip → micro order matches this. The teaching version does not have the contextCollapse mechanism.

### read_file Trade-off

The teaching version's `micro_compact` replaces old `tool_result` blocks with placeholders uniformly, including `read_file`. This usually does not affect functional correctness: if the model needs the file contents later, it can read the file again. The cost is an extra tool call and potentially lower prompt cache hit rates.

Claude Code does not solve this with the teaching version's simple rule. It also puts `Read` in the microcompactable tool set, but maintains a separate `readFileState`: repeated reads of unchanged files return `FILE_UNCHANGED_STUB`, and after compaction it restores recently read file contents within a budget (for example, up to 5 files, 5K tokens per file, 50K tokens total). That is a production-level cache and recovery mechanism. The teaching version does not expand into that machinery; it keeps the simpler trade-off of compacting old results and re-reading when needed.

### Full Constant Reference

| Constant | Value | Source File |
|----------|-------|-------------|
| `AUTOCOMPACT_BUFFER_TOKENS` | 13,000 | `autoCompact.ts:62` |
| `MAX_CONSECUTIVE_AUTOCOMPACT_FAILURES` | 3 | `autoCompact.ts:70` |
| `MAX_OUTPUT_TOKENS_FOR_SUMMARY` | 20,000 | `autoCompact.ts:30` |
| `POST_COMPACT_TOKEN_BUDGET` | 50,000 | `compact.ts:123` |
| `POST_COMPACT_MAX_FILES_TO_RESTORE` | 5 | `compact.ts:122` |
| `POST_COMPACT_MAX_TOKENS_PER_FILE` | 5,000 | `compact.ts:124` |
| Time micro_compact interval | 60 minutes | `timeBasedMCConfig.ts` |
| `MAX_COMPACT_STREAMING_RETRIES` | 2 | `compact.ts:131` |

### contextCollapse and sessionMemoryCompact

CC source code has two additional mechanisms not covered in this teaching version:

- **contextCollapse**: An independent context management system that, when enabled, suppresses proactive autocompact (`autoCompact.ts:215-222`), with collapse's commit/blocking flow taking over context management. Manual `/compact` and reactive fallback remain independent paths, unaffected by contextCollapse.
- **sessionMemoryCompact**: Before compact_history, CC first attempts a lightweight summary using existing session memory (covered in s09) without calling the LLM. This mechanism becomes clearer after learning s09.

### What Does the Compression Prompt Look Like?

CC's compression prompt has two hard requirements:

1. **Absolutely no tool calls**: It begins with `CRITICAL: Respond with TEXT ONLY. Do NOT call any tools.`, and appends another REMINDER at the end
2. **Analyze first, then summarize**: The model must first reason in an `<analysis>` tag, then output the formal summary in a `<summary>` tag. The analysis is stripped during formatting

### Teaching Version Simplifications Are Intentional

- micro_compact uses text placeholders → we don't have API-level `cache_edits` access
- read_file is not special-cased → the teaching version accepts re-reading when needed instead of introducing readFileState and post-compaction recovery
- Tokens estimated via character count → precise tokenizers are out of scope
- Post-compaction recovery omitted → teaching version only keeps summary, does not auto re-attach files
- Two auxiliary mechanisms not covered → they fall in the 10% detail category

The core design principle, cheap first, expensive last, is fully preserved.

</details>

<!-- translation-sync: zh@v2, en@v2, ja@v2 -->
