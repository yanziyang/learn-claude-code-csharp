// s12_task_system/Program.cs -- Task System
//
// "Big goals break into small tasks, ordered, persisted to disk" —
// a file-backed task graph with blockedBy dependencies.
//
// Compared to s11:
//   + TaskStore: JSON files under .tasks/
//   + 5 new tools: create_task, list_tasks, get_task, claim_task, complete_task
//   + CanStart respects blockedBy (missing deps are blocked)

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Compact;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Tasks;
using AgentCommon.Tools;
using AgentCommon.Util;

HostEnvironment.Initialize();
Console.WriteLine($"[host] {HostEnvironment.OsName} ({HostEnvironment.Shell})");

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);

var workDir = Directory.GetCurrentDirectory();
var tools = new ToolRegistry();
BashTool.Register(tools, workDir);
FileTools.Register(tools, workDir);

var store = new TaskStore(workDir);
TaskTools.Register(tools, store, msg => Console.WriteLine(msg));   // NEW in s12

var compactor = new ContextCompactor(client, config, workDir, msg => Console.WriteLine(msg));
var system = $"You are a coding agent at {workDir}. " +
             "Plan work with create_task, claim_task, complete_task.\n\n" +
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

Console.WriteLine("s12: Task System — file-backed task graph with blockedBy");
Console.WriteLine("Tasks persist in .tasks/. Type a multi-step task and watch the graph grow. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms12 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
