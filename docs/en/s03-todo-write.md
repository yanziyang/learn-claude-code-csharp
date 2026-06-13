# s03: TodoWrite

`s01 > s02 > [ s03 ] s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"An agent without a plan drifts"* -- list the steps first, then execute.
>
> **Harness layer**: Planning -- keeping the model on course without scripting the route.

## Problem

On multi-step tasks, the model loses track. It repeats work, skips steps, or wanders off. Long conversations make this worse -- the system prompt fades as tool results fill the context. A 10-step refactoring might complete steps 1-3, then the model starts improvising because it forgot steps 4-10.

## Solution

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

## How It Works

1. A `TodoState` stores items with statuses. Only one item can be `in_progress` at a time.

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

2. The `todo_write` tool goes into the registry like any other tool.

```csharp
var todos = new TodoTools.TodoState();
TodoTools.Register(tools, todos);
```

3. After updating, the rendered list is shown back to the model so it sees the same view the user does.

```csharp
tools.Register("todo_write", "Create and manage a task list for your current coding session.",
    /* schema */, input =>
    {
        var items = ParseTodos(input);
        state.Update(items);
        state.Render();      // mirrored back into the model's context
        return $"Updated {items.Count} tasks";
    });
```

The "one in_progress at a time" constraint forces sequential focus. Rendering the list into the tool result creates accountability.

## What Changed From s02

| Component      | Before (s02)     | After (s03)                |
|----------------|------------------|----------------------------|
| Tools          | 4                | 5 (+todo_write)            |
| Planning       | None             | `TodoState` with statuses  |
| Mirror         | None             | Rendered list into result  |
| Agent loop     | Simple dispatch  | Unchanged                  |

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s05_todo_write
```

1. `Refactor the file hello.cs: add XML doc comments and a main guard`
2. `Create a small class library with Class1.cs, Utils.cs, and tests/UtilsTests.cs`
3. `Review all C# files in this project and fix any obvious style issues`
