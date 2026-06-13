// s06_subagent/Program.cs -- Subagent
//
// "Big tasks split small, each subtask gets clean context" — the model
// can call `task` to launch a sub-agent. The sub-agent runs with a fresh
// message list and a trimmed tool set (no `task` itself, to prevent
// recursive spawning), and only the final text returns to the parent.

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Subagent;
using AgentCommon.Tools;
using AgentCommon.Util;

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);

var workDir = Directory.GetCurrentDirectory();

// ── Parent tools ─────────────────────────────────────────
var parentTools = new ToolRegistry();
BashTool.Register(parentTools, workDir);
FileTools.Register(parentTools, workDir);
var todos = new TodoTools.TodoState();
TodoTools.Register(parentTools, todos);

// ── Sub-agent tools (no `task` to prevent recursion) ────
var subTools = new ToolRegistry();
BashTool.Register(subTools, workDir);
FileTools.Register(subTools, workDir);

// ── Sub-agent factory: returns a SubagentRunner that runs the
//    shared loop with the sub-tool set and a sub-system prompt. ──
SubagentRunner SpawnSub() => new(
    client, config, subTools,
    $"You are a focused sub-agent at {workDir}. Complete the given task and return a concise final answer.",
    msg => Console.WriteLine(msg));

// Register `task` tool on the parent
TaskTool.Register(parentTools, SpawnSub);

var system = $"You are a coding agent at {workDir}. Use tools to solve tasks. Act, don't explain. " +
             "Delegate focused subtasks via the `task` tool.";
var agent = new AgentHarness(client, parentTools, system)
{
    OnLog = Console.WriteLine,
    Permissions = new AllowAllPermissions(),
};

// Minimal hook log
agent.Hooks.OnUserPromptSubmit(_ =>
    Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit: cwd={workDir}\u001b[0m"));
agent.Hooks.OnPreToolUse(block =>
{
    var args = string.Join(", ", block.Input.EnumerateObject().Take(2).Select(o => $"{o.Name}={Trunc(o.Value.ToString())}"));
    Console.WriteLine($"\u001b[90m[HOOK] {block.Name}({args})\u001b[0m");
    return null;
});

Console.WriteLine("s06: Subagent — `task` tool spawns focused sub-agents");
Console.WriteLine("Type a task and press Enter. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms06 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
