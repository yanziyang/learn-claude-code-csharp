# s07: Task System (任务系统)

`s01 > s02 > s03 > s04 > s05 > s06 | [ s07 ] s08 > s09 > s10 > s11 > s12`

> *"大目标要拆成小任务, 排好序, 记在磁盘上"* -- 文件持久化的任务图, 为多 agent 协作打基础。
>
> **Harness 层**: 持久化任务 -- 比任何一次对话都长命的目标。

## 问题

s03 的 TodoState 只是内存中的扁平清单: 没有顺序、没有依赖、状态只有做完没做完。真实目标是有结构的 -- 任务 B 依赖任务 A, 任务 C 和 D 可以并行, 任务 E 要等 C 和 D 都完成。

没有显式的关系, Agent 分不清什么能做、什么被卡住、什么能同时跑。而且清单只活在内存里, 上下文压缩 (s06) 一跑就没了。

## 解决方案

把扁平清单升级为持久化到磁盘的**任务图**。每个任务是一个 JSON 文件, 有状态、前置依赖 (`blockedBy`)。任务图随时回答三个问题:

- **什么可以做?** -- 状态为 `pending` 且 `blockedBy` 为空的任务。
- **什么被卡住?** -- 等待前置任务完成的任务。
- **什么做完了?** -- 状态为 `completed` 的任务, 完成时自动解锁后续任务。

```
.tasks/
  task_<ts>_<rand>.json  {"id":"task_...", "status":"completed"}
  task_<ts>_<rand>.json  {"id":"task_...", "blockedBy":["task_..."], "status":"pending"}
  task_<ts>_<rand>.json  {"id":"task_...", "blockedBy":["task_..."], "status":"pending"}
  task_<ts>_<rand>.json  {"id":"task_...", "blockedBy":["task_..."], "status":"pending"}

任务图 (DAG):
                 +----------+
            +--> | task 2   | --+
            |    | pending  |   |
+----------+     +----------+    +--> +----------+
| task 1   |                          | task 4   |
| completed| --> +----------+    +--> | blocked  |
+----------+     | task 3   | --+     +----------+
                 | pending  |
                 +----------+

顺序:   task 1 必须先完成, 才能开始 2 和 3
并行:   task 2 和 3 可以同时执行
依赖:   task 4 要等 2 和 3 都完成
状态:   pending -> in_progress -> completed
```

这个任务图是 s07 之后所有机制的协调骨架: 后台执行 (s08)、多 agent 团队 (s09+)、worktree 隔离 (s12) 都读写这同一个结构。

## 工作原理

1. **`TaskStore`**: 每个任务一个 JSON 文件, CRUD + 依赖图 (`AgentCommon/Tasks/TaskSystem.cs`)。

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

2. **依赖解除**: 完成任务时, 自动把它从其他任务的 `blockedBy` 中移除。

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

3. **状态变更 + 依赖关联**: `Claim` 和 `CanStart` 强制依赖边。

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

4. 任务工具注册到分发表。

```csharp
var store = new TaskStore(workDir);
TaskTools.Register(tools, store, msg => Console.WriteLine(msg));
// → 注册: create_task, list_tasks, get_task, claim_task, complete_task
```

从 s07 起, 任务图是多步工作的默认选择。s03 的 Todo 仍可用于单次会话内的快速清单。

## 相对 s06 的变更

| 组件 | 之前 (s06) | 之后 (s07) |
|---|---|---|
| Tools | 5 | 9 (+create/list/get/claim/complete) |
| 规划模型 | 扁平清单 (仅内存) | 带依赖关系的任务图 (磁盘) |
| 关系 | 无 | `blockedBy` 边 |
| 状态追踪 | 做完没做完 | `pending` -> `in_progress` -> `completed` |
| 持久化 | 压缩后丢失 | 压缩和重启后存活 |

## 试一试

```sh
cd learn-claude-code-csharp
dotnet run --project s12_task_system
```

试试这些 prompt (英文 prompt 对 LLM 效果更好, 也可以用中文):

1. `Create 3 tasks: "Setup project", "Write code", "Write tests". Make them depend on each other in order.`
2. `List all tasks and show the dependency graph`
3. `Complete task 1 and then list tasks to see task 2 unblocked`
4. `Create a task board for refactoring: parse -> transform -> emit -> test, where transform and emit can run in parallel after parse`
