using System.Text.Json;
using AgentCommon.Tools;

namespace AgentCommon.Defaults;

public sealed class TodoItem
{
    public string content { get; set; } = "";
    public string status { get; set; } = "pending"; // pending | in_progress | completed
}

public static class TodoTools
{
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
            Console.WriteLine("\n\u001b[33m## Current Tasks\u001b[0m");
            foreach (var t in Items)
            {
                var icon = t.status switch
                {
                    "completed" => "\u001b[32m\u2713\u001b[0m",
                    "in_progress" => "\u001b[36m\u25b8\u001b[0m",
                    _ => " ",
                };
                Console.WriteLine($"  [{icon}] {t.content}");
            }
        }
    }

    public static void Register(ToolRegistry tools, TodoState state)
    {
        var itemSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                content = new { type = "string", description = "The task description" },
                status = new { type = "string", @enum = new[] { "pending", "in_progress", "completed" } },
            },
            required = new[] { "content", "status" },
        });
        var arraySchema = JsonSerializer.SerializeToElement(new
        {
            type = "array",
            items = itemSchema,
        });
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { todos = arraySchema },
            required = new[] { "todos" },
        });

        tools.Register("todo_write", "Create and manage a task list for your current coding session.", schema, input =>
        {
            try
            {
                var todos = new List<TodoItem>();
                if (input.TryGetProperty("todos", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var content = el.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                        var status = el.TryGetProperty("status", out var s) ? s.GetString() ?? "pending" : "pending";
                        if (status is not ("pending" or "in_progress" or "completed"))
                        {
                            return $"Error: invalid status '{status}'";
                        }
                        todos.Add(new TodoItem { content = content, status = status });
                    }
                }
                state.Update(todos);
                state.Render();
                return $"Updated {todos.Count} tasks";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        });
    }
}
