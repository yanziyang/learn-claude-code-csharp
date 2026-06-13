# s08: Context Compact — 上下文总会满，要有办法腾地方

[中文](README.md) · [English](README.en.md) · [日本語](README.ja.md)

s01 → s02 → s03 → s04 → s05 → s06 → s07 → `s08` → [s09](../s09_memory/) → s10 → ... → s20
> *"上下文总会满, 要有办法腾地方"* — 四层压缩策略, 便宜的先跑贵的后跑。
>
> **Harness 层**: 压缩 — 干净的记忆, 无限的会话。

---

## 问题

Agent 跑着跑着，不动了。

手里有 bash、有 read、有 write，能力是够的。但它读了一个 1000 行的文件（~4000 token），又读了 30 个文件，跑了 20 条命令。每条命令的输出、每个文件的内容，全都堆在 `messages` 列表里。

上下文窗口是有限的。满了之后，API 直接拒绝：`prompt_too_long`。

不压缩，Agent 根本没法在大项目里干活。

---

## 解决方案

![Compact Overview](images/compact-overview.svg)

保留 s07 的 hook 结构、技能加载、子 Agent 等骨架，省略部分工具细节以聚焦压缩。核心变动：每轮 LLM 调用前插入三层预处理器（0 API），token 仍超阈值时触发 LLM 摘要（1 API），API 报错时应急裁剪。

核心设计：便宜的先跑，贵的后跑。

---

## 工作原理

![四层压缩管线](images/compaction-layers.svg)

### L1: snip_compact — 裁掉无关的旧对话

Agent 跑了 80 轮对话，`messages` 攒了 160 条。最前面的"帮我创建 hello.py"和当前工作几乎无关了，但全占着位置。

消息数超过 50 条 → 保留头部 3 条（初始上下文）和尾部 47 条（当前工作），中间裁掉；唯一额外边界条件是，不能把 `assistant(tool_use)` 和后面的 `user(tool_result)` 拆开：

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

裁掉的是消息本身，只是在切口处多做一步保护；剩下的消息里 `tool_result` 内容仍在累积——第 34 条消息里可能躺着 30KB 的旧文件内容。→ L2。

### L2: micro_compact — 旧工具结果占位

![旧结果占位](images/micro-compact.svg)

Agent 连续读了 10 个文件。第 1-7 次的完整内容还躺在上下文里，早就不需要了，但占着大量空间。

只保留最近 3 条 `tool_result` 的完整内容，更旧的替换为一行占位符：

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

旧结果清掉了，但单条新结果可能就有 500KB——一个 `cat` 大文件的输出就能打满上下文。→ L3。

### L3: tool_result_budget — 大结果落盘

![大结果落盘](images/layer1-budget.svg)

模型一次读了 5 个大文件，单条 user 消息里所有 `tool_result` 加起来 500KB。

统计最后一条 user 消息里所有 `tool_result` 的总大小。超过 200KB → 按大小排序，从最大的开始落盘到 `.task_outputs/tool-results/`，上下文里只留 `<persisted-output>` 标记 + 前 2000 字符预览。模型看到标记后知道完整内容在磁盘上，需要时可以重新读。

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

前三层都是纯文本/结构操作，0 API 调用，但也无法"理解"对话内容。上下文可能仍然太大。→ L4。

### L4: compact_history — LLM 全量摘要

![LLM 全量摘要](images/auto-compact.svg)

前三层全跑完了，但在超大项目中连续工作 30 分钟后，token 仍然超过阈值。

三步流程：

1. **保存 transcript**：完整对话写入 `.transcripts/`，JSONL 格式。transcript 保留了可恢复记录，但模型的活跃上下文里只剩摘要。对模型当下推理来说，细节已经不在上下文中了。教学代码没有提供 transcript 检索工具。
2. **LLM 生成摘要**：把对话历史发给 LLM，要求保留当前目标、重要发现、已改文件、剩余工作、用户约束等关键信息。
3. **替换消息列表**：所有旧消息被替换为一条摘要。教学版只保留摘要；真实 Claude Code 会在 compact 后重新附加部分最近文件、计划、agent/skill/tool 等上下文。

