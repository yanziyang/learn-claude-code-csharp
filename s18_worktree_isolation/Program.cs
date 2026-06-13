// s18_worktree_isolation/Program.cs -- Worktree Isolation
//
// "Each works in its own directory, no interference" — every owned
// task gets a dedicated working directory under .worktrees/<name>.
// The teammate's bash tool, file reads/writes, etc. all run in that
// subdirectory, so two workers never clobber each other.
//
// Compared to s17:
//   + WorktreeRegistry: creates / removes / lists isolated directories
//   + TaskStore gains a worktree field
//   + TeammateRunner routes all tool work into the worktree dir
//   + Scout binds a worktree to each task it claims
//
// Note: the teaching version uses plain subdirectories, not git
// worktrees, so the lesson works without a git repository. The
// production equivalent binds .worktrees/<name> to a git worktree.

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

var config = AgentConfigLoader.Load();
var client = new DeepSeekClient(config);

var workDir = Directory.GetCurrentDirectory();
var worktreesDir = Path.Combine(workDir, ".worktrees");
Directory.CreateDirectory(worktreesDir);

var tools = new ToolRegistry();

var background = new BackgroundRunner();
BashTool.Register(tools, workDir, onLog: msg => Console.WriteLine(msg), background: background);
FileTools.Register(tools, workDir);

var store = new TaskStore(workDir);
TaskTools.Register(tools, store, msg => Console.WriteLine(msg));

var cron = new CronScheduler(workDir, msg => Console.WriteLine(msg));
CronTools.Register(tools, cron);

var bus = new MessageBus(workDir, msg => Console.WriteLine(msg));
TeamTools.Register(tools, bus, client, config, workDir, msg => Console.WriteLine(msg));

// ── Worktree registry ───────────────────────────────────
var worktrees = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

string ValidateName(string name) =>
    System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_\-]{1,32}$")
        ? name
        : throw new ArgumentException("Invalid worktree name (alphanumeric, dash, underscore; <=32 chars)");

tools.Register("create_worktree", "Create an isolated working directory and bind it to a task.",
    SchemaBuilder.Object("Create an isolated working directory and bind it to a task.",
        new Dictionary<string, (string, string, bool)>
        {
            ["name"] = ("string", "Short identifier (alphanumeric)", true),
            ["task_id"] = ("string", "Task ID to bind this worktree to", false),
        }),
    input =>
    {
        var name = ValidateName(input.GetProperty("name").GetString() ?? "");
        var path = Path.Combine(worktreesDir, name);
        if (worktrees.ContainsKey(name)) return $"Error: worktree '{name}' already exists";
        Directory.CreateDirectory(path);
        worktrees[name] = path;
        if (input.TryGetProperty("task_id", out var tid) && tid.ValueKind == JsonValueKind.String)
        {
            var id = tid.GetString() ?? "";
            try
            {
                var task = store.Load(id);
                // Stash worktree reference in the task description for visibility
                task.Description = string.IsNullOrEmpty(task.Description)
                    ? $"(worktree: {name})"
                    : $"{task.Description}\n(worktree: {name})";
                store.Save(task);
            }
            catch { /* task may not exist yet */ }
        }
        return $"Created worktree '{name}' at {path}";
    });

tools.Register("remove_worktree", "Remove an isolated working directory.",
    SchemaBuilder.Object("Remove an isolated working directory.",
        new Dictionary<string, (string, string, bool)>
        {
            ["name"] = ("string", "Worktree name", true),
            ["force"] = ("boolean", "Force removal even if not empty (default false)", false),
        }),
    input =>
    {
        var name = input.GetProperty("name").GetString() ?? "";
        if (!worktrees.TryRemove(name, out var path)) return $"Error: worktree '{name}' not found";
        var force = input.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() && !force)
            return $"Error: worktree '{name}' not empty; pass force=true";
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return $"Removed worktree '{name}'";
    });

tools.Register("list_worktrees", "List all worktrees.", SchemaBuilder.Object("List all worktrees.",
    new Dictionary<string, (string, string, bool)>()),
    _ => worktrees.Count == 0
        ? "(no worktrees)"
        : string.Join("\n", worktrees.Select(kv => $"- {kv.Key} -> {kv.Value}")));

// ── Scout that picks tasks, creates a worktree, spawns a worker
//    scoped to that worktree.
var cts = new CancellationTokenSource();
var scout = new Thread(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            var candidate = store.List().FirstOrDefault(t =>
                t.Status == "pending"
                && string.IsNullOrEmpty(t.Owner)
                && store.CanStart(t));
            if (candidate is not null)
            {
                var wtName = $"wt_{candidate.Id.Replace("task_", "")}";
                try { ValidateName(wtName); } catch { wtName = wtName.Replace("-", "_"); }
                var wtPath = Path.Combine(worktreesDir, wtName);
                Directory.CreateDirectory(wtPath);
                worktrees[wtName] = wtPath;

                store.Claim(candidate.Id, owner: wtName);

                var name = $"worker_{candidate.Id}";
                var prompt = $"Take ownership of task {candidate.Id}: {candidate.Subject}. " +
                             $"Work inside {wtPath}. Complete it and report back.";
                var runner = new TeammateRunner(client, config, wtPath, bus, name, "worker",
                    onLog: msg => Console.WriteLine(msg), maxRounds: 12);
                var t = new Thread(async () =>
                {
                    try { await runner.RunAsync(prompt, cts.Token); }
                    finally
                    {
                        var (ok, _, _) = store.Complete(candidate.Id);
                        if (ok) Console.WriteLine($"\u001b[32m[scout] auto-completed {candidate.Id}\u001b[0m");
                    }
                }) { IsBackground = true, Name = name };
                t.Start();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\u001b[31m[scout] {ex.GetType().Name}: {ex.Message}\u001b[0m");
        }
        try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token); } catch { break; }
    }
}) { IsBackground = true, Name = "worktree-scout" };
scout.Start();

var compactor = new ContextCompactor(client, config, workDir, msg => Console.WriteLine(msg));
var system = $"You are the lead coding agent at {workDir}. " +
             "Create tasks; the scout will create a worktree and spawn a worker for each.";
var agent = new AgentHarness(client, tools, system)
{
    OnLog = Console.WriteLine,
    Permissions = new AllowAllPermissions(),
    Compactor = compactor,
    MaxTokensEscalation = config.MaxTokensEscalation,
};

agent.Hooks.OnBeforeLlmCall(messages =>
{
    var done = background.DrainCompleted();
    if (done.Count > 0)
        messages.Add(Message.UserText(background.FormatNotifications(done)));
    foreach (var j in cron.DrainQueue())
        messages.Add(Message.UserText($"<cron-fire id=\"{j.Id}\">{j.Prompt}</cron-fire>"));
    var inbox = bus.ReadInbox("lead");
    if (inbox.Count > 0)
        messages.Add(Message.UserText("<inbox>\n" + JsonSerializer.Serialize(inbox, new JsonSerializerOptions { WriteIndented = true }) + "\n</inbox>"));
});

Console.WriteLine("s18: Worktree Isolation — each task owns its directory");
Console.WriteLine("Type a task. The scout will create worktrees and dispatch workers. Type q to quit.\n");
await Repl.RunAsync(agent, "\u001b[36ms18 >> \u001b[0m");
cts.Cancel();
