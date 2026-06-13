# s12: Worktree + Task Isolation (Worktree 任务隔离)

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > [ s12 ]`

> *"各干各的目录, 互不干扰"* -- 任务管目标, worktree 管目录, 按 ID 绑定。
>
> **Harness 层**: 目录隔离 -- 永不碰撞的并行执行通道。

## 问题

到 s11, Agent 已经能自主认领和完成任务。但所有任务共享一个目录。两个 Agent 同时重构不同模块 -- A 改 `Config.cs`, B 也改 `Config.cs`, 未提交的改动互相污染, 谁也没法干净回滚。

任务板管 "做什么" 但不管 "在哪做"。解法: 给每个任务一个独立的工作目录, 用任务 ID 把两边关联起来。

## 解决方案

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

## 工作原理

1. **创建任务。** 先把目标持久化。

```csharp
var task = store.Create("Implement auth refactor");
// → .tasks/task_<ts>_<rand>.json  status=pending  blockedBy=[]
```

2. **创建 worktree 并绑定任务。** 传入 `task_id` 自动将任务推进到 `in_progress` (`s18/Program.cs`)。

```csharp
var wtName = $"wt_auth_refactor";
Directory.CreateDirectory(Path.Combine(worktreesDir, wtName));
worktrees[wtName] = wtPath;
store.Claim(task.Id, owner: wtName);   // status: pending -> in_progress
```

绑定同时写入两侧状态:

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

3. **在 worktree 中执行命令。** 队友的 `TeammateRunner` 以 worktree 路径构造, 因此所有工具调用 (bash, 读写文件, glob) 都以该目录为根。

```csharp
var runner = new TeammateRunner(client, config, worktreePath, bus, name, "worker",
    onLog: msg => Console.WriteLine(msg), maxRounds: 12);
```

4. **收尾。** 两种选择:
   - `keep_worktree(name)` -- 保留目录供后续使用。
   - `remove_worktree(name, complete_task=true)` -- 删除目录, 完成绑定任务。一个调用搞定拆除 + 完成。

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

> **Note:** 教学版用普通子目录, 不依赖 git 仓库; 生产版会把 `.worktrees/<name>` 绑定到 `git worktree add -b wt/<name> .worktrees/<name> HEAD`。

崩溃后从 `.tasks/` + `.worktrees/index.json` 重建现场。会话记忆是易失的; 磁盘状态是持久的。

## 相对 s11 的变更

| 组件               | 之前 (s11)                 | 之后 (s12)                                   |
|--------------------|----------------------------|----------------------------------------------|
| 协调               | 任务板 (owner/status)      | 任务板 + worktree 显式绑定                   |
| 执行范围           | 共享目录                   | 每个任务独立目录                             |
| 可恢复性           | 仅任务状态                 | 任务状态 + worktree 索引                     |
| 收尾               | 任务完成                   | 任务完成 + 显式 keep/remove                  |
| 生命周期可见性     | 隐式日志                   | 显式 `list_worktrees` / 事件钩子             |

## 试一试

```sh
cd learn-claude-code-csharp
dotnet run --project s18_worktree_isolation
```

试试这些 prompt (英文 prompt 对 LLM 效果更好, 也可以用中文):

1. `Create tasks for backend auth and frontend login page, then list tasks.`
2. `Create worktree "auth-refactor" for task 1, then bind task 2 to a new worktree "ui-login".`
3. `Run "git status --short" in worktree "auth-refactor".`
4. `Keep worktree "ui-login", then list worktrees.`
5. `Remove worktree "auth-refactor" with force=true, then list tasks and worktrees.`
