using AgentCommon.Config;
using AgentCommon.Llm;
using AgentCommon.Messages;
using AgentCommon.Tools;

namespace AgentCommon.Subagent;

/// <summary>
/// A "task" tool handler: spawns a sub-agent with a fresh message list,
/// runs the loop to completion, and returns only the final text summary.
/// </summary>
public sealed class SubagentRunner
{
    private readonly DeepSeekClient _client;
    private readonly AgentConfig _config;
    private readonly ToolRegistry _tools;
    private readonly string _systemPrompt;
    private readonly Action<string>? _onLog;
    private readonly int _maxIterations;

    public SubagentRunner(
        DeepSeekClient client,
        AgentConfig config,
        ToolRegistry tools,
        string systemPrompt,
        Action<string>? onLog = null,
        int maxIterations = 30)
    {
        _client = client;
        _config = config;
        _tools = tools;
        _systemPrompt = systemPrompt;
        _onLog = onLog;
        _maxIterations = maxIterations;
    }

    public async Task<string> RunAsync(string description, CancellationToken ct = default)
    {
        _onLog?.Invoke("\n\u001b[35m[Subagent spawned]\u001b[0m");
        var messages = new List<Message> { Message.UserText(description) };

        LlmResponse? last = null;
        for (var i = 0; i < _maxIterations; i++)
        {
            last = await _client.CreateMessageAsync(
                _systemPrompt, messages, _tools.AllSpecs().ToList(), ct: ct);
            messages.Add(Message.Assistant(last.Content));

            if (last.StopReason != "tool_use")
            {
                break;
            }

            var results = new List<ToolResultBlock>();
            foreach (var block in last.Content.OfType<ToolUseBlock>())
            {
                var output = _tools.Invoke(block.Name, block.Input);
                _onLog?.Invoke($"  \u001b[90m[sub] {block.Name}: {(output.Length > 100 ? output[..100] : output)}\u001b[0m");
                results.Add(new ToolResultBlock(block.Id, output));
            }
            messages.Add(Message.UserToolResults(results));
        }

        // Walk back to find the most recent assistant text
        if (last is not null)
        {
            foreach (var b in last.Content.OfType<TextBlock>())
            {
                _onLog?.Invoke("\u001b[35m[Subagent done]\u001b[0m");
                return b.Text;
            }
        }
        foreach (var msg in EnumerateReverse(messages))
        {
            if (msg.Role != "assistant") continue;
            var text = string.Join("\n", msg.Content.OfType<TextBlock>().Select(t => t.Text));
            if (!string.IsNullOrWhiteSpace(text))
            {
                _onLog?.Invoke("\u001b[35m[Subagent done]\u001b[0m");
                return text;
            }
        }

        _onLog?.Invoke("\u001b[35m[Subagent done]\u001b[0m");
        return $"Subagent stopped after {_maxIterations} turns without final answer.";
    }

    private static IEnumerable<Message> EnumerateReverse(List<Message> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--) yield return messages[i];
    }
}
