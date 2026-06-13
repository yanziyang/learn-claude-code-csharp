using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCommon.Messages;

[JsonConverter(typeof(ContentBlockConverter))]
public abstract record ContentBlock
{
}

public sealed record TextBlock : ContentBlock
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    public TextBlock() { }
    public TextBlock(string text) { Text = text; }
}

public sealed record ToolUseBlock : ContentBlock
{
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

public sealed record RawContentBlock : ContentBlock
{
    [JsonPropertyName("raw")]
    public JsonElement Raw { get; init; }

    public RawContentBlock() { }
    public RawContentBlock(JsonElement raw) { Raw = raw; }
}

public sealed class ContentBlockConverter : JsonConverter<ContentBlock>
{
    public override ContentBlock? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
        {
            return new RawContentBlock(root.Clone());
        }

        var type = typeProp.GetString();
        switch (type)
        {
            case "text":
                return new TextBlock
                {
                    Text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "",
                };
            case "tool_use":
                return new ToolUseBlock
                {
                    Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "",
                    Name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "",
                    Input = root.TryGetProperty("input", out var i) ? i.Clone() : default,
                };
            case "tool_result":
                return new ToolResultBlock
                {
                    ToolUseId = root.TryGetProperty("tool_use_id", out var tu) && tu.ValueKind == JsonValueKind.String ? tu.GetString() ?? "" : "",
                    Content = root.TryGetProperty("content", out var c) ? c.ToString() : "",
                    IsError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True ? (bool?)true : null,
                };
            default:
                return new RawContentBlock(root.Clone());
        }
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case TextBlock t:
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", t.Text);
                writer.WriteEndObject();
                break;
            case ToolUseBlock tu:
                writer.WriteStartObject();
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", tu.Id);
                writer.WriteString("name", tu.Name);
                writer.WritePropertyName("input");
                tu.Input.WriteTo(writer);
                writer.WriteEndObject();
                break;
            case ToolResultBlock tr:
                writer.WriteStartObject();
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", tr.ToolUseId);
                writer.WriteString("content", tr.Content);
                if (tr.IsError == true)
                {
                    writer.WriteBoolean("is_error", true);
                }
                writer.WriteEndObject();
                break;
            case RawContentBlock raw:
                raw.Raw.WriteTo(writer);
                break;
            default:
                throw new JsonException($"Unknown ContentBlock subtype: {value?.GetType().FullName ?? "null"}");
        }
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
