using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCommon.Teams;

public sealed class MailboxMessage
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "message";

    [JsonPropertyName("ts")]
    public double Ts { get; set; }
}

public sealed class MessageBus
{
    private readonly string _mailboxDir;
    private readonly object _lock = new();
    private readonly Action<string>? _onLog;

    public MessageBus(string workDir, Action<string>? onLog = null)
    {
        _mailboxDir = Path.Combine(workDir, ".mailboxes");
        Directory.CreateDirectory(_mailboxDir);
        _onLog = onLog;
    }

    public string MailboxDir => _mailboxDir;

    public void Send(string from, string to, string content, string msgType = "message")
    {
        var msg = new MailboxMessage
        {
            From = from,
            To = to,
            Content = content,
            Type = msgType,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };
        var inbox = Path.Combine(_mailboxDir, $"{to}.jsonl");
        lock (_lock)
        {
            File.AppendAllText(inbox, JsonSerializer.Serialize(msg) + "\n");
        }
        _onLog?.Invoke($"\u001b[33m[bus] {from} → {to}: {Trunc(content, 50)}\u001b[0m");
    }

    public List<MailboxMessage> ReadInbox(string agent)
    {
        var inbox = Path.Combine(_mailboxDir, $"{agent}.jsonl");
        if (!File.Exists(inbox)) return new List<MailboxMessage>();
        List<MailboxMessage> msgs;
        lock (_lock)
        {
            var lines = File.ReadAllText(inbox).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            msgs = lines.Select(l => JsonSerializer.Deserialize<MailboxMessage>(l)!).ToList();
            File.Delete(inbox); // consume: read + delete
        }
        return msgs;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
