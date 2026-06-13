// s20_comprehensive/Program.cs -- Comprehensive Agent
//
// "Many mechanisms, one loop" — every prior mechanism is enabled at
// once. The harness's loop doesn't change; we just register every
// tool and every hook. This is what production looks like.
//
//   s01  Agent loop
//   s02  Tool use (file tools)
//   s03  Permission pipeline
//   s04  Hook system
//   s05  TodoWrite
//   s06  Subagent (task tool)
//   s07  Skill loading
//   s08  Context compact
//   s09  Memory
//   s10  System prompt assembly
//   s11  Error recovery
//   s12  Task system
//   s13  Background tasks
//   s14  Cron scheduler
//   s15  Agent teams
//   s16  Team protocols
//   s17  Autonomous agents
//   s18  Worktree isolation
//   s19  MCP plugin

using System.Text.Json;
using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Background;
using AgentCommon.Compact;
using AgentCommon.Config;
using AgentCommon.Cron;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Memory;
using AgentCommon.Messages;
using AgentCommon.Permissions;
using AgentCommon.Skills;
using AgentCommon.Subagent;
using AgentCommon.Tasks;
using AgentCommon.Teams;
using AgentCommon.Tools;
using AgentCommon.Util;

HostEnvironment.Initialize();
Console.WriteLine($"[host] {HostEnvironment.OsName} ({HostEnvironment.Shell})");

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);
client.AttachRetryPolicy(new RetryPolicy
{
    FallbackModel = config.FallbackModel,
    OnLog = msg => Console.WriteLine(msg),
});

var workDir = Directory.GetCurrentDirectory();
var skillsDir = Path.GetFullPath(Path.Combine(workDir, "..", "skills"));
var skills = SkillRegistry.LoadFromDir(skillsDir);

var tools = new ToolRegistry();
var background = new BackgroundRunner();
BashTool.Register(tools, workDir, onLog: msg => Console.WriteLine(msg), background: background);
FileTools.Register(tools, workDir);
SkillTools.Register(tools, skills);

var todos = new TodoTools.TodoState();
TodoTools.Register(tools, todos);

var store = new TaskStore(workDir);
TaskTools.Register(tools, store, msg => Console.WriteLine(msg));

var cron = new CronScheduler(workDir, msg => Console.WriteLine(msg));
CronTools.Register(tools, cron);

var bus = new MessageBus(workDir, msg => Console.WriteLine(msg));
TeamTools.Register(tools, bus, client, config, workDir, msg => Console.WriteLine(msg));

// Sub-agent (uses its own stripped-down tool set)
var subTools = new ToolRegistry();
BashTool.Register(subTools, workDir, onLog: msg => Console.WriteLine(msg));
FileTools.Register(subTools, workDir);
SubagentRunner SpawnSub() => new(
    client, config, subTools,
    $"You are a focused sub-agent on {HostEnvironment.OsName} ({HostEnvironment.Shell}). " +
    "Return a concise summary.\n\n" + HostEnvironment.PromptFragment,
    msg => Console.WriteLine(msg));
TaskTool.Register(tools, SpawnSub);

// MCP tools (mock)
tools.Register("mcp__docs__search", "Search an external docs service.",
    SchemaBuilder.Object("Search an external docs service.",
        new Dictionary<string, (string, string, bool)> { ["query"] = ("string", "Search query", true) }),
    input => $"docs.search(\"{input.GetProperty("query").GetString()}\") — mocked MCP result");

// Memory + selection (hook-based)
var memStore = new MemoryStore(workDir);
var memSelector = new MemorySelector(client, memStore);
var memExtractor = new MemoryExtractor(client, memStore, msg => Console.WriteLine(msg));
var memConsolidator = new MemoryConsolidator(client, memStore, threshold: 10, msg => Console.WriteLine(msg));

// Permissions (s03)
var denyList = new[] { "rm -rf /", "sudo", "shutdown", "reboot", "mkfs", "dd if=", "> /dev/sda" };
var rules = new List<PermissionRule>
{
    new() { Tools = new[] { "write_file", "edit_file" },
            Check = args => { try { _ = PathGuard.SafePath(workDir, args.GetProperty("path").GetString() ?? ""); return null; } catch { return "Writing outside workspace"; } },
            Message = "Writing outside workspace" },
    new() { Tools = new[] { "bash" },
            Check = args => { var c = args.TryGetProperty("command", out var v) ? v.GetString() ?? "" : ""; return c.Contains("rm ", StringComparison.Ordinal) || c.Contains("> /etc/", StringComparison.Ordinal) ? "Potentially destructive command" : null; },
            Message = "Potentially destructive command" },
};
var permissions = new InteractivePermissionPipeline(workDir, denyList, rules);

// System prompt assembly (s10) — sections + cache
var sections = new Dictionary<string, string>
{
    ["identity"] = "You are a comprehensive coding agent with all mechanisms enabled.",
    ["tools"] = "Available tools: bash, read_file, write_file, edit_file, glob, todo_write, task, load_skill, create_task/list_tasks/claim_task/complete_task, schedule_cron/list_crons/cancel_cron, spawn_teammate/send_message/check_inbox, mcp__docs__search.",
    ["workspace"] = $"Working directory: {workDir}",
    ["memory"] = "Relevant memories are injected below when available.",
    ["environment"] = HostEnvironment.PromptFragment,
};
var ctx = new Dictionary<string, object>
{
    ["enabled_tools"] = tools.All().Select(t => t.Name).ToList(),
    ["workspace"] = workDir,
    ["memories"] = memStore.IndexText(),
};
string? lastKey = null, lastPrompt = null;
string GetPrompt()
{
    var key = JsonSerializer.Serialize(ctx);
    if (key == lastKey && lastPrompt is not null) return lastPrompt;
    lastKey = key; lastPrompt = Assemble(sections, ctx);
    return lastPrompt;
}

var compactor = new ContextCompactor(client, config, workDir, msg => Console.WriteLine(msg));
var agent = new AgentHarness(client, tools, (Func<string>)GetPrompt)
{
    OnLog = Console.WriteLine,
    Permissions = permissions,
    Compactor = compactor,
    MaxTokensEscalation = config.MaxTokensEscalation,
};

agent.Hooks.OnUserPromptSubmit(_ => Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit\u001b[0m"));
agent.Hooks.OnPreToolUse(block =>
{
    var args = string.Join(", ", block.Input.EnumerateObject().Take(2).Select(o => $"{o.Name}={Trunc(o.Value.ToString())}"));
    Console.WriteLine($"\u001b[90m[HOOK] {block.Name}({args})\u001b[0m");
    return null;
});
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
    _ = memExtractor.ExtractFromAsync(messages).ContinueWith(t => memConsolidator.ConsolidateIfNeededAsync());
});

Console.WriteLine("s20: Comprehensive Agent — every mechanism, one loop");
Console.WriteLine("Bash, files, todo, skills, sub-agent, memory, compact, retry, tasks, background, cron, teams, MCP, hooks, permissions.");
Console.WriteLine("Type a complex task. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms20 >> \u001b[0m");

static string Assemble(Dictionary<string, string> sections, Dictionary<string, object> ctx)
{
    var parts = new List<string> { sections["identity"], sections["tools"], sections["workspace"], sections["environment"] };
    if (ctx.TryGetValue("memories", out var m) && m is string ms && !string.IsNullOrEmpty(ms))
        parts.Add($"Relevant memories:\n{ms}");
    return string.Join("\n\n", parts);
}

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
