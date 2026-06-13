using System.Text.RegularExpressions;

namespace AgentCommon.Hooks;

/// <summary>
/// Mirrors Claude Code's matcher syntax:
///   - empty / null: matches every tool
///   - "bash"      : exact name match
///   - "bash*"     : prefix match (everything starting with "bash")
///   - "/regex/"   : regex match
/// </summary>
public static class Matcher
{
    public static bool Matches(string? pattern, string toolName)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        if (pattern.StartsWith('/') && pattern.EndsWith('/') && pattern.Length >= 2)
        {
            var rx = pattern[1..^1];
            try
            {
                return Regex.IsMatch(toolName, rx);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        if (pattern.EndsWith('*'))
        {
            return toolName.StartsWith(pattern[..^1], StringComparison.Ordinal);
        }
        return string.Equals(toolName, pattern, StringComparison.Ordinal);
    }
}
