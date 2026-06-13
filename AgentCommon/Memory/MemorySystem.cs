using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCommon.Llm;
using AgentCommon.Messages;

namespace AgentCommon.Memory;

public sealed class MemoryFile
{
    public string Filename { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "user";   // user | feedback | project | reference
    public string Body { get; set; } = "";
}

public sealed class MemoryStore
{
    private readonly string _memoryDir;
    private readonly string _indexPath;

    public MemoryStore(string workDir)
    {
        _memoryDir = Path.Combine(workDir, ".memory");
        Directory.CreateDirectory(_memoryDir);
        _indexPath = Path.Combine(_memoryDir, "MEMORY.md");
    }

    public string MemoryDir => _memoryDir;

    public IReadOnlyList<MemoryFile> List()
    {
        var files = new List<MemoryFile>();
        foreach (var f in Directory.EnumerateFiles(_memoryDir, "*.md"))
        {
            if (Path.GetFileName(f) == "MEMORY.md") continue;
            var raw = File.ReadAllText(f);
            var (meta, body) = ParseFrontmatter(raw);
            files.Add(new MemoryFile
            {
                Filename = Path.GetFileName(f),
                Name = meta.TryGetValue("name", out var n) ? n : Path.GetFileNameWithoutExtension(f),
                Description = meta.TryGetValue("description", out var d) ? d : "",
                Type = meta.TryGetValue("type", out var t) ? t : "user",
                Body = body,
            });
        }
        return files;
    }

    public string? ReadContent(string filename)
    {
        var path = Path.Combine(_memoryDir, filename);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Write(MemoryFile mem)
    {
        var slug = Slugify(mem.Name);
        var filename = string.IsNullOrEmpty(slug) ? Guid.NewGuid().ToString("n") : slug + ".md";
        mem.Filename = filename;
        var path = Path.Combine(_memoryDir, filename);
        var content = $"---\nname: {mem.Name}\ndescription: {mem.Description}\ntype: {mem.Type}\n---\n\n{mem.Body}\n";
        File.WriteAllText(path, content);
        RebuildIndex();
    }

    public void RebuildIndex()
    {
        var lines = new List<string>();
        foreach (var f in List())
        {
            lines.Add($"- [{f.Name}]({f.Filename}) — {f.Description}");
        }
        File.WriteAllText(_indexPath, string.Join("\n", lines) + (lines.Count > 0 ? "\n" : ""));
    }

    public string IndexText()
    {
        if (!File.Exists(_indexPath)) return "";
        var t = File.ReadAllText(_indexPath).Trim();
        return string.IsNullOrEmpty(t) ? "" : t;
    }

    public void ClearAll()
    {
        foreach (var f in Directory.EnumerateFiles(_memoryDir, "*.md"))
        {
            if (Path.GetFileName(f) == "MEMORY.md") continue;
            File.Delete(f);
        }
        RebuildIndex();
    }

    private static string Slugify(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    private static (Dictionary<string, string> meta, string body) ParseFrontmatter(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith("---")) return (meta, text);
        var parts = text.Split("---", 3);
        if (parts.Length < 3) return (meta, text);
        foreach (var rawLine in parts[1].Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim().Trim('"', '\'');
            meta[key] = val;
        }
        return (meta, parts[2].Trim());
    }
}

public sealed class MemorySelector
{
    private readonly DeepSeekClient _client;
    private readonly MemoryStore _store;

    public MemorySelector(DeepSeekClient client, MemoryStore store)
    {
        _client = client;
        _store = store;
    }

    public async Task<List<string>> SelectRelevantAsync(IReadOnlyList<Message> messages, int maxItems = 5, CancellationToken ct = default)
    {
        var files = _store.List();
        if (files.Count == 0) return new List<string>();

        var recentText = CollectRecentText(messages);
        if (string.IsNullOrWhiteSpace(recentText)) return new List<string>();

        var catalog = string.Join("\n", files.Select((f, i) => $"{i}: {f.Name} — {f.Description}"));
        var prompt =
            "Given the recent conversation and the memory catalog below, " +
            "select the indices of memories that are clearly relevant. " +
            "Return ONLY a JSON array of integers, e.g. [0, 3]. " +
            "If none are relevant, return [].\n\n" +
            $"Recent conversation:\n{recentText}\n\n" +
            $"Memory catalog:\n{catalog}";

        try
        {
            var resp = await _client.CreateMessageAsync(
                systemPrompt: "You are a memory router.",
                messages: new List<Message> { Message.UserText(prompt) },
                tools: null,
                maxTokensOverride: 200,
                ct: ct);
            var text = string.Join("\n", resp.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
            var match = Regex.Match(text, @"\[.*?\]", RegexOptions.Singleline);
            if (match.Success)
            {
                var indices = JsonSerializer.Deserialize<List<int>>(match.Value) ?? new();
                return indices
                    .Where(i => i >= 0 && i < files.Count)
                    .Take(maxItems)
                    .Select(i => files[i].Filename)
                    .ToList();
            }
        }
        catch
        {
            // Fall through to keyword fallback
        }

        return KeywordFallback(recentText, files, maxItems);
    }

    private List<string> KeywordFallback(string recent, IReadOnlyList<MemoryFile> files, int maxItems)
    {
        var keywords = recent.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3).Select(w => w.ToLowerInvariant()).ToHashSet();
        var hits = new List<string>();
        foreach (var f in files)
        {
            var haystack = (f.Name + " " + f.Description).ToLowerInvariant();
            if (keywords.Any(k => haystack.Contains(k)))
            {
                hits.Add(f.Filename);
                if (hits.Count >= maxItems) break;
            }
        }
        return hits;
    }

