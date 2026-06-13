// s10_system_prompt/Program.cs -- System Prompt
//
// "Prompts are assembled at runtime, not hardcoded" — section-keyed
// fragments joined by current context, with a deterministic cache.
//
// Compared to s09:
//   + PROMPT_SECTIONS: topic-keyed dict of prompt fragments
//   + assemble_system_prompt(context): select + join sections by real state
//   + get_system_prompt(context): deterministic cache (re-assembles only
//     when the context changes)
//   + update_context: derive context from real state on disk
//   The loop calls provider(context) before each LLM call, so sections
//   like 'memory' are picked up the moment a memory file appears.

using System.Text.Json;
using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Memory;
using AgentCommon.Tools;
using AgentCommon.Util;

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);

var workDir = Directory.GetCurrentDirectory();
var tools = new ToolRegistry();
BashTool.Register(tools, workDir);
FileTools.Register(tools, workDir);

var store = new MemoryStore(workDir);
var indexPath = Path.Combine(store.MemoryDir, "MEMORY.md");

// ── Prompt sections ────────────────────────────────────
var sections = new Dictionary<string, string>
{
    ["identity"] = "You are a coding agent. Act, don't explain.",
    ["tools"] = "Available tools: bash, read_file, write_file, edit_file, glob.",
    ["workspace"] = $"Working directory: {workDir}",
    ["memory"] = "Relevant memories are injected below when available.",
};

var assemble = (Dictionary<string, object> ctx) =>
{
    var parts = new List<string> { sections["identity"], sections["tools"], sections["workspace"] };
    if (ctx.TryGetValue("memories", out var m) && m is string ms && !string.IsNullOrEmpty(ms))
    {
        parts.Add($"Relevant memories:\n{ms}");
    }
    return string.Join("\n\n", parts);
};

string? lastKey = null;
string? lastPrompt = null;
var getPrompt = (Dictionary<string, object> ctx) =>
{
    var key = JsonSerializer.Serialize(ctx, new JsonSerializerOptions
    {
        WriteIndented = false,
    });
    if (key == lastKey && lastPrompt is not null)
    {
        Console.WriteLine("  \u001b[90m[cache hit] system prompt unchanged\u001b[0m");
        return lastPrompt;
    }
    lastKey = key;
    lastPrompt = assemble(ctx);
    var loaded = new List<string> { "identity", "tools", "workspace" };
    if (ctx.TryGetValue("memories", out var m) && m is string ms && !string.IsNullOrEmpty(ms))
    {
        loaded.Add("memory");
    }
    Console.WriteLine($"  \u001b[32m[assembled] sections: {string.Join(", ", loaded)}\u001b[0m");
    return lastPrompt;
};

var updateContext = () =>
{
    var memIndex = File.Exists(indexPath) ? File.ReadAllText(indexPath).Trim() : "";
    return new Dictionary<string, object>
    {
        ["enabled_tools"] = tools.All().Select(t => t.Name).ToList(),
        ["workspace"] = workDir,
        ["memories"] = memIndex,
    };
};

var ctx = updateContext();
var agent = new AgentHarness(client, tools, () => getPrompt(ctx))
{
    OnLog = Console.WriteLine,
    Permissions = new AllowAllPermissions(),
};
agent.Hooks.OnPreToolUse(block =>
{
    ctx = updateContext();
    return null;
});

Console.WriteLine("s10: System Prompt — runtime assembly with deterministic cache");
Console.WriteLine("Type a task and watch the system prompt cache log. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms10 >> \u001b[0m");
