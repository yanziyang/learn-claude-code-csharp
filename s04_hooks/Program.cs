// s04_hooks/Program.cs -- Hook System
//
// "Hook around the loop, never rewrite the loop" — extension points live
// on the HookBus, not in AgentHarness body.
//
// Compared to s03:
//   + PreToolUse:    permission_hook (s03 logic moved here)
//                    log_hook       (audit trail)
//   + PostToolUse:   large_output_hook
//   + UserPromptSubmit: context_inject_hook
//   + Stop:          summary_hook
//
// The loop in AgentHarness doesn't know about any of these. It just fires
// the right hook at the right time.

using System.Text.Json;
using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Messages;
using AgentCommon.Tools;
using AgentCommon.Util;

HostEnvironment.Initialize();
Console.WriteLine($"[host] {HostEnvironment.OsName} ({HostEnvironment.Shell})");

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);
var tools = new ToolRegistry();

var workDir = Directory.GetCurrentDirectory();
BashTool.Register(tools, workDir);                    // from s01
FileTools.Register(tools, workDir);                   // from s02

var system = $"You are a coding agent at {workDir}. Use tools to solve tasks. Act, don't explain.\n\n" +
             HostEnvironment.PromptFragment;
var agent = new AgentHarness(client, tools, system)
{
    OnLog = Console.WriteLine,
    // s04 change: s03's permission pipeline is no longer wired via
    // IPermissionChecker — it lives on the PreToolUse hook instead.
    Permissions = new AllowAllPermissions(),
};

// ── NEW in s04: hook subscribers ───────────────────────────

// UserPromptSubmit: log the working directory before the LLM is called
agent.Hooks.OnUserPromptSubmit(_ =>
    Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit: cwd={workDir}\u001b[0m"));

// PreToolUse: the s03 permission logic, now expressed as a hook
agent.Hooks.OnPreToolUse(block =>
{
    if (block.Name == "bash"
        && block.Input.TryGetProperty("command", out var cmd)
        && cmd.ValueKind == JsonValueKind.String)
    {
        var text = cmd.GetString() ?? "";
        foreach (var d in new[] { "rm -rf /", "sudo", "shutdown", "reboot", "mkfs", "dd if=" })
        {
            if (text.Contains(d, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"\n\u001b[31m\u26d4 Blocked: '{d}'\u001b[0m");
                return "Permission denied by deny list";
            }
        }
        foreach (var kw in new[] { "rm ", "> /etc/", "chmod 777" })
        {
            if (text.Contains(kw, StringComparison.Ordinal))
            {
                Console.WriteLine($"\n\u001b[33m\u26a0  Potentially destructive command\u001b[0m");
                Console.WriteLine($"   Tool: {block.Name}({block.Input})");
                Console.Write("   Allow? [y/N] ");
                var ans = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (ans is not ("y" or "yes"))
                {
                    return "Permission denied by user";
                }
            }
        }
    }
    if (block.Name is "write_file" or "edit_file"
        && block.Input.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
    {
        try { _ = PathGuard.SafePath(workDir, p.GetString() ?? ""); }
        catch
        {
            Console.WriteLine($"\n\u001b[33m\u26a0  Writing outside workspace\u001b[0m");
            Console.WriteLine($"   Tool: {block.Name}({block.Input})");
            Console.Write("   Allow? [y/N] ");
            var ans = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (ans is not ("y" or "yes"))
            {
                return "Permission denied by user";
            }
        }
    }
    return null;
});

// PreToolUse: log every tool call (audit trail)
agent.Hooks.OnPreToolUse(block =>
{
    var args = string.Join(", ", block.Input.EnumerateObject().Take(2).Select(o => $"{o.Name}={Trunc(o.Value.ToString())}"));
    Console.WriteLine($"\u001b[90m[HOOK] {block.Name}({args})\u001b[0m");
    return null;
});

// PostToolUse: warn on oversized outputs
agent.Hooks.OnPostToolUse((block, output) =>
{
    if (output.Length > 100_000)
    {
        Console.WriteLine($"\u001b[33m[HOOK] \u26a0 Large output from {block.Name}: {output.Length} chars\u001b[0m");
    }
});

// Stop: print a session summary when the loop is about to exit
agent.Hooks.OnStop((IReadOnlyList<Message>? history) =>
{
    var toolCalls = 0;
    if (history is not null)
    {
        foreach (var msg in history)
        {
            foreach (var b in msg.Content.OfType<ToolResultBlock>()) toolCalls++;
        }
    }
    Console.WriteLine($"\u001b[90m[HOOK] Stop: session used {toolCalls} tool calls\u001b[0m");
    return null;
});

Console.WriteLine("s04: Hooks — Pre/Post/Submit/Stop, loop untouched");
Console.WriteLine("Type a task and press Enter. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms04 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
