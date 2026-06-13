# s07: Task System

`s01 > s02 > s03 > s04 > s05 > s06 | [ s07 ] s08 > s09 > s10 > s11 > s12`

> *"Break big goals into small tasks, order them, persist to disk"* -- a file-based task graph with dependencies, laying the foundation for multi-agent collaboration.
>
> **Harness layer**: Persistent tasks -- goals that outlive any single conversation.

## Problem

s03's TodoState is a flat checklist in memory: no ordering, no dependencies, no status beyond done-or-not. Real goals have structure -- task B depends on task A, tasks C and D can run in parallel, task E waits for both C and D.

Without explicit relationships, the agent can't tell what's ready, what's blocked, or what can run concurrently. And because the list lives only in memory, context compression (s06) wipes it clean.

## Solution

Promote the checklist into a **task graph** persisted to disk. Each task is a JSON file with status, dependencies (`blockedBy`). The graph answers three questions at any moment:

- **What's ready?** -- tasks with `pending` status and empty `blockedBy`.
- **What's blocked?** -- tasks waiting on unfinished dependencies.
- **What's done?** -- `completed` tasks, whose completion automatically unblocks dependents.

```
.tasks/
  task_<ts>_<rand>.json  {"id":"task_...", "status":"completed"}
  task_<ts>_<rand>.json  {"id":"task_...", "blockedBy":["task_..."], "status":"pending"}
  task_<ts>_<rand>.json  {"id":"task_...", "blockedBy":["task_..."], "status":"pending"}
  task_<ts>_<rand>.json  {"id":"task_...", "blockedBy":["task_..."], "status":"pending"}

Task graph (DAG):
                 +----------+
            +--> | task 2   | --+
            |    | pending  |   |
+----------+     +----------+    +--> +----------+
| task 1   |                          | task 4   |
| completed| --> +----------+    +--> | blocked  |
+----------+     | task 3   | --+     +----------+
                 | pending  |
                 +----------+

Ordering:     task 1 must finish before 2 and 3
Parallelism:  tasks 2 and 3 can run at the same time
Dependencies: task 4 waits for both 2 and 3
Status:       pending -> in_progress -> completed
```

This task graph becomes the coordination backbone for everything after s07: background execution (s08), multi-agent teams (s09+), and worktree isolation (s12) all read from and write to this same structure.

## How It Works

1. **`TaskStore`**: one JSON file per task, CRUD with dependency graph (`AgentCommon/Tasks/TaskSystem.cs`).

```csharp
public sealed class TaskStore
{
    private readonly string _tasksDir;

    public TaskStore(string workDir)
    {
        _tasksDir = Path.Combine(workDir, ".tasks");
        Directory.CreateDirectory(_tasksDir);
    }

    public TaskRecord Create(string subject, string description = "",
                             IEnumerable<string>? blockedBy = null)
    {
        var id = $"task_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(0, 9999):D4}";
        var task = new TaskRecord
        {
            Id = id,
            Subject = subject,
            Description = description,
            Status = "pending",
            BlockedBy = blockedBy?.ToList() ?? new(),
        };
        Save(task);
        return task;
    }
}
```

2. **Dependency resolution**: completing a task automatically unblocks dependents that were waiting on it.

```csharp
public (bool ok, string message, IReadOnlyList<TaskRecord> unblocked) Complete(string id)
{
    var task = Load(id);
    if (task.Status != "in_progress")
        return (false, $"Task {id} is {task.Status}, cannot complete", Array.Empty<TaskRecord>());

    task.Status = "completed";
    Save(task);

    var unblocked = List()
        .Where(t => t.Status == "pending"
                 && t.BlockedBy.Count > 0
                 && CanStart(t))
        .ToList();
    return (true, $"Completed {task.Id} ({task.Subject})", unblocked);
}
```

3. **Status + dependency wiring**: `Claim` and `CanStart` enforce the edges.

```csharp
public bool CanStart(TaskRecord task)
{
    foreach (var depId in task.BlockedBy)
    {
        var dep = Load(depId);
        if (dep.Status != "completed") return false;
    }
    return true;
}
```

4. Four task tools go into the dispatch map.

```csharp
var store = new TaskStore(workDir);
TaskTools.Register(tools, store, msg => Console.WriteLine(msg));
// → registers: create_task, list_tasks, get_task, claim_task, complete_task
```

From s07 onward, the task graph is the default for multi-step work. s03's Todo remains for quick single-session checklists.

## What Changed From s06

| Component | Before (s06) | After (s07) |
|---|---|---|
| Tools | 5 | 9 (+create/list/get/claim/complete) |
| Planning model | Flat checklist (in-memory) | Task graph with dependencies (on disk) |
| Relationships | None | `blockedBy` edges |
| Status tracking | Done or not | `pending` -> `in_progress` -> `completed` |
| Persistence | Lost on compression | Survives compression and restarts |

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s12_task_system
```

1. `Create 3 tasks: "Setup project", "Write code", "Write tests". Make them depend on each other in order.`
2. `List all tasks and show the dependency graph`
3. `Complete task 1 and then list tasks to see task 2 unblocked`
4. `Create a task board for refactoring: parse -> transform -> emit -> test, where transform and emit can run in parallel after parse`
