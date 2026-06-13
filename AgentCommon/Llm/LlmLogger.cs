using System.Text.Json;

namespace AgentCommon.Llm;

public static class LlmLogger
{
    private static int _seq;
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly string LogDir =
        Path.Combine(Directory.GetCurrentDirectory(), "logs", "llm");

    public static int NextSeq() => Interlocked.Increment(ref _seq);

    public static async Task LogRequestAsync(int seq, string url, string model, string body)
    {
        await WriteAsync($"req-{seq:D4}.json", new
        {
            seq,
            ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            url,
            model,
            body = ParseOrRaw(body),
        });
    }

    public static async Task LogResponseAsync(int seq, int status, string body)
    {
        await WriteAsync($"resp-{seq:D4}.json", new
        {
            seq,
            ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            status,
            body = ParseOrRaw(body),
        });
    }

    private static object ParseOrRaw(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch
        {
            return body;
        }
    }

    private static async Task WriteAsync(string fileName, object content)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, fileName);
            var json = JsonSerializer.Serialize(content, PrettyJson);
            await File.WriteAllTextAsync(path, json);
        }
        catch
        {
        }
    }
}
