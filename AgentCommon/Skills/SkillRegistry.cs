using System.Text.Json;
using AgentCommon.Tools;

namespace AgentCommon.Skills;

public sealed class SkillManifest
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string FullContent { get; init; } = "";
}

public sealed class SkillRegistry
{
    private readonly Dictionary<string, SkillManifest> _byName = new();

    public static SkillRegistry LoadFromDir(string skillsDir)
    {
        var reg = new SkillRegistry();
        if (!Directory.Exists(skillsDir)) return reg;

        foreach (var dir in Directory.EnumerateDirectories(skillsDir))
        {
            var manifest = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(manifest)) continue;

            var raw = File.ReadAllText(manifest);
            var (name, desc) = ParseFrontmatter(raw);
            if (string.IsNullOrEmpty(name)) name = Path.GetFileName(dir);
            reg._byName[name] = new SkillManifest
            {
                Name = name,
                Description = desc,
                FullContent = raw,
            };
        }
        return reg;
    }

    public IReadOnlyCollection<SkillManifest> All => _byName.Values;

    public string Catalog()
    {
        if (_byName.Count == 0) return "(no skills found)";
        return string.Join("\n", _byName.Values.Select(s => $"- **{s.Name}**: {s.Description}"));
    }

    public SkillManifest? Get(string name) =>
        _byName.TryGetValue(name, out var s) ? s : null;

    public static (string name, string description) ParseFrontmatter(string text)
    {
        if (!text.StartsWith("---")) return ("", FirstLine(text));
        var parts = text.Split("---", 3);
        if (parts.Length < 3) return ("", FirstLine(text));

        var meta = parts[1];
        string name = "", desc = "";
        foreach (var rawLine in meta.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)) name = val;
            else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase)) desc = val;
        }
        return (name, desc);
    }

    private static string FirstLine(string text)
    {
        var first = text.Split('\n', 2)[0].TrimStart('#', ' ', '\t');
        return first;
    }
}

public static class SkillTools
{
    public static void Register(ToolRegistry tools, SkillRegistry registry)
    {
        var schema = SchemaBuilder.Object("Load the full content of a skill by name.",
            new Dictionary<string, (string, string, bool)>
            {
                ["name"] = ("string", "Name of the skill (from the catalog)", true),
            });

        tools.Register("load_skill", "Load the full content of a skill by name.", schema, input =>
        {
            var name = input.GetProperty("name").GetString() ?? "";
            var skill = registry.Get(name);
            return skill is null ? $"Skill not found: {name}" : skill.FullContent;
        });
    }
}
