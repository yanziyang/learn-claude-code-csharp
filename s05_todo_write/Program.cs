// s05_todo_write/Program.cs -- TodoWrite
//
// "An agent without a plan drifts" — make the model maintain a todo list
// while it works, and nag it back to the list if it forgets.
//
// Compared to s04:
//   + TodoTools  (NEW: a tool the model calls to publish its plan)
//   + Nag counter (PostToolUse hook resets on todo_write, fires reminder
//                   on the next iteration if 3 rounds pass without one)

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Messages;
using AgentCommon.Tools;
using AgentCommon.Util;

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);
var tools = new ToolRegistry();

var workDir = Directory.GetCurrentDirectory();
BashTool.Register(tools, workDir);                    // from s01
FileTools.Register(tools, workDir);                   // from s02
var todos = new TodoTools.TodoState();
TodoTools.Register(tools, todos);                     // NEW in s05

var system = $"You are a coding agent at {workDir}. Use tools to solve tasks. Act, don't explain.";
var agent = new AgentHarness(client, tools, system)
{
    OnLog = Console.WriteLine,
    Permissions = new AllowAllPermissions(),
};

// s05: nag reminder — runs as a Stop-style "force continue" with a user message
// every 3 rounds if the model has not called todo_write
var roundsSinceTodo = 0;
agent.Hooks.OnPostToolUse((block, _) =>
{
    if (block.Name == "todo_write")
    {
        roundsSinceTodo = 0;
    }
});
agent.Hooks.OnStop((IReadOnlyList<Message>? history) =>
{
    if (++roundsSinceTodo >= 3 && history is { Count: > 0 })
    {
        // Re-inject the last user message with a reminder nudge
        roundsSinceTodo = 0;
        return "<reminder>Update your todo list.</reminder>";
    }
    return null;
});

// Minimal hook log for s05
agent.Hooks.OnUserPromptSubmit(_ =>
    Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit: cwd={workDir}\u001b[0m"));
agent.Hooks.OnPreToolUse(block =>
{
    var args = string.Join(", ", block.Input.EnumerateObject().Take(2).Select(o => $"{o.Name}={Trunc(o.Value.ToString())}"));
    Console.WriteLine($"\u001b[90m[HOOK] {block.Name}({args})\u001b[0m");
    return null;
});

Console.WriteLine("s05: TodoWrite — plan first, nag if you forget");
Console.WriteLine("Type a task and press Enter. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms05 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
