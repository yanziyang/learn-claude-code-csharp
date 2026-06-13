// s03_permission/Program.cs -- Permission System
//
// "Set boundaries first, then grant freedom" — three gates inserted
// before tool execution:
//
//     Gate 1: Hard deny list (rm -rf /, sudo, ...)
//     Gate 2: Rule matching (writes outside workspace? destructive cmd?)
//     Gate 3: User approval (pause and wait for confirmation)
//
// Only one decision is added to the agent loop — AgentHarness already
// calls agent.Permissions.Check(...) before invoking each tool.

using System.Text.Json;
using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Permissions;
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

// NEW in s03: the three-gate permission pipeline
var denyList = new[] { "rm -rf /", "sudo", "shutdown", "reboot", "mkfs", "dd if=", "> /dev/sda" };
var rules = new List<PermissionRule>
{
    new()
    {
        Tools = new[] { "write_file", "edit_file" },
        Check = args =>
        {
            if (args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                try { _ = PathGuard.SafePath(workDir, p.GetString() ?? ""); return null; }
                catch { return "Writing outside workspace"; }
            }
            return null;
        },
        Message = "Writing outside workspace",
    },
    new()
    {
        Tools = new[] { "bash" },
        Check = args =>
        {
            if (args.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var cmd = c.GetString() ?? "";
                if (cmd.Contains("rm ", StringComparison.Ordinal) ||
                    cmd.Contains("> /etc/", StringComparison.Ordinal) ||
                    cmd.Contains("chmod 777", StringComparison.Ordinal))
                {
                    return "Potentially destructive command";
                }
            }
            return null;
        },
        Message = "Potentially destructive command",
    },
};

var system = $"You are a coding agent at {workDir}. All destructive operations require user approval.\n\n" +
             HostEnvironment.PromptFragment;
var agent = new AgentHarness(client, tools, system)
{
    OnLog = Console.WriteLine,
    Permissions = new InteractivePermissionPipeline(workDir, denyList, rules),
};

Console.WriteLine("s03: Permission — three gates before every tool call");
Console.WriteLine("Type a task and press Enter. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms03 >> \u001b[0m");
