// s13_background_tasks/Program.cs -- Background Tasks
//
// "Slow ops go background, agent keeps thinking" — bash accepts a
// `run_in_background` flag. When set, the command is dispatched to a
// worker thread and the model gets a placeholder immediately. Completed
// tasks inject <task_notification> blocks into the next turn.
//
// Compared to s12:
//   + BackgroundRunner dispatches work to a thread pool
//   + BashTool accepts run_in_background=true
//   + AgentHarness.Hooks.OnBeforeLlmCall drains the runner and
//     appends notifications to messages before the next LLM call

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Background;
using AgentCommon.Compact;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Messages;
using AgentCommon.Tasks;
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

var compactor = new ContextCompactor(client, config, workDir, msg => Console.WriteLine(msg));
var system = $"You are a coding agent at {workDir}. " +
             "Use run_in_background=true on bash for slow operations (build, install, test).";
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

// NEW in s13: drain completed background tasks before each LLM call
agent.Hooks.OnBeforeLlmCall(messages =>
{
    var done = background.DrainCompleted();
    if (done.Count == 0) return;
    var notifications = background.FormatNotifications(done);
    messages.Add(Message.UserText(notifications));
    foreach (var bt in done)
    {
        Console.WriteLine($"  \u001b[32m[background done] {bt.BgId}: {bt.Command[..Math.Min(40, bt.Command.Length)]}\u001b[0m");
    }
});

Console.WriteLine("s13: Background Tasks — slow ops off the main thread");
Console.WriteLine("Type a task. Ask the model to run a slow command in the background. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms13 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
