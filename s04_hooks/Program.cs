// s04_hooks/Program.cs -- Hook System
//
// "Hook around the loop, never rewrite the loop" — extension points live
// on the HookBus, not in AgentHarness body.
//
// All hooks are declared in appsettings.json (the `hooks` section) and
// loaded dynamically at startup via agent.Hooks.ConfigureExternal(...).
// This file does not register a single OnXxx handler: the agent loop is
// untouched, every behavior comes from the configuration.

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Hooks;
using AgentCommon.Llm;
using AgentCommon.Tools;
using AgentCommon.Util;

HostEnvironment.Initialize();
Console.WriteLine($"[host] {HostEnvironment.OsName} ({HostEnvironment.Shell})");

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);
var tools = new ToolRegistry();

var workDir = ResolveWorkDir();
BashTool.Register(tools, workDir);
FileTools.Register(tools, workDir);

static string ResolveWorkDir()
{
    var cwd = Directory.GetCurrentDirectory();
    if (Directory.Exists(Path.Combine(cwd, "hooks"))) return cwd;

    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir))
    {
        if (Directory.Exists(Path.Combine(dir, "hooks"))) return dir;
        var parent = Directory.GetParent(dir);
        if (parent is null || parent.FullName == dir) break;
        dir = parent.FullName;
    }
    return cwd;
}

var system = $"You are a coding agent at {workDir}. Use tools to solve tasks. Act, don't explain.\n\n" +
             HostEnvironment.PromptFragment;
var agent = new AgentHarness(client, tools, system)
{
    OnLog = Console.WriteLine,
    WorkDir = workDir,
    Permissions = new AllowAllPermissions(),
};

// One log file per day, written into <workDir>/logs/. Wired into the
// ExternalHookRunner so every hook invocation produces a structured
// before/after entry in the daily log.
using var logger = new AppLogger(workDir);
logger.Info("host", $"s04 starting in {workDir}");

// Load every external hook declared under `hooks.<Event>[]` in
// appsettings.json. The runner, work dir, and command list are all set
// from configuration; nothing is hard-coded below this line.
var n = agent.Hooks.ConfigureExternal(
    config.Hooks,
    workDir,
    log: msg => Console.Error.WriteLine(msg),
    timeout: TimeSpan.FromSeconds(30),
    logger: logger);
if (n > 0) Console.WriteLine($"[host] loaded {n} external hook(s) from appsettings.json");
Console.WriteLine($"[host] logging to {logger.CurrentFile}");

Console.WriteLine("s04: Hooks — all behavior from appsettings.json, loop untouched");
Console.WriteLine("Type a task and press Enter. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms04 >> \u001b[0m");
logger.Info("host", "s04 stopped");
