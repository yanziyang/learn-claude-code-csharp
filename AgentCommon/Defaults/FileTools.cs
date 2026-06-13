using System.Text.Json;
using AgentCommon.Tools;
using AgentCommon.Util;

namespace AgentCommon.Defaults;

public static class FileTools
{
    public static void Register(ToolRegistry tools, string workDir, Action<string>? onLog = null)
    {
        tools.Register("read_file", "Read file contents.",
            SchemaBuilder.Object("Read file contents.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["path"] = ("string", "Path to the file (relative to workspace)", true),
                    ["limit"] = ("integer", "Optional maximum number of lines to read", false),
                }),
            input =>
            {
                var pathStr = input.GetProperty("path").GetString() ?? "";
                int? limit = input.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
                    ? l.GetInt32() : null;
                try
                {
                    var resolved = PathGuard.SafePath(workDir, pathStr);
                    var lines = File.ReadAllText(resolved).Split('\n');
                    IEnumerable<string> final = lines;
                    if (limit is int n && n < lines.Length)
                    {
                        final = lines.Take(n).Concat(new[] { $"... ({lines.Length - n} more lines)" });
                    }
                    var outp = string.Join("\n", final);
                    onLog?.Invoke(outp.Length > 200 ? outp[..200] : outp);
                    return outp;
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });

        tools.Register("write_file", "Write content to a file.",
            SchemaBuilder.Object("Write content to a file.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["path"] = ("string", "Path to the file (relative to workspace)", true),
                    ["content"] = ("string", "Content to write", true),
                }),
            input =>
            {
                var pathStr = input.GetProperty("path").GetString() ?? "";
                var content = input.GetProperty("content").GetString() ?? "";
                try
                {
                    var resolved = PathGuard.SafePath(workDir, pathStr);
                    Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
                    File.WriteAllText(resolved, content);
                    return $"Wrote {content.Length} bytes to {pathStr}";
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });

        tools.Register("edit_file", "Replace exact text in a file once.",
            SchemaBuilder.Object("Replace exact text in a file once.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["path"] = ("string", "Path to the file (relative to workspace)", true),
                    ["old_text"] = ("string", "Text to be replaced (must match exactly once)", true),
                    ["new_text"] = ("string", "Replacement text", true),
                }),
            input =>
            {
                var pathStr = input.GetProperty("path").GetString() ?? "";
                var oldText = input.GetProperty("old_text").GetString() ?? "";
                var newText = input.GetProperty("new_text").GetString() ?? "";
                try
                {
                    var resolved = PathGuard.SafePath(workDir, pathStr);
                    var text = File.ReadAllText(resolved);
                    var idx = text.IndexOf(oldText, StringComparison.Ordinal);
                    if (idx < 0)
                    {
                        return $"Error: text not found in {pathStr}";
                    }
                    var updated = text[..idx] + newText + text[(idx + oldText.Length)..];
                    File.WriteAllText(resolved, updated);
                    return $"Edited {pathStr}";
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });

        tools.Register("glob", "Find files matching a glob pattern.",
            SchemaBuilder.Object("Find files matching a glob pattern.",
                new Dictionary<string, (string, string, bool)>
                {
                    ["pattern"] = ("string", "Glob pattern, e.g. *.cs", true),
                }),
            input =>
            {
                var pattern = input.GetProperty("pattern").GetString() ?? "";
                try
                {
                    var matches = PathGuard.GlobSafe(workDir, pattern).ToList();
                    var outp = matches.Count == 0 ? "(no matches)" : string.Join("\n", matches);
                    onLog?.Invoke(outp.Length > 200 ? outp[..200] : outp);
                    return outp;
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            });
    }
}
