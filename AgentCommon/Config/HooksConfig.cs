using System.Text.Json.Serialization;

namespace AgentCommon.Config;

public sealed class HooksConfig
{
    [JsonPropertyName("PreToolUse")]
    public List<HookGroup>? PreToolUse { get; set; }

    [JsonPropertyName("PostToolUse")]
    public List<HookGroup>? PostToolUse { get; set; }

    [JsonPropertyName("UserPromptSubmit")]
    public List<HookGroup>? UserPromptSubmit { get; set; }

    [JsonPropertyName("Stop")]
    public List<HookGroup>? Stop { get; set; }
}

public sealed class HookGroup
{
    [JsonPropertyName("matcher")]
    public string? Matcher { get; set; }

    [JsonPropertyName("hooks")]
    public List<HookCommand>? Hooks { get; set; }
}

public sealed class HookCommand
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "command";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";
}
