using System.Net;

namespace AgentCommon.Llm;

public sealed class RetryPolicy
{
    public int MaxRetries { get; init; } = 10;
    public int BaseDelayMs { get; init; } = 500;
    public int MaxConsecutiveOverloaded { get; init; } = 3;
    public string? FallbackModel { get; init; }
    public Action<string>? OnLog { get; init; }

    public sealed class Result
    {
        public string? CurrentModel { get; set; }
        public int ConsecutiveOverloaded { get; set; }
    }

    public async Task<T> WithRetryAsync<T>(
        Func<string, Task<T>> call,
        Func<string> currentModelProvider,
        Result state,
        CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var result = await call(currentModelProvider());
                state.ConsecutiveOverloaded = 0;
                return result;
            }
            catch (HttpRequestException ex) when (IsTransient(ex))
            {
                await BackoffAsync(attempt, ex, ct);
            }
            catch (InvalidOperationException ex) when (IsRateLimited(ex) || IsOverloaded(ex))
            {
                if (IsOverloaded(ex))
                {
                    state.ConsecutiveOverloaded++;
                    if (state.ConsecutiveOverloaded >= MaxConsecutiveOverloaded && !string.IsNullOrEmpty(FallbackModel))
                    {
                        state.CurrentModel = FallbackModel;
                        state.ConsecutiveOverloaded = 0;
                        OnLog?.Invoke($"\u001b[31m[529 x{MaxConsecutiveOverloaded}] switching to {FallbackModel}\u001b[0m");
                    }
                }
                await BackoffAsync(attempt, ex, ct);
            }
        }
        throw new InvalidOperationException($"Max retries ({MaxRetries}) exceeded");
    }

    private async Task BackoffAsync(int attempt, Exception ex, CancellationToken ct)
    {
        var delay = ComputeDelay(attempt);
        OnLog?.Invoke($"\u001b[33m[{ex.GetType().Name}] retry {attempt + 1}/{MaxRetries}, wait {delay:F1}s\u001b[0m");
        await Task.Delay(TimeSpan.FromSeconds(delay), ct);
    }

    private double ComputeDelay(int attempt)
    {
        var baseDelay = Math.Min(BaseDelayMs * Math.Pow(2, attempt), 32_000) / 1000.0;
        var jitter = Random.Shared.NextDouble() * baseDelay * 0.25;
        return baseDelay + jitter;
    }

    private static bool IsTransient(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.TooManyRequests
                       or HttpStatusCode.ServiceUnavailable
                       or HttpStatusCode.BadGateway
                       or HttpStatusCode.GatewayTimeout;

    private static bool IsRateLimited(Exception ex)
    {
        var msg = ex.Message?.ToLowerInvariant() ?? "";
        return msg.Contains("429") || msg.Contains("rate limit");
    }

    private static bool IsOverloaded(Exception ex)
    {
        var msg = ex.Message?.ToLowerInvariant() ?? "";
        return msg.Contains("529") || msg.Contains("overloaded");
    }
}
