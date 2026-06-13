# s12: Worktree + Task Isolation

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > [ s12 ]`

> *"各自のディレクトリで作業し、互いに干渉しない"* -- タスクは目標を管理、worktree はディレクトリを管理、IDで紐付け。
>
> **Harness 層**: ディレクトリ隔離 -- 決して衝突しない並列実行レーン。

## 問題

s11までにエージェントはタスクを自律的に確保して完了できるようになった。しかし全タスクが1つの共有ディレクトリで走る。2つのエージェントが同時に異なるモジュールをリファクタリングすると衝突する: 片方が `Config.cs` を編集し、もう片方も `Config.cs` を編集し、未コミットの変更が混ざり合い、どちらもクリーンにロールバックできない。

タスクボードは*何をやるか*を追跡するが、*どこでやるか*には関知しない。解決策: 各タスクに専用の作業ディレクトリを与える。タスクが目標を管理し、worktree が実行コンテキストを管理する。タスクIDで紐付ける。

## 解決策

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

## 仕組み

1. **タスクを作成する。** まず目標を永続化する。

```csharp
var task = store.Create("Implement auth refactor");
// → .tasks/task_<ts>_<rand>.json  status=pending  blockedBy=[]
```

2. **worktree を作成してタスクに紐付ける。** `task_id` を渡すと、タスクが自動的に `in_progress` に遷移する (`s18/Program.cs`)。

```csharp
var wtName = $"wt_auth_refactor";
Directory.CreateDirectory(Path.Combine(worktreesDir, wtName));
worktrees[wtName] = wtPath;
store.Claim(task.Id, owner: wtName);   // status: pending -> in_progress
```

紐付けは両側に状態を書き込む:

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

3. **worktree 内でコマンドを実行する。** チームメイトの `TeammateRunner` は worktree パスで構築されるため、すべてのツール呼び出し (bash, ファイル読み書き, glob) がそこをルートとする。

```csharp
var runner = new TeammateRunner(client, config, worktreePath, bus, name, "worker",
    onLog: msg => Console.WriteLine(msg), maxRounds: 12);
```

4. **終了処理。** 2つの選択肢:
   - `keep_worktree(name)` -- ディレクトリを保持する。
   - `remove_worktree(name, complete_task=true)` -- ディレクトリを削除し、紐付けられたタスクを完了する。1回の呼び出しで後片付けと完了を処理する。

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

> **Note:** 学習版ではプレーンなサブディレクトリを使い、git worktree は使わない(リポジトリなしでレッスンが動くように)。本番版は `.worktrees/<name>` を `git worktree add -b wt/<name> .worktrees/<name> HEAD` にバインドする。

クラッシュ後も `.tasks/` + `.worktrees/index.json` から状態を再構築できる。会話メモリは揮発性だが、ファイル状態は永続的だ。

## s11からの変更点

| Component          | Before (s11)               | After (s12)                                  |
|--------------------|----------------------------|----------------------------------------------|
| Coordination       | Task board (owner/status)  | Task board + explicit worktree binding       |
| Execution scope    | Shared directory           | Task-scoped isolated directory               |
| Recoverability     | Task status only           | Task status + worktree index                 |
| Teardown           | Task completion            | Task completion + explicit keep/remove       |
| Lifecycle visibility | Implicit in logs         | Explicit `list_worktrees` / event hooks      |

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s18_worktree_isolation
```

1. `Create tasks for backend auth and frontend login page, then list tasks.`
2. `Create worktree "auth-refactor" for task 1, then bind task 2 to a new worktree "ui-login".`
3. `Run "git status --short" in worktree "auth-refactor".`
4. `Keep worktree "ui-login", then list worktrees.`
5. `Remove worktree "auth-refactor" with force=true, then list tasks and worktrees.`
