using System.Text.Json;

namespace AgentCommon.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, Tool> _tools = new();

    public void Register(Tool tool)
    {
        _tools[tool.Name] = tool;
    }

    public void Register(string name, string description, JsonElement schema, Func<JsonElement, string> handler)
    {
        _tools[name] = new Tool(name, description, schema, handler);
    }

    public Tool? Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;

    public IEnumerable<Tool> All() => _tools.Values;

    public IEnumerable<Llm.LlmToolSpec> AllSpecs() => _tools.Values.Select(t => t.ToSpec());

    public string Invoke(string name, JsonElement input)
    {
        var tool = Get(name);
        if (tool is null)
        {
            return $"Error: Unknown tool '{name}'";
        }
        try
        {
            return tool.Invoke(input);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
