// s09_memory/Program.cs -- Memory System
//
// "Remember what matters, forget what doesn't" — three subsystems:
//
//   selection    : pick relevant memories for the current context
//   extraction   : write new memories from recent dialogue
//   consolidation: merge/dedupe when count exceeds a threshold
//
// Compared to s08:
//   + MemoryStore, MemorySelector, MemoryExtractor, MemoryConsolidator
//   + PreToolUse hook injects selected memories
//   + PostToolUse hook extracts new memories and may consolidate
//   + SYSTEM carries the MEMORY.md index

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Compact;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Memory;
using AgentCommon.Messages;
using AgentCommon.Skills;
using AgentCommon.Subagent;
using AgentCommon.Tools;
using AgentCommon.Util;

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
    $"You are a focused sub-agent at {workDir}. Complete the given task and return a concise final answer.",
    msg => Console.WriteLine(msg));
TaskTool.Register(parentTools, SpawnSub);

var store = new MemoryStore(workDir);
var selector = new MemorySelector(client, store);
var extractor = new MemoryExtractor(client, store, msg => Console.WriteLine(msg));
var consolidator = new MemoryConsolidator(client, store, threshold: 10, msg => Console.WriteLine(msg));

var system =
    $"You are a coding agent at {workDir}.\n\n" +
    "Memories available:\n" + (string.IsNullOrEmpty(store.IndexText()) ? "(none yet)" : store.IndexText()) + "\n\n" +
    "Relevant memories are injected below. Respect user preferences from memory.\n" +
    "When the user says 'remember' or expresses a clear preference, extract it as a memory.";

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

// Memory hooks
agent.Hooks.OnPreToolUse(block =>
{
    // Only run on the first LLM call of an iteration: skip on subsequent tool calls
    if (block.Name == "bash" || block.Name == "read_file" || block.Name == "glob" || block.Name == "edit_file" || block.Name == "write_file" || block.Name == "task" || block.Name == "todo_write" || block.Name == "load_skill")
    {
        return null; // do nothing here — selection happens via the Stop hook below
    }
    return null;
});

agent.Hooks.OnStop((IReadOnlyList<Message>? history) =>
{
    if (history is null || history.Count == 0) return null;
    _ = Task.Run(async () =>
    {
        // Selection: find relevant memories and surface them via console
        var selected = await selector.SelectRelevantAsync(history);
        if (selected.Count > 0)
        {
            Console.WriteLine($"\u001b[36m[Memory: selected {selected.Count}]\u001b[0m");
            foreach (var f in selected) Console.WriteLine($"  - {f}");
        }
        // Extraction: try to extract from the recent turn
        var extracted = await extractor.ExtractFromAsync(history);
        // Dream: consolidate if too many
        if (extracted > 0 || store.List().Count >= 10)
        {
            await consolidator.ConsolidateIfNeededAsync();
        }
    });
    return null;
});

Console.WriteLine("s09: Memory — selection, extraction, consolidation");
Console.WriteLine("Type a task and watch memories accumulate in .memory/. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms09 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
