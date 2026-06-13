using System.Text.Json;
using AgentCommon.Llm;

namespace AgentCommon.Tools;

public sealed class Tool
{
    public string Name { get; }
    public string Description { get; }
    public JsonElement InputSchema { get; }
    public Func<JsonElement, string> Handler { get; }

    public Tool(string name, string description, JsonElement inputSchema, Func<JsonElement, string> handler)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
        Handler = handler;
    }

    public LlmToolSpec ToSpec() => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = InputSchema,
    };

    public string Invoke(JsonElement input) => Handler(input);
}

public static class SchemaBuilder
{
    public static JsonElement Object(
        string description,
        Dictionary<string, (string type, string description, bool required)> fields)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var (key, (type, desc, req)) in fields)
        {
            properties[key] = new { type, description = desc };
            if (req)
            {
                required.Add(key);
            }
        }
        var schema = new
        {
            type = "object",
            properties,
            required,
        };
        return JsonSerializer.SerializeToElement(schema);
    }
}
