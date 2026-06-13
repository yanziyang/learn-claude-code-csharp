using System.Text.Json;

namespace AgentCommon.Config;

public static class AgentConfigLoader
{
    public static AgentConfig Load(string? explicitPath = null)
    {
        var path = explicitPath ?? FindConfigFile();
        if (path is null || !File.Exists(path))
        {
            return new AgentConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AgentConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            return cfg ?? new AgentConfig();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load config file '{path}': {ex.Message}", ex);
        }
    }

    private static string? FindConfigFile()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }
            dir = parent.FullName;
        }
        return null;
    }

    /// <summary>
    /// Returns the project source directory (the folder containing the
    /// chapter's .csproj), found by walking up from AppContext.BaseDirectory.
    /// Useful for resolving relative hook-script paths when the user invokes
    /// the chapter from a different cwd (e.g. the repo root).
    /// Returns null if no .csproj is found within 8 levels.
    /// </summary>
    public static string? ResolveProjectDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            if (Directory.EnumerateFiles(dir, "*.csproj").Any())
            {
                return dir;
            }
            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }
            dir = parent.FullName;
        }
        return null;
    }
}
