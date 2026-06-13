// s01_agent_loop/Program.cs -- The Agent Loop
//
// The whole secret of an AI coding agent in one pattern:
//
//     while stop_reason == "tool_use":
//         response = LLM(messages, tools)
//         execute tools
//         append results
//
// +---------+    +-----+    +---------+
// |  User   |--->| LLM |--->|  Tool   |
// | prompt  |    |     |    | execute |
// +---------+    +--+--+    +----+----+
//                  ^             |
//                  | tool_result |
//                  +-------------+ (loop continues)
//
// This is the core loop: feed tool results back to the model
// until the model decides to stop. Production agents layer
// policy, hooks, and lifecycle controls on top.

using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Config;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Tools;
using AgentCommon.Util;

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);
var tools = new ToolRegistry();

var workDir = Directory.GetCurrentDirectory();
BashTool.Register(tools, workDir);  // ← the only tool the agent has in s01

// ── The core pattern: a while loop that calls tools until the model stops ──
//    (the loop itself lives in AgentHarness.RunUntilDoneAsync)
var system = $"You are a coding agent at {workDir}. Use bash to solve tasks. Act, don't explain.";
var agent = new AgentHarness(client, tools, system)
{
    OnLog = Console.WriteLine,
};

Console.WriteLine("s01: Agent Loop");
Console.WriteLine("Type a task and press Enter. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms01 >> \u001b[0m");
