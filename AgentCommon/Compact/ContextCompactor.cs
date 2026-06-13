using System.Text;
using System.Text.Json;
using AgentCommon.Config;
using AgentCommon.Llm;
using AgentCommon.Messages;

namespace AgentCommon.Compact;

/// <summary>
/// Four-layer compaction pipeline (s08):
///   L1: SnipCompact       — drop middle messages when count > 50
///   L2: MicroCompact      — replace old tool_results with placeholders
///   L3: ToolResultBudget  — persist large results to disk
///   L4: CompactHistory    — LLM full summary (1 API call)
///
/// Plus an emergency reactive path for prompt_too_long errors.
/// Cheap first, expensive last.
/// </summary>
public sealed class ContextCompactor : IContextCompactor
{
    private readonly DeepSeekClient _client;
    private readonly AgentConfig _config;
    private readonly string _transcriptDir;
    private readonly string _toolResultsDir;
    private readonly int _maxMessages;
    private readonly int _keepHead;
    private readonly int _keepTail;
    private readonly int _keepRecentToolResults;
    private readonly int _persistThreshold;
    private readonly int _toolResultBudgetBytes;
    private readonly int _contextLimitChars;
    private readonly Action<string>? _onLog;

    public ContextCompactor(
        DeepSeekClient client,
        AgentConfig config,
        string workDir,
        Action<string>? onLog = null,
        int maxMessages = 50,
        int keepHead = 3,
        int keepRecentToolResults = 3,
        int persistThreshold = 30_000,
        int toolResultBudgetBytes = 200_000,
        int contextLimitChars = 50_000)
    {
        _client = client;
        _config = config;
        _transcriptDir = Path.Combine(workDir, ".transcripts");
        _toolResultsDir = Path.Combine(workDir, ".task_outputs", "tool-results");
        _maxMessages = maxMessages;
        _keepHead = keepHead;
        _keepTail = maxMessages - keepHead;
        _keepRecentToolResults = keepRecentToolResults;
        _persistThreshold = persistThreshold;
        _toolResultBudgetBytes = toolResultBudgetBytes;
        _contextLimitChars = contextLimitChars;
        _onLog = onLog;
    }

    public void PrepareBeforeLlm(List<Message> messages)
    {
        ToolResultBudget(messages);
        SnipCompact(messages);
        MicroCompact(messages);
    }

    public async Task<List<Message>> EmergencyAsync(List<Message> messages, CancellationToken ct = default)
    {
        var transcriptPath = WriteTranscript(messages);
        _onLog?.Invoke($"[transcript saved: {transcriptPath}]");
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

    // ── L3 ───────────────────────────────────────────────
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
            // ToolResultBlock is immutable; rebuild last message
            var idx = last.Content.IndexOf(block);
            if (idx >= 0)
            {
                last.Content[idx] = new ToolResultBlock(block.ToolUseId, persisted, block.IsError);
                total = last.Content.OfType<ToolResultBlock>().Sum(b => b.Content?.Length ?? 0);
            }
        }
    }

    private string PersistLargeOutput(string toolUseId, string output)
    {
        Directory.CreateDirectory(_toolResultsDir);
        var path = Path.Combine(_toolResultsDir, $"{toolUseId}.txt");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, output);
        }
        return $"<persisted-output>\nFull output: {path}\nPreview:\n{output[..Math.Min(2000, output.Length)]}\n</persisted-output>";
    }

    // ── L1 ───────────────────────────────────────────────
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

    // ── L2 ───────────────────────────────────────────────
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

    // ── L4 ───────────────────────────────────────────────
    private async Task<string> SummarizeHistoryAsync(List<Message> messages, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(messages, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        if (json.Length > 80_000) json = json[..80_000];

        var prompt =
            "Summarize this coding-agent conversation so work can continue.\n" +
            "Preserve: 1. current goal, 2. key findings/decisions, 3. files read/changed, " +
            "4. remaining work, 5. user constraints.\nBe compact but concrete.\n\n" + json;

        var resp = await _client.CreateMessageAsync(
            systemPrompt: "You are a concise summarizer.",
            messages: new List<Message> { Message.UserText(prompt) },
            tools: null,
            maxTokensOverride: 2000,
            ct: ct);

        var text = string.Join("\n", resp.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
        return string.IsNullOrEmpty(text) ? "(empty summary)" : text;
    }

    private string WriteTranscript(List<Message> messages)
    {
        Directory.CreateDirectory(_transcriptDir);
        var path = Path.Combine(_transcriptDir, $"transcript_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.jsonl");
        using var w = new StreamWriter(path, append: false, Encoding.UTF8);
        foreach (var m in messages)
        {
            var s = JsonSerializer.Serialize(m, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
            w.WriteLine(s);
        }
        return path;
    }

    private static bool MessageHasToolUse(Message m) =>
        m.Role == "assistant" && m.Content.OfType<ToolUseBlock>().Any();

    private static bool IsToolResultMessage(Message m) =>
        m.Role == "user" && m.Content.OfType<ToolResultBlock>().Any();
}
