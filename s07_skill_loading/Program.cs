// s07_skill_loading/Program.cs -- Skill Loading
//
// "Load knowledge on demand, not upfront" — two-level injection:
//
//   Layer 1 (cheap, always present): the system prompt carries the skill
//          catalog — name + one-line description per skill.
//
//   Layer 2 (expensive, on demand): the model calls load_skill(name)
//          to get the full SKILL.md content via the tool result.
//
// Compared to s06:
//   + SkillRegistry scans ../skills/ at startup
//   + build_system() injects the catalog into SYSTEM
//   + load_skill(name) returns the full SKILL.md

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Skills;
using AgentCommon.Subagent;
using AgentCommon.Tools;
using AgentCommon.Util;

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);

var workDir = Directory.GetCurrentDirectory();
var skillsDir = Path.GetFullPath(Path.Combine(workDir, "..", "skills"));
var skills = SkillRegistry.LoadFromDir(skillsDir);

// ── Parent tools ─────────────────────────────────────────
var parentTools = new ToolRegistry();
BashTool.Register(parentTools, workDir);
FileTools.Register(parentTools, workDir);
var todos = new TodoTools.TodoState();
TodoTools.Register(parentTools, todos);
SkillTools.Register(parentTools, skills);  // NEW in s07

// Sub-agent gets a stripped-down view: no skill loading, no task
var subTools = new ToolRegistry();
BashTool.Register(subTools, workDir);
FileTools.Register(subTools, workDir);

SubagentRunner SpawnSub() => new(
    client, config, subTools,
    $"You are a focused sub-agent at {workDir}. Complete the given task and return a concise final answer.",
    msg => Console.WriteLine(msg));
TaskTool.Register(parentTools, SpawnSub);

var system =
    $"You are a coding agent at {workDir}. " +
    "Skills available:\n" + skills.Catalog() + "\n" +
    "Use load_skill to get full details when needed.";

var agent = new AgentHarness(client, parentTools, system)
{
    OnLog = Console.WriteLine,
    Permissions = new AllowAllPermissions(),
};

agent.Hooks.OnUserPromptSubmit(_ =>
    Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit: cwd={workDir}\u001b[0m"));
agent.Hooks.OnPreToolUse(block =>
{
    var args = string.Join(", ", block.Input.EnumerateObject().Take(2).Select(o => $"{o.Name}={Trunc(o.Value.ToString())}"));
    Console.WriteLine($"\u001b[90m[HOOK] {block.Name}({args})\u001b[0m");
    return null;
});

Console.WriteLine("s07: Skill Loading — catalog in SYSTEM, full content on demand");
Console.WriteLine("Type a task and press Enter. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms07 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
