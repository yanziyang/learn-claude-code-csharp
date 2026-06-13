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
}
