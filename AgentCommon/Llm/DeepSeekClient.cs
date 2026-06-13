using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCommon.Config;
using AgentCommon.Messages;

namespace AgentCommon.Llm;

public sealed class LlmResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    [JsonPropertyName("content")]
    public List<ContentBlock> Content { get; set; } = new();

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("usage")]
    public JsonElement? Usage { get; set; }
}

public sealed class LlmToolSpec
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; init; }
}

public sealed class DeepSeekClient : IDisposable
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly AgentConfig _config;
    private RetryPolicy? _retryPolicy;

    public DeepSeekClient(AgentConfig config)
    {
        _config = config;
    }

    public void AttachRetryPolicy(RetryPolicy policy) => _retryPolicy = policy;

    public async Task<LlmResponse> CreateMessageAsync(
        string systemPrompt,
        IReadOnlyList<Message> messages,
        IReadOnlyList<LlmToolSpec>? tools = null,
        int? maxTokensOverride = null,
        string? modelOverride = null,
        CancellationToken ct = default)
    {
        var apiKey = _config.ResolveApiKey();
        var url = _config.BaseUrl.TrimEnd('/') + "/v1/messages";

        if (_retryPolicy is null)
        {
            return await SendAsync(apiKey, url, systemPrompt, messages, tools, maxTokensOverride, modelOverride ?? _config.ModelId, ct);
        }

        var state = new RetryPolicy.Result { CurrentModel = modelOverride ?? _config.ModelId };
        return await _retryPolicy.WithRetryAsync(
            mdl => SendAsync(apiKey, url, systemPrompt, messages, tools, maxTokensOverride, mdl, ct),
            () => state.CurrentModel ?? _config.ModelId,
            state,
            ct);
    }

    private async Task<LlmResponse> SendAsync(
        string apiKey,
        string url,
        string systemPrompt,
        IReadOnlyList<Message> messages,
        IReadOnlyList<LlmToolSpec>? tools,
        int? maxTokensOverride,
        string model,
        CancellationToken ct)
    {
        var maxTokens = maxTokensOverride ?? _config.MaxTokens;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["system"] = systemPrompt,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
        };

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOpts),
            Encoding.UTF8,
            "application/json");

        using var resp = await SharedHttp.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LLM call failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {body}");
        }

        var parsed = JsonSerializer.Deserialize<LlmResponse>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        if (parsed is null)
        {
            throw new InvalidOperationException("LLM returned an empty response.");
        }

        return parsed;
    }

    public void Dispose()
    {
        // Use shared HttpClient; nothing to dispose.
    }
}
