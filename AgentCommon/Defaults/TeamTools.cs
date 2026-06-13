using System.Text.Json;
using AgentCommon.Llm;
using AgentCommon.Teams;
using AgentCommon.Tools;

namespace AgentCommon.Defaults;

public static class TeamTools
{
    public static void Register(
        ToolRegistry tools,
        MessageBus bus,
        DeepSeekClient client,
        AgentCommon.Config.AgentConfig config,
        string workDir,
        Action<string>? onLog = null)
    {
        var activeNames = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        tools.Register("spawn_teammate", "Spawn a teammate agent in a background thread.",
            SchemaBuilder.Object("Spawn a teammate agent in a background thread.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["name"] = ("string", "Unique teammate name", true),
                    ["role"] = ("string", "Role / persona description", true),
                    ["prompt"] = ("string", "Initial task prompt", true),
                }),
            input =>
            {
                var name = input.GetProperty("name").GetString() ?? "";
                var role = input.GetProperty("role").GetString() ?? "";
                var prompt = input.GetProperty("prompt").GetString() ?? "";
                if (string.IsNullOrEmpty(name)) return "Error: name is required";
                if (!activeNames.TryAdd(name, 0)) return $"Teammate '{name}' already exists";

                var runner = new TeammateRunner(client, config, workDir, bus, name, role,
                    onLog: onLog, maxRounds: 10);
                var t = new System.Threading.Thread(async () =>
                {
                    try { await runner.RunAsync(prompt); }
                    finally { activeNames.TryRemove(name, out _); }
                }) { IsBackground = true, Name = $"teammate-{name}" };
                t.Start();
                return $"Spawned teammate '{name}' (role: {role})";
            });

        tools.Register("send_message", "Send a message to another agent.",
            SchemaBuilder.Object("Send a message to another agent.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["to"] = ("string", "Recipient agent name", true),
                    ["content"] = ("string", "Message content", true),
                }),
            input =>
            {
                var to = input.GetProperty("to").GetString() ?? "";
                var content = input.GetProperty("content").GetString() ?? "";
                bus.Send("lead", to, content);
                return "Sent";
            });

        tools.Register("check_inbox", "Drain and return the lead agent's inbox.",
            SchemaBuilder.Object("Drain and return the lead agent's inbox.",
                new Dictionary<string, (string, string, bool)>()),
            _ =>
            {
                var msgs = bus.ReadInbox("lead");
                if (msgs.Count == 0) return "(inbox empty)";
                return JsonSerializer.Serialize(msgs, new JsonSerializerOptions { WriteIndented = true });
            });
    }
}
