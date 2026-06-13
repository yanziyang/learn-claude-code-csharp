// s11_error_recovery/Program.cs -- Error Recovery
//
// "Errors aren't the end, they're the start of a retry" — three recovery
// paths wrapped around the LLM call:
//
//   1. max_tokens:           escalate 8K -> 64K, then a continuation nudge
//   2. prompt_too_long:      emergency compact -> retry
//   3. 429 / 529 overloaded: exponential backoff with jitter, fallback
//                            model on consecutive 529s
//
// Compared to s10:
//   + RetryPolicy attached to DeepSeekClient
//   + MaxTokensEscalation property on AgentHarness
//   + Emergency compact is exposed via IContextCompactor (s08)
//
// This chapter intentionally keeps the working toolset small so the
// focus is on the recovery paths.

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Compact;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Tools;
using AgentCommon.Util;

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);

// NEW in s11: attach a retry policy (exponential backoff + fallback model)
client.AttachRetryPolicy(new RetryPolicy
{
    MaxRetries = 10,
    BaseDelayMs = 500,
    MaxConsecutiveOverloaded = 3,
    FallbackModel = config.FallbackModel,
    OnLog = msg => Console.WriteLine(msg),
});

var workDir = Directory.GetCurrentDirectory();
var tools = new ToolRegistry();
BashTool.Register(tools, workDir);
FileTools.Register(tools, workDir);

var compactor = new ContextCompactor(client, config, workDir, msg => Console.WriteLine(msg));

var system = $"You are a coding agent at {workDir}. Use tools to solve tasks. Act, don't explain.";
var agent = new AgentHarness(client, tools, system)
{
    OnLog = Console.WriteLine,
    Permissions = new AllowAllPermissions(),
    Compactor = compactor,
    // Escalation target: 64K
    MaxTokensEscalation = 64_000,
};

agent.Hooks.OnUserPromptSubmit(_ =>
    Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit: cwd={workDir}\u001b[0m"));
agent.Hooks.OnPreToolUse(block =>
{
    var args = string.Join(", ", block.Input.EnumerateObject().Take(2).Select(o => $"{o.Name}={Trunc(o.Value.ToString())}"));
    Console.WriteLine($"\u001b[90m[HOOK] {block.Name}({args})\u001b[0m");
    return null;
});

Console.WriteLine("s11: Error Recovery — backoff, escalation, fallback");
Console.WriteLine("Type a task. Set FALLBACK_MODEL_ID in appsettings.json to enable model fallback. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms11 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
