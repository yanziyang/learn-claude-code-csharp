using System.Text.Json.Serialization;

namespace AgentCommon.Config;

public sealed class AgentConfig
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "deepseek-v4-flash";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://api.deepseek.com/anthropic";

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 8000;

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("maxTokensEscalation")]
    public int? MaxTokensEscalation { get; set; }

    [JsonPropertyName("fallbackModel")]
    public string? FallbackModel { get; set; }

    [JsonPropertyName("hooks")]
    public HooksConfig? Hooks { get; set; }

    public string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey) && ApiKey != "PUT-YOUR-KEY-HERE")
        {
            return ApiKey;
        }

        var env = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
                  ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        throw new InvalidOperationException(
            "No API key configured. " +
            "Edit appsettings.json and set ApiKey, " +
            "or set the DEEPSEEK_API_KEY environment variable. " +
            "See appsettings.example.json for a template.");
    }
}
