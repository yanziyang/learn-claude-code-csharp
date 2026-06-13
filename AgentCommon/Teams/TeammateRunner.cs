using System.Text.Json;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Messages;
using AgentCommon.Tools;
using AgentCommon.Util;

namespace AgentCommon.Teams;

public sealed class TeammateRunner
{
    private readonly DeepSeekClient _client;
    private readonly AgentConfig _config;
    private readonly string _workDir;
    private readonly MessageBus _bus;
    private readonly string _name;
    private readonly string _role;
    private readonly Action<string>? _onLog;
    private readonly int _maxRounds;

    public TeammateRunner(
        DeepSeekClient client,
        AgentConfig config,
        string workDir,
        MessageBus bus,
        string name,
        string role,
        Action<string>? onLog = null,
        int maxRounds = 10)
    {
        _client = client;
        _config = config;
        _workDir = workDir;
        _bus = bus;
        _name = name;
        _role = role;
        _onLog = onLog;
        _maxRounds = maxRounds;
    }

    public async Task RunAsync(string initialPrompt, CancellationToken ct = default)
    {
        var system = $"You are '{_name}', a {_role} at {_workDir}. " +
                     "Use your tools (bash, read_file, write_file, send_message) to complete the task. " +
                     "Send your final result via send_message to 'lead'.";

        var tools = new ToolRegistry();
        BashTool.Register(tools, _workDir, onLog: msg => _onLog?.Invoke(msg));
        FileTools.Register(tools, _workDir, onLog: msg => _onLog?.Invoke(msg));

        var sendSchema = SchemaBuilder.Object("Send a message to another agent.",
            new Dictionary<string, (string, string, bool)>
            {
                ["to"] = ("string", "Recipient agent name", true),
                ["content"] = ("string", "Message content", true),
            });
        tools.Register("send_message", "Send a message to another agent.", sendSchema, input =>
        {
            var to = input.GetProperty("to").GetString() ?? "";
            var content = input.GetProperty("content").GetString() ?? "";
            _bus.Send(_name, to, content);
            return "Sent";
        });

        var messages = new List<Message> { Message.UserText(initialPrompt) };

        for (var i = 0; i < _maxRounds; i++)
        {
            var inbox = _bus.ReadInbox(_name);
            if (inbox.Count > 0)
            {
                var json = JsonSerializer.Serialize(inbox, new JsonSerializerOptions { WriteIndented = true });
                messages.Add(Message.UserText($"<inbox>\n{json}\n</inbox>"));
            }

            LlmResponse resp;
            try
            {
                resp = await _client.CreateMessageAsync(
                    system, messages, tools.AllSpecs().ToList(), ct: ct);
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"\u001b[31m[teammate {_name}] LLM error: {ex.GetType().Name}\u001b[0m");
                break;
            }
            messages.Add(Message.Assistant(resp.Content));

            if (resp.StopReason != "tool_use") break;

            var results = new List<ToolResultBlock>();
            foreach (var block in resp.Content.OfType<ToolUseBlock>())
            {
                var output = tools.Invoke(block.Name, block.Input);
                _onLog?.Invoke($"  \u001b[90m[{_name}] {block.Name}: {Trunc(output, 100)}\u001b[0m");
                results.Add(new ToolResultBlock(block.Id, output));
            }
            messages.Add(Message.UserToolResults(results));
        }

        // Final summary to lead
        var lastText = "";
        foreach (var m in EnumerateReverse(messages))
        {
            if (m.Role != "assistant") continue;
            var t = string.Join("\n", m.Content.OfType<TextBlock>().Select(b => b.Text));
            if (!string.IsNullOrWhiteSpace(t)) { lastText = t; break; }
        }
        _bus.Send(_name, "lead", string.IsNullOrEmpty(lastText) ? $"[{_name} done]" : lastText, "completion");
        _onLog?.Invoke($"\u001b[35m[teammate done] {_name}\u001b[0m");
    }

    private static IEnumerable<Message> EnumerateReverse(List<Message> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--) yield return messages[i];
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
