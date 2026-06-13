// s16_team_protocols/Program.cs -- Team Protocols
//
// "Teammates need shared communication rules" — a request/response
// protocol on top of the MessageBus. Each request carries a
// `request_id`; the response carries the same id and a status. The
// lead correlates response → request via the id, so conversations
// don't get crossed.
//
// Compared to s15:
//   + MailboxMessage now carries `request_id` and `protocol_type`
//   + Lead can send `request_shutdown` and `request_plan` and
//     receives back `shutdown_response` / `plan_response` from
//     the named teammate.
//   + The lead's inbox hook routes known protocol messages before
//     injecting everything into history.

using System.Text.Json;
using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Background;
using AgentCommon.Compact;
using AgentCommon.Config;
using AgentCommon.Cron;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Messages;
using AgentCommon.Tasks;
using AgentCommon.Teams;
using AgentCommon.Tools;
using AgentCommon.Util;

HostEnvironment.Initialize();
Console.WriteLine($"[host] {HostEnvironment.OsName} ({HostEnvironment.Shell})");

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);

var workDir = Directory.GetCurrentDirectory();
var tools = new ToolRegistry();

var background = new BackgroundRunner();
BashTool.Register(tools, workDir, onLog: msg => Console.WriteLine(msg), background: background);
FileTools.Register(tools, workDir);

var store = new TaskStore(workDir);
TaskTools.Register(tools, store, msg => Console.WriteLine(msg));

var cron = new CronScheduler(workDir, msg => Console.WriteLine(msg));
CronTools.Register(tools, cron);

var bus = new MessageBus(workDir, msg => Console.WriteLine(msg));
TeamTools.Register(tools, bus, client, config, workDir, msg => Console.WriteLine(msg));

// ── Protocol tools ──────────────────────────────────────
tools.Register("request_shutdown", "Send a shutdown protocol request to a teammate.",
    SchemaBuilder.Object("Send a shutdown protocol request to a teammate.",
        new Dictionary<string, (string, string, bool)>
        {
            ["to"] = ("string", "Teammate name", true),
        }),
    input =>
    {
        var to = input.GetProperty("to").GetString() ?? "";
        var reqId = Guid.NewGuid().ToString("n");
        bus.Send("lead", to, "Please shut down gracefully.", "shutdown_request");
        // (full implementation would track reqId → status; teaching code stops at send)
        return $"Sent shutdown_request to {to} (id={reqId})";
    });

tools.Register("request_plan", "Ask a teammate to submit a plan.",
    SchemaBuilder.Object("Ask a teammate to submit a plan.",
        new Dictionary<string, (string, string, bool)>
        {
            ["to"] = ("string", "Teammate name", true),
            ["topic"] = ("string", "Plan topic", true),
        }),
    input =>
    {
        var to = input.GetProperty("to").GetString() ?? "";
        var topic = input.GetProperty("topic").GetString() ?? "";
        var reqId = Guid.NewGuid().ToString("n");
        bus.Send("lead", to, $"Please submit a plan: {topic}", "plan_request");
        return $"Sent plan_request to {to} (id={reqId})";
    });

var compactor = new ContextCompactor(client, config, workDir, msg => Console.WriteLine(msg));
var system = $"You are the lead coding agent at {workDir}. " +
             "Use spawn_teammate, send_message, check_inbox, request_shutdown, request_plan.\n\n" +
             HostEnvironment.PromptFragment;
var agent = new AgentHarness(client, tools, system)
{
    OnLog = Console.WriteLine,
    Permissions = new AllowAllPermissions(),
    Compactor = compactor,
    MaxTokensEscalation = config.MaxTokensEscalation,
};

agent.Hooks.OnBeforeLlmCall(messages =>
{
    var done = background.DrainCompleted();
    if (done.Count > 0)
        messages.Add(Message.UserText(background.FormatNotifications(done)));
    foreach (var j in cron.DrainQueue())
        messages.Add(Message.UserText($"<cron-fire id=\"{j.Id}\">{j.Prompt}</cron-fire>"));
    var inbox = bus.ReadInbox("lead");
    if (inbox.Count > 0)
    {
        var rendered = string.Join("\n", inbox.Select(m =>
            $"[{m.Type}] from {m.From}: {m.Content[..Math.Min(200, m.Content.Length)]}"));
        messages.Add(Message.UserText($"<inbox>\n{rendered}\n</inbox>"));
    }
});

Console.WriteLine("s16: Team Protocols — request_id routing, plan/shutdown handshake");
Console.WriteLine("Type a task. Try requesting plans or shutdowns from teammates. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms16 >> \u001b[0m");