```csharp
public async Task<List<Message>> CompactHistoryAsync(List<Message> messages, CancellationToken ct = default)
{
    var transcriptPath = WriteTranscript(messages);  // Save full conversation first
    var summary = await SummarizeHistoryAsync(messages, ct);  // LLM generates summary
    return new List<Message> { Message.UserText($"[Compacted]\n\n{summary}") };
}
```

**熔断器**：连续失败 3 次后停止重试，防止死循环浪费 API 调用。

### 应急: reactive_compact

有时候 API 还是返回 `prompt_too_long`（413），上下文增长速度快于压缩触发速度时。

这时触发 **reactive_compact**：比 compact_history 更激进，从尾部回退，但仍要避免留下孤立 `tool_result`。

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

reactive compact 有重试上限（默认 1 次）。再失败就抛出异常，不无限循环。完整的错误恢复逻辑留给 s11。

### 合起来跑

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

**顺序不能换。** L3（budget）在 L2（micro）前面，因为 micro 会把旧的大 tool_result 替换成一行占位符，budget 必须在那之前把完整内容落盘。这也是为什么 CC 源码把 `applyToolResultBudget` 放在最前面。

---

## 相对 s07 的变更

| 组件 | 之前 (s07) | 之后 (s08) |
|------|-----------|-----------|
| 上下文管理 | 无（上下文无限膨胀） | 四层压缩管线 + 应急 |
| 新函数 | — | snip_compact, micro_compact, tool_result_budget, compact_history, reactive_compact |
| 工具 | bash, read, write, edit, glob, todo_write, task, load_skill (8) | 8 + compact (9) |
| 循环 | LLM 调用 → 工具执行 | 每轮前跑三层预处理器 + 阈值触发 compact_history |
| 设计原则 | — | 便宜的先跑，贵的后跑 |

---

## 试一下

```sh
cd learn-claude-code
dotnet run --project s08_context_compact
```

试试这些 prompt：

1. `Read the file README.md, then read Program.cs, then read s01_agent_loop/README.md`（连续读多个文件，观察 L2 压缩旧结果）
2. `Read every file in s08_context_compact/`（一次性读大量内容，观察 L3 落盘）
3. 反复对话 20+ 轮，观察是否出现 `[auto compact]` 或 `[reactive compact]`

观察重点：每次工具执行后，旧 tool_result 是否被压缩？连续对话后 token 超阈值时，是否自动触发了摘要？

---

## 接下来

上下文压缩让 Agent 能跑很久不会崩。但每次压缩后，用户之前告诉它的偏好、约束也跟着丢了。能不能让 Agent 有选择地记住重要的事？

s09 Memory → 三个子系统：选择记什么、提取关键信息、整理巩固。跨压缩、跨会话。

<details>
<summary>深入 CC 源码</summary>

> 以下基于 CC 源码 `compact.ts`、`autoCompact.ts`、`microCompact.ts`、`query.ts` 的分析。

### 执行顺序对照

教学版为了讲解方便按 L1/L2/L3/L4 编号，但实际执行顺序和编号不完全对应：

| 维度 | 教学版 | Claude Code |
|------|--------|-------------|
| 执行顺序 | budget → snip → micro → auto | budget → snip → micro → collapse → auto（`query.ts:379-468`） |
| snip_compact | 保留头 3 + 尾 47 | CC 仅主线程启用；实现不在开源仓库中（`HISTORY_SNIP` feature gate），但接口可见：`snipCompactIfNeeded(messages)` → `{ messages, tokensFreed, boundaryMessage? }`，还暴露了 `SnipTool` 工具让模型主动调用。教学版的 3/47 是简化参数 |
| micro_compact | 文本占位符替换 | 两条路径：time-based 直接清内容，cached 走 API `cache_edits`（legacy path 已移除） |
| micro_compact 白名单 | 按位置（最近 3 条） | time-based 按时间阈值触发；cached 按计数触发（`microCompact.ts`） |
| tool_result_budget | 200KB 字符 | 200,000 字符（`toolLimits.ts:49`） |
| compact_history 阈值 | 字符数估算 | 精确 token：`contextWindow - maxOutputTokens - 13_000` |
| 摘要要求 | 5 类信息 | 9 个部分 + `<analysis>`/`<summary>` 双标签 |
| 压缩 prompt | 简单 prompt | 首尾双重防呆禁止调工具 |
| PTL retry | 有（简化） | `truncateHeadForPTLRetry()` 按消息组回退（`compact.ts:243-290`） |
| 后压缩恢复 | 无（教学版只保留摘要） | 自动重新读取最近文件、计划、agent/skill/tool 等 |
| 熔断器 | 3 次 | 3 次（`autoCompact.ts:70`） |
| reactive 重试 | 1 次 | CC 有更精细的分级重试 |

