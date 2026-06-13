# s03: TodoWrite (待办写入)

`s01 > s02 > [ s03 ] s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"没有计划的 agent 走哪算哪"* -- 先列步骤再动手, 完成率翻倍。
>
> **Harness 层**: 规划 -- 让模型不偏航, 但不替它画航线。

## 问题

多步任务中, 模型会丢失进度 -- 重复做过的事、跳步、跑偏。对话越长越严重: 工具结果不断填满上下文, 系统提示的影响力逐渐被稀释。一个 10 步重构可能做完 1-3 步就开始即兴发挥, 因为 4-10 步已经被挤出注意力了。

## 解决方案

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

## 工作原理

1. `TodoState` 存储带状态的项目。同一时间只允许一个 `in_progress`。

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

2. `todo_write` 工具和其他工具一样加入注册。

```csharp
var todos = new TodoTools.TodoState();
TodoTools.Register(tools, todos);
```

3. 更新后, 渲染结果以 `tool_result` 形式回显给模型, 让模型和用户看到同一份视图。

```csharp
tools.Register("todo_write", "Create and manage a task list for your current coding session.",
    /* schema */, input =>
    {
        var items = ParseTodos(input);
        state.Update(items);
        state.Render();      // 同一视图回灌到模型上下文
        return $"Updated {items.Count} tasks";
    });
```

"同时只能有一个 in_progress" 强制顺序聚焦。回显到 result 制造问责压力。

## 相对 s02 的变更

| 组件           | 之前 (s02)       | 之后 (s03)                     |
|----------------|------------------|--------------------------------|
| Tools          | 4                | 5 (+todo_write)                |
| 规划           | 无               | `TodoState` with statuses      |
| 回显           | 无               | 渲染结果写入 result            |
| Agent loop     | 简单分发         | 不变                           |

## 试一试

```sh
cd learn-claude-code-csharp
dotnet run --project s05_todo_write
```

试试这些 prompt (英文 prompt 对 LLM 效果更好, 也可以用中文):

1. `Refactor the file hello.cs: add XML doc comments and a main guard`
2. `Create a small class library with Class1.cs, Utils.cs, and tests/UtilsTests.cs`
3. `Review all C# files in this project and fix any obvious style issues`