    private static string CollectRecentText(IReadOnlyList<Message> messages)
    {
        var texts = new List<string>();
        for (var i = messages.Count - 1; i >= 0 && texts.Count < 3; i--)
        {
            var m = messages[i];
            if (m.Role != "user") continue;
            var t = string.Join(" ", m.Content.OfType<TextBlock>().Select(b => b.Text));
            if (!string.IsNullOrWhiteSpace(t)) texts.Add(t);
        }
        texts.Reverse();
        return string.Join(" ", texts)[..Math.Min(2000, string.Join(" ", texts).Length)];
    }
}

public sealed class MemoryExtractor
{
    private readonly DeepSeekClient _client;
    private readonly MemoryStore _store;
    private readonly Action<string>? _onLog;

    public MemoryExtractor(DeepSeekClient client, MemoryStore store, Action<string>? onLog = null)
    {
        _client = client;
        _store = store;
        _onLog = onLog;
    }

    public async Task<int> ExtractFromAsync(IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        var dialogue = new StringBuilder();
        foreach (var m in messages.TakeLast(10))
        {
            var t = string.Join(" ", m.Content.OfType<TextBlock>().Select(b => b.Text));
            if (!string.IsNullOrWhiteSpace(t))
            {
                dialogue.AppendLine($"{m.Role}: {t}");
            }
        }
        if (dialogue.Length == 0) return 0;

        var existing = _store.List();
        var existingDesc = existing.Count == 0
            ? "(none)"
            : string.Join("\n", existing.Select(m => $"- {m.Name}: {m.Description}"));

        var prompt =
            "Extract user preferences, constraints, or project facts from this dialogue.\n" +
            "Return a JSON array. Each item: {name, type, description, body}.\n" +
            "- name: short kebab-case identifier\n" +
            "- type: one of 'user' (user preference), 'feedback' (guidance), " +
            "'project' (project fact), 'reference' (external pointer)\n" +
            "- description: one-line summary for index lookup\n" +
            "- body: full detail in markdown\n" +
            "If nothing new or already covered, return [].\n\n" +
            $"Existing memories:\n{existingDesc}\n\n" +
            $"Dialogue:\n{dialogue.ToString()[..Math.Min(4000, dialogue.Length)]}";

        try
        {
            var resp = await _client.CreateMessageAsync(
                systemPrompt: "You extract structured memory items.",
                messages: new List<Message> { Message.UserText(prompt) },
                tools: null,
                maxTokensOverride: 800,
                ct: ct);
            var text = string.Join("\n", resp.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
            var match = Regex.Match(text, @"\[.*\]", RegexOptions.Singleline);
            if (!match.Success) return 0;
            var items = JsonSerializer.Deserialize<List<MemoryFile>>(match.Value, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (items is null) return 0;
            var count = 0;
            foreach (var mem in items)
            {
                if (string.IsNullOrWhiteSpace(mem.Description) || string.IsNullOrWhiteSpace(mem.Body))
                    continue;
                _store.Write(mem);
                count++;
            }
            if (count > 0)
            {
                _onLog?.Invoke($"\n\u001b[33m[Memory: extracted {count} new memories]\u001b[0m");
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }
}

public sealed class MemoryConsolidator
{
    private readonly DeepSeekClient _client;
    private readonly MemoryStore _store;
    private readonly int _threshold;
    private readonly Action<string>? _onLog;

    public MemoryConsolidator(DeepSeekClient client, MemoryStore store, int threshold = 10, Action<string>? onLog = null)
    {
        _client = client;
        _store = store;
        _threshold = threshold;
        _onLog = onLog;
    }

    public async Task<bool> ConsolidateIfNeededAsync(CancellationToken ct = default)
    {
        var files = _store.List();
        if (files.Count < _threshold) return false;

        var catalog = string.Join("\n\n", files.Select(f =>
            $"## {f.Filename}\nname: {f.Name}\ndescription: {f.Description}\n{f.Body}"));

        var prompt =
            "Consolidate the following memory files. Rules:\n" +
            "1. Merge duplicates into one\n" +
            "2. Remove outdated/contradicted memories\n" +
            "3. Keep the total under 30 memories\n" +
            "4. Preserve important user preferences above all\n" +
            "Return a JSON array. Each item: {name, type, description, body}.\n\n" +
            $"{catalog[..Math.Min(16000, catalog.Length)]}";

        try
        {
            var resp = await _client.CreateMessageAsync(
                systemPrompt: "You consolidate memory files.",
                messages: new List<Message> { Message.UserText(prompt) },
                tools: null,
                maxTokensOverride: 3000,
                ct: ct);
            var text = string.Join("\n", resp.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
            var match = Regex.Match(text, @"\[.*\]", RegexOptions.Singleline);
            if (!match.Success) return false;
            var items = JsonSerializer.Deserialize<List<MemoryFile>>(match.Value, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (items is null) return false;

            _store.ClearAll();
            foreach (var mem in items)
            {
                if (string.IsNullOrWhiteSpace(mem.Description) || string.IsNullOrWhiteSpace(mem.Body))
                    continue;
                _store.Write(mem);
            }
            _onLog?.Invoke($"\n\u001b[33m[Memory: consolidated {files.Count} → {items.Count} memories]\u001b[0m");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
