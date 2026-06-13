// s15_agent_teams/Program.cs -- Agent Teams
//
// "Too big for one agent -- delegate to teammates" — a file-based
// MessageBus and background-thread teammates. Lead has 3 new tools:
//   spawn_teammate  - start a teammate on a thread
//   send_message    - send a message via the bus
//   check_inbox     - drain the lead's inbox
//
// Compared to s14:
//   + MessageBus  (file-based .mailboxes/*.jsonl)
//   + TeammateRunner (background thread, fixed 10-round loop)
//   + 3 new tools on the lead
//   + BeforeLlmCall hook injects inbox into messages

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

var compactor = new ContextCompactor(client, config, workDir, msg => Console.WriteLine(msg));
var system = $"You are the lead coding agent at {workDir}. " +
             "You can spawn_teammate, send_message, check_inbox. " +
             "Delegate parallel work to teammates via the message bus.\n\n" +
             HostEnvironment.PromptFragment;
var agent = new AgentHarness(client, tools, system)
{
    OnLog = Console.WriteLine,
    Permissions = new AllowAllPermissions(),
    Compactor = compactor,
    MaxTokensEscalation = config.MaxTokensEscalation,
};

agent.Hooks.OnUserPromptSubmit(_ =>
    Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit: cwd={workDir}\u001b[0m"));
agent.Hooks.OnPreToolUse(block =>
{
    var args = string.Join(", ", block.Input.EnumerateObject().Take(2).Select(o => $"{o.Name}={Trunc(o.Value.ToString())}"));
    Console.WriteLine($"\u001b[90m[HOOK] {block.Name}({args})\u001b[0m");
    return null;
});

// Drain background + cron + inbox before each LLM call
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
        messages.Add(Message.UserText("<inbox>\n" + JsonSerializer.Serialize(inbox, new JsonSerializerOptions { WriteIndented = true }) + "\n</inbox>"));
    }
});

Console.WriteLine("s15: Agent Teams — MessageBus, teammates on threads");
Console.WriteLine("Type a task. Try spawning a teammate. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms15 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
