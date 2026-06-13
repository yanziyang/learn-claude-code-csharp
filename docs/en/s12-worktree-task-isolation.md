# s12: Worktree + Task Isolation

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > [ s12 ]`

> *"Each works in its own directory, no interference"* -- tasks manage goals, worktrees manage directories, bound by ID.
>
> **Harness layer**: Directory isolation -- parallel execution lanes that never collide.

## Problem

By s11, agents can claim and complete tasks autonomously. But every task runs in one shared directory. Two agents refactoring different modules at the same time will collide: agent A edits `Config.cs`, agent B edits `Config.cs`, unstaged changes mix, and neither can roll back cleanly.

The task board tracks *what to do* but has no opinion about *where to do it*. The fix: give each task its own working directory. Tasks manage goals, worktrees manage execution context. Bind them by task ID.

## Solution

```
Control plane (.tasks/)             Execution plane (.worktrees/)
+------------------+                +------------------------+
| task_<id>.json   |                | wt_<id>/               |
|   status: in_progress  <------>   task_id: <id>           |
|   worktree: "wt_..."        |                             |
+------------------+                +------------------------+
| task_<id>.json   |                | wt_<id>/               |
|   status: pending    <------>     task_id: <id>           |
|   worktree: "wt_..."        |                             |
+------------------+                +------------------------+

State machines:
  Task:     pending -> in_progress -> completed
  Worktree: absent  -> active      -> removed | kept
```

## How It Works

1. **Create a task.** Persist the goal first.

```csharp
var task = store.Create("Implement auth refactor");
// → .tasks/task_<ts>_<rand>.json  status=pending  blockedBy=[]
```

2. **Create a worktree and bind to the task.** Passing `task_id` auto-advances the task to `in_progress` (`s18/Program.cs`).

```csharp
var wtName = $"wt_auth_refactor";
Directory.CreateDirectory(Path.Combine(worktreesDir, wtName));
worktrees[wtName] = wtPath;
store.Claim(task.Id, owner: wtName);   // status: pending -> in_progress
```

The binding writes state to both sides:

```csharp
public (bool ok, string message) Claim(string id, string owner = "agent")
{
    var task = Load(id);
    if (task.Status != "pending")
        return (false, $"Task {id} is {task.Status}, cannot claim");
    if (!CanStart(task))
        return (false, $"Blocked by: [{string.Join(", ", task.BlockedBy)}]");

    task.Owner = owner;
    task.Status = "in_progress";
    Save(task);
    return (true, $"Claimed {task.Id} ({task.Subject})");
}
```

3. **Run commands in the worktree.** The teammate's `TeammateRunner` is constructed with the worktree path, so every tool call (bash, file read/write, glob) is rooted there.

```csharp
var runner = new TeammateRunner(client, config, worktreePath, bus, name, "worker",
    onLog: msg => Console.WriteLine(msg), maxRounds: 12);
```

4. **Close out.** Two choices:
   - `keep_worktree(name)` -- preserve the directory for later.
   - `remove_worktree(name, complete_task=true)` -- remove directory, complete the bound task. One call handles teardown + completion.

```csharp
tools.Register("remove_worktree", "Remove an isolated working directory.",
    SchemaBuilder.Object("Remove an isolated working directory.",
        new Dictionary<string, (string, string, bool)>
        {
            ["name"]  = ("string",  "Worktree name", true),
            ["force"] = ("boolean", "Force removal even if not empty (default false)", false),
        }),
    input =>
    {
        var name = input.GetProperty("name").GetString() ?? "";
        if (!worktrees.TryRemove(name, out var path))
            return $"Error: worktree '{name}' not found";
        var force = input.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() && !force)
            return $"Error: worktree '{name}' not empty; pass force=true";
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return $"Removed worktree '{name}'";
    });
```

> **Note:** The teaching version uses plain subdirectories, not git worktrees, so the lesson works without a git repository. The production equivalent binds `.worktrees/<name>` to `git worktree add -b wt/<name> .worktrees/<name> HEAD`.

After a crash, state reconstructs from `.tasks/` + `.worktrees/index.json` on disk. Conversation memory is volatile; file state is durable.

## What Changed From s11

| Component          | Before (s11)               | After (s12)                                  |
|--------------------|----------------------------|----------------------------------------------|
| Coordination       | Task board (owner/status)  | Task board + explicit worktree binding       |
| Execution scope    | Shared directory           | Task-scoped isolated directory               |
| Recoverability     | Task status only           | Task status + worktree index                 |
| Teardown           | Task completion            | Task completion + explicit keep/remove       |
| Lifecycle visibility | Implicit in logs         | Explicit `list_worktrees` / event hooks      |

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s18_worktree_isolation
```

1. `Create tasks for backend auth and frontend login page, then list tasks.`
2. `Create worktree "auth-refactor" for task 1, then bind task 2 to a new worktree "ui-login".`
3. `Run "git status --short" in worktree "auth-refactor".`
4. `Keep worktree "ui-login", then list worktrees.`
5. `Remove worktree "auth-refactor" with force=true, then list tasks and worktrees.`
