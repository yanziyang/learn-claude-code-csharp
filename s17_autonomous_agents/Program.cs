// s17_autonomous_agents/Program.cs -- Autonomous Agents
//
// "Teammates check the board, claim work themselves" — an idle cycle
// that scans the task board, claims an unowned task whose deps are
// done, then runs to completion. No leader assigns work; the team
// self-organizes.
//
// Compared to s16:
//   + AutonomousTeammate: WORK → IDLE → claim → WORK loop
//   + TaskStore exposes unclaimed pending tasks
//   + A daemon "scout" polls every 5s; when it finds an unclaimed
//     task whose deps are done, it spawns a worker teammate

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

// ── Autonomous scout: poll task board, spawn a worker for each
//    unclaimed task whose deps are done.
var cts = new CancellationTokenSource();
var scout = new Thread(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            var candidate = store.List().FirstOrDefault(t =>
                t.Status == "pending"
                && string.IsNullOrEmpty(t.Owner)
                && store.CanStart(t));
            if (candidate is not null)
            {
                var name = $"worker_{candidate.Id}";
                var prompt = $"Take ownership of task {candidate.Id}: {candidate.Subject}. " +
                             $"Description: {candidate.Description}. Complete it and report back.";
                var runner = new TeammateRunner(client, config, workDir, bus, name, "worker",
                    onLog: msg => Console.WriteLine(msg), maxRounds: 12);
                var claim = store.Claim(candidate.Id, owner: name);
                Console.WriteLine($"\u001b[36m[scout] {claim}\u001b[0m");
                var t = new Thread(async () =>
                {
                    try { await runner.RunAsync(prompt, cts.Token); }
                    finally
                    {
                        var (ok, _, _) = store.Complete(candidate.Id);
                        if (ok) Console.WriteLine($"\u001b[32m[scout] auto-completed {candidate.Id}\u001b[0m");
                    }
                }) { IsBackground = true, Name = name };
                t.Start();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\u001b[31m[scout] {ex.GetType().Name}: {ex.Message}\u001b[0m");
        }
        try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); } catch { break; }
    }
}) { IsBackground = true, Name = "autonomous-scout" };
scout.Start();

var compactor = new ContextCompactor(client, config, workDir, msg => Console.WriteLine(msg));
var system = $"You are the lead coding agent at {workDir}. " +
             "Create tasks; the scout will spawn workers automatically.";
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
        messages.Add(Message.UserText("<inbox>\n" + JsonSerializer.Serialize(inbox, new JsonSerializerOptions { WriteIndented = true }) + "\n</inbox>"));
});

Console.WriteLine("s17: Autonomous Agents — scout polls, workers self-claim");
Console.WriteLine("Create some tasks; the scout will dispatch workers automatically. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms17 >> \u001b[0m");
cts.Cancel();
