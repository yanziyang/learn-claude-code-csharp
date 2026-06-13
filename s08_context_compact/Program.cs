// s08_context_compact/Program.cs -- Context Compact
//
// "Context always fills up -- have a way to make room" — four-layer
// compaction pipeline inserted before every LLM call.
//
//   L1: SnipCompact       — trim middle messages when count > 50
//   L2: MicroCompact      — replace old tool_results with placeholders
//   L3: ToolResultBudget  — persist large results to disk
//   L4: CompactHistory    — LLM full summary (1 API call)
//
// Plus an emergency reactive path for prompt_too_long errors.
//
// Compared to s07:
//   + ContextCompactor  (the four layers, plus emergency reactive path)
//   + IContextCompactor plugged into AgentHarness.Compactor
//   Sub-agent, skills, todo_write, hooks: all unchanged from s07.

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Compact;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Skills;
using AgentCommon.Subagent;
using AgentCommon.Tools;
using AgentCommon.Util;

HostEnvironment.Initialize();
Console.WriteLine($"[host] {HostEnvironment.OsName} ({HostEnvironment.Shell})");

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);

var workDir = Directory.GetCurrentDirectory();
var skillsDir = Path.GetFullPath(Path.Combine(workDir, "..", "skills"));
var skills = SkillRegistry.LoadFromDir(skillsDir);

var parentTools = new ToolRegistry();
BashTool.Register(parentTools, workDir);
FileTools.Register(parentTools, workDir);
var todos = new TodoTools.TodoState();
TodoTools.Register(parentTools, todos);
SkillTools.Register(parentTools, skills);

var subTools = new ToolRegistry();
BashTool.Register(subTools, workDir);
FileTools.Register(subTools, workDir);

SubagentRunner SpawnSub() => new(
    client, config, subTools,
    $"You are a focused sub-agent at {workDir} on {HostEnvironment.OsName} ({HostEnvironment.Shell}). " +
    "Complete the given task and return a concise final answer.\n\n" +
    HostEnvironment.PromptFragment,
    msg => Console.WriteLine(msg));
TaskTool.Register(parentTools, SpawnSub);

var system =
    $"You are a coding agent at {workDir}. " +
    "Skills available:\n" + skills.Catalog() + "\n" +
    "Use load_skill to get full details when needed.\n\n" +
    HostEnvironment.PromptFragment;

// NEW in s08: the four-layer compactor
var compactor = new ContextCompactor(client, config, workDir, msg => Console.WriteLine(msg));

var agent = new AgentHarness(client, parentTools, system)
{
    OnLog = Console.WriteLine,
    Permissions = new AllowAllPermissions(),
    Compactor = compactor,
};

agent.Hooks.OnUserPromptSubmit(_ =>
    Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit: cwd={workDir}\u001b[0m"));
agent.Hooks.OnPreToolUse(block =>
{
    var args = string.Join(", ", block.Input.EnumerateObject().Take(2).Select(o => $"{o.Name}={Trunc(o.Value.ToString())}"));
    Console.WriteLine($"\u001b[90m[HOOK] {block.Name}({args})\u001b[0m");
    return null;
});

Console.WriteLine("s08: Context Compact — four layers, cheap first, expensive last");
Console.WriteLine("Type a long conversation and watch the compactor kick in. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms08 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
