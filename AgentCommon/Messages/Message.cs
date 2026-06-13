using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCommon.Messages;

public abstract record ContentBlock
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed record TextBlock : ContentBlock
{
    public override string Type => "text";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    public TextBlock() { }
    public TextBlock(string text) { Text = text; }
}

public sealed record ToolUseBlock : ContentBlock
{
    public override string Type => "tool_use";

    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("input")]
    public JsonElement Input { get; init; }

    public ToolUseBlock() { }
    public ToolUseBlock(string id, string name, JsonElement input)
    {
        Id = id;
        Name = name;
        Input = input;
    }
}

public sealed record ToolResultBlock : ContentBlock
{
    public override string Type => "tool_result";

    [JsonPropertyName("tool_use_id")]
    public string ToolUseId { get; init; } = "";

    [JsonPropertyName("content")]
    public string Content { get; init; } = "";

    [JsonPropertyName("is_error")]
    public bool? IsError { get; init; }

    public ToolResultBlock() { }
    public ToolResultBlock(string toolUseId, string content, bool? isError = null)
    {
        ToolUseId = toolUseId;
        Content = content;
        IsError = isError;
    }
}

public sealed record Message
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    [JsonPropertyName("content")]
    public List<ContentBlock> Content { get; init; } = new();

    public static Message UserText(string text) => new()
    {
        Role = "user",
        Content = new List<ContentBlock> { new TextBlock(text) },
    };

    public static Message Assistant(IEnumerable<ContentBlock> blocks) => new()
    {
        Role = "assistant",
        Content = blocks.ToList(),
    };

    public static Message UserToolResults(IEnumerable<ToolResultBlock> results) => new()
    {
        Role = "user",
        Content = results.Cast<ContentBlock>().ToList(),
    };
}