### 执行顺序详解

CC 源码 `query.ts` 中的真实顺序：

1. `applyToolResultBudget`（L379）：先处理大结果，确保完整内容落盘
2. `snipCompact`（L403）：裁中间消息
3. `microcompact`（L414）：旧结果占位
4. `contextCollapse`（L441）：独立的上下文管理系统（教学版无）
5. `autoCompact`（L454）：LLM 全量摘要

教学版的 budget → snip → micro 顺序与此一致。教学版没有 contextCollapse 机制。

### read_file 的取舍

教学版的 `micro_compact` 会把旧 `tool_result` 统一替换成占位符，包括 `read_file`。这通常不影响功能正确性：如果后续还需要文件内容，模型可以重新读一次。代价是可能多一次工具调用，也可能降低 prompt cache 命中率。

Claude Code 没有用教学版这种简单规则解决这个问题。它把 `Read` 也放进可 microcompact 的工具集合，但同时维护 `readFileState`：重复读取未变化文件时返回 `FILE_UNCHANGED_STUB`，compact 后再按预算恢复最近读过的文件内容（例如最多 5 个文件、每个 5K token、总预算 50K token）。这是生产级实现里的缓存和恢复机制，教学版不展开，保留“压缩旧结果，必要时重新读取”的简单 trade-off。

### 完整常量参考

| 常量 | 值 | 源文件 |
|------|-----|--------|
| `AUTOCOMPACT_BUFFER_TOKENS` | 13,000 | `autoCompact.ts:62` |
| `MAX_CONSECUTIVE_AUTOCOMPACT_FAILURES` | 3 | `autoCompact.ts:70` |
| `MAX_OUTPUT_TOKENS_FOR_SUMMARY` | 20,000 | `autoCompact.ts:30` |
| `POST_COMPACT_TOKEN_BUDGET` | 50,000 | `compact.ts:123` |
| `POST_COMPACT_MAX_FILES_TO_RESTORE` | 5 | `compact.ts:122` |
| `POST_COMPACT_MAX_TOKENS_PER_FILE` | 5,000 | `compact.ts:124` |
| 时间 micro_compact 间隔 | 60 分钟 | `timeBasedMCConfig.ts` |
| `MAX_COMPACT_STREAMING_RETRIES` | 2 | `compact.ts:131` |

### contextCollapse 和 sessionMemoryCompact

CC 源码中还有两个机制本教学版没有展开：

- **contextCollapse**：独立的上下文管理系统，启用时抑制 proactive autocompact（`autoCompact.ts:215-222`），由 collapse 的 commit/blocking 流程接管上下文管理。但 manual `/compact` 和 reactive fallback 仍是独立路径，不受 contextCollapse 影响。
- **sessionMemoryCompact**：compact_history 之前，CC 会先尝试用已有的 session memory（s09 会讲到）做轻量摘要，不调 LLM。这个机制等学完 s09 之后回头看会更清楚。

### 压缩 prompt 长什么样？

CC 的压缩 prompt 有两个硬性要求：

1. **绝对禁止调用工具**：开头就是 `CRITICAL: Respond with TEXT ONLY. Do NOT call any tools.`，末尾还会再 REMINDER 一次
2. **先分析再总结**：模型需要先在 `<analysis>` 标签里理清思路，然后在 `<summary>` 标签里输出正式摘要。analysis 在格式化时被剥离

### 教学版的简化是刻意的

- micro_compact 用文本占位 → 我们没有 API 层的 `cache_edits` 权限
- read_file 不特殊处理 → 教学版接受必要时重新读取，避免引入 readFileState 和后压缩恢复机制
- token 用字符数估算 → 精确 tokenizer 不在教学范围内
- 后压缩恢复省略 → 教学版只保留摘要，不自动重新附加文件
- 两个辅助机制不展开 → 属于 10% 的细节

核心设计思想，便宜的先跑贵的后跑，完整保留。

</details>

<!-- translation-sync: zh@v2, en@v2, ja@v2 -->
