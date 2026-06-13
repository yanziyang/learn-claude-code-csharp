namespace AgentCommon.Util;

public static class PathGuard
{
    public static string SafePath(string workDir, string p)
    {
        var root = Path.GetFullPath(workDir);
        var candidate = Path.IsPathRooted(p) ? p : Path.Combine(root, p);
        var resolved = Path.GetFullPath(candidate);

        // Normalize trailing separators for comparison
        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path escapes workspace: {p}");
        }
        return resolved;
    }

    public static IEnumerable<string> GlobSafe(string workDir, string pattern)
    {
        var root = Path.GetFullPath(workDir);
        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;

        var results = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            if (path.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
        }
        return results;
    }
}
