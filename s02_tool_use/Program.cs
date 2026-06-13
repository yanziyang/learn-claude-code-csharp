// s02_tool_use/Program.cs -- Tool Use
//
// "Adding a tool means adding one handler" — the loop stays untouched;
// new tools register into the dispatch map.
//
// Compared to s01:
//   + BashTool  (carried forward)
//   + FileTools (NEW in s02: read_file, write_file, edit_file, glob)
//   + PathGuard (NEW in s02: prevents escaping the workspace)
//
// The agent loop in AgentHarness.RunUntilDoneAsync is unchanged.

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Tools;
using AgentCommon.Util;

HostEnvironment.Initialize();
Console.WriteLine($"[host] {HostEnvironment.OsName} ({HostEnvironment.Shell})");

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);
var tools = new ToolRegistry();

var workDir = Directory.GetCurrentDirectory();
BashTool.Register(tools, workDir);                    // from s01
FileTools.Register(tools, workDir);                   // NEW in s02

var system = $"You are a coding agent at {workDir}. Use tools to solve tasks. Act, don't explain.\n\n" +
             HostEnvironment.PromptFragment;
var agent = new AgentHarness(client, tools, system)
{
    OnLog = Console.WriteLine,
};

Console.WriteLine("s02: Tool Use — five tools in one dispatch map");
Console.WriteLine("Type a task and press Enter. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms02 >> \u001b[0m");
