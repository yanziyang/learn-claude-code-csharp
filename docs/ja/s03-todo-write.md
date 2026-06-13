# s03: TodoWrite

`s01 > s02 > [ s03 ] s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"計画のないエージェントは行き当たりばったり"* -- まずステップを書き出し、それから実行。
>
> **Harness 層**: 計画 -- 航路を描かずにモデルを軌道に乗せる。

## 問題

マルチステップのタスクで、モデルは途中で迷子になる。作業を繰り返したり、ステップを飛ばしたり、脱線したりする。長い会話になるほど悪化する -- ツール結果がコンテキストを埋めるにつれ、システムプロンプトの影響力が薄れる。10ステップのリファクタリングでステップ1-3を完了した後、残りを忘れて即興を始めてしまう。

## 解決策

```
+--------+      +-------+      +---------+
|  User  | ---> |  LLM  | ---> | Tools   |
| prompt |      |       |      | + todo  |
+--------+      +---+---+      +----+----+
                    ^                |
                    |   tool_result  |
                    +----------------+
                          |
              +-----------+-----------+
              | TodoState              |
              | [ ] task A            |
              | [>] task B  <- doing  |
              | [x] task C            |
              +-----------------------+
                          |
              if rounds_since_todo >= 3:
                inject <reminder> into tool_result
```

## 仕組み

1. `TodoState` はアイテムのリストをステータス付きで保持する。`in_progress` にできるのは同時に1つだけ。

```csharp
public sealed class TodoState
{
    public List<TodoItem> Items { get; private set; } = new();
    public event Action? Changed;

    public void Update(List<TodoItem> items)
    {
        Items = items;
        Changed?.Invoke();
    }

    public void Render()
    {
        Console.WriteLine("\n## Current Tasks");
        foreach (var t in Items)
        {
            var icon = t.status switch
            {
                "completed"   => "x",
                "in_progress" => ">",
                _             => " ",
            };
            Console.WriteLine($"  [{icon}] {t.content}");
        }
    }
}
```

2. `todo_write` ツールは他のツールと同様に登録される。

```csharp
var todos = new TodoTools.TodoState();
TodoTools.Register(tools, todos);
```

3. 更新後、レンダリング結果がモデル自身にも見えるよう `tool_result` として返る。

```csharp
tools.Register("todo_write", "Create and manage a task list for your current coding session.",
    /* schema */, input =>
    {
        var items = ParseTodos(input);
        state.Update(items);
        state.Render();      // 同じビューをモデル側にもミラー
        return $"Updated {items.Count} tasks";
    });
```

「一度に in_progress は1つだけ」の制約が逐次的な集中を強制する。レンダリング結果をコンテキストにミラーすることで説明責任が生まれる。

## s02からの変更点

| Component      | Before (s02)     | After (s03)                |
|----------------|------------------|----------------------------|
| Tools          | 4                | 5 (+todo_write)            |
| Planning       | None             | `TodoState` with statuses  |
| Mirror         | None             | レンダリング結果を result に|
| Agent loop     | Simple dispatch  | Unchanged                  |

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s05_todo_write
```

1. `Refactor the file hello.cs: add XML doc comments and a main guard`
2. `Create a small class library with Class1.cs, Utils.cs, and tests/UtilsTests.cs`
3. `Review all C# files in this project and fix any obvious style issues`
