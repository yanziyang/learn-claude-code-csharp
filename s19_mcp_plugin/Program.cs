// s19_mcp_plugin/Program.cs -- MCP Plugin
//
// "Not enough capability? Plug in more via MCP" — tools from external
// MCP servers are combined with built-in tools into a single pool
// the model sees. Tool names are normalized as
// `mcp__{server}__{tool}`.
//
// The teaching implementation uses a hand-rolled mock MCP server
// (a static catalog of tools). The protocol structure is identical
// to the production MCP JSON-RPC pattern; only the transport
// (stdio / SSE) is replaced by an in-process dictionary.

using System.Text.Json;
using AgentCommon;
using AgentCommon.Agent;
using AgentCommon.Background;
using AgentCommon.Compact;
using AgentCommon.Config;
using AgentCommon.Cron;
using AgentCommon.Defaults;
using AgentCommon.Llm;
using AgentCommon.Messages;
using AgentCommon.Tasks;
using AgentCommon.Teams;
using AgentCommon.Tools;
using AgentCommon.Util;

HostEnvironment.Initialize();
Console.WriteLine($"[host] {HostEnvironment.OsName} ({HostEnvironment.Shell})");

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);

var workDir = Directory.GetCurrentDirectory();
var builtin = new ToolRegistry();

var background = new BackgroundRunner();
BashTool.Register(builtin, workDir, onLog: msg => Console.WriteLine(msg), background: background);
FileTools.Register(builtin, workDir);

var store = new TaskStore(workDir);
TaskTools.Register(builtin, store, msg => Console.WriteLine(msg));

var cron = new CronScheduler(workDir, msg => Console.WriteLine(msg));
CronTools.Register(builtin, cron);

var bus = new MessageBus(workDir, msg => Console.WriteLine(msg));
TeamTools.Register(builtin, bus, client, config, workDir, msg => Console.WriteLine(msg));

// ── Mock MCP server: "docs" exposes two tools ───────────
var mockDocs = new Dictionary<string, Func<JsonElement, string>>(StringComparer.OrdinalIgnoreCase)
{
    ["search"] = input =>
    {
        var q = input.TryGetProperty("query", out var qp) ? qp.GetString() ?? "" : "";
        return $"docs.search(\"{q}\") — would call an external docs service (mocked).";
    },
    ["get_version"] = _ => "docs.get_version: 1.0.0 (mocked)",
};
var mockFs = new Dictionary<string, Func<JsonElement, string>>(StringComparer.OrdinalIgnoreCase)
{
    ["list_dir"] = input =>
    {
        var p = input.TryGetProperty("path", out var pp) ? pp.GetString() ?? "." : ".";
        try
        {
            var full = Path.IsPathRooted(p) ? p : Path.Combine(workDir, p);
            return string.Join("\n", Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName));
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    },
};

var serverTools = new Dictionary<string, (Dictionary<string, Func<JsonElement, string>> catalog, string connection)>(StringComparer.OrdinalIgnoreCase)
{
    ["docs"] = (mockDocs, "stdio://docs-server"),
    ["fs"] = (mockFs, "stdio://fs-server"),
};

string Normalize(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9_]+", "_");

// ── Pool = builtin ∪ MCP ────────────────────────────────
var pool = new ToolRegistry();
foreach (var t in builtin.All()) pool.Register(t.Name, t.Description, t.InputSchema, t.Handler);
foreach (var (server, (catalog, conn)) in serverTools)
{
    foreach (var toolName in catalog.Keys)
    {
        var localCatalog = catalog;
        var prefix = $"mcp__{Normalize(server)}__{Normalize(toolName)}";
        var schema = SchemaBuilder.Object($"MCP tool {toolName} on {server}.",
            new Dictionary<string, (string, string, bool)>());
        pool.Register(prefix, $"MCP tool {toolName} on {server}.", schema, input =>
            localCatalog[toolName](input));
    }
}

Console.WriteLine($"\u001b[36m[s19] tool pool assembled: {pool.All().Count()} tools ({serverTools.Sum(kv => kv.Value.catalog.Count)} MCP, {builtin.All().Count()} builtin)\u001b[0m");

var compactor = new ContextCompactor(client, config, workDir, msg => Console.WriteLine(msg));
var system = $"You are a coding agent at {workDir}. " +
             $"Available tools: {string.Join(", ", pool.All().Select(t => t.Name))}.\n\n" +
             HostEnvironment.PromptFragment;
var agent = new AgentHarness(client, pool, system)
{
    OnLog = Console.WriteLine,
    Permissions = new AllowAllPermissions(),
    Compactor = compactor,
    MaxTokensEscalation = config.MaxTokensEscalation,
};

agent.Hooks.OnUserPromptSubmit(_ =>
    Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit: cwd={workDir}\u001b[0m"));
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
});

Console.WriteLine("s19: MCP Plugin — external tools merged into one pool");
Console.WriteLine("Type a task. The model sees builtins + mcp__* tools. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms19 >> \u001b[0m");

static string Trunc(string s) => s.Length > 60 ? s[..60] + "..." : s;
