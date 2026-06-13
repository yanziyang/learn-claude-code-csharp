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

    /// <summary>
    /// Glob implementation that supports the <c>**</c> recursive wildcard
    /// (e.g. <c>**/*.cs</c>, <c>src/**/*.md</c>) on top of the .NET
    /// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
    /// built-ins, which only understand <c>*</c> and <c>?</c>. The .NET
    /// call rejects <c>**</c> as a syntactically invalid file name on
    /// Windows; LLMs frequently pass that pattern, so we translate it.
    /// </summary>
    public static IEnumerable<string> GlobSafe(string workDir, string pattern)
    {
        var root = Path.GetFullPath(workDir);
        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;

        var (dir, filePattern, recursive) = SplitGlob(root, pattern);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, filePattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid glob pattern '{pattern}': {ex.Message}", ex);
        }

        var results = new List<string>();
        foreach (var path in files)
        {
            if (path.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
        }
        return results;
    }

    /// <summary>
    /// Split a glob pattern into (baseDir, filePattern, recursive).
    /// The last segment is the file pattern; everything before it forms
    /// the directory, with any <c>**</c> segments stripped (they only
    /// enable recursion). A pattern of just <c>**</c> matches every file.
    /// </summary>
    private static (string dir, string filePattern, bool recursive) SplitGlob(string root, string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return (root, "*", true);
        }

        var filePattern = segments[^1];
        var dirSegments = segments.Take(segments.Length - 1).ToArray();
        var recursive = false;

        if (filePattern == "**")
        {
            filePattern = "*";
            recursive = true;
        }

        var realDirSegments = dirSegments.Where(s => s != "**").ToArray();
        if (dirSegments.Length != realDirSegments.Length)
        {
            recursive = true;
        }

        var dir = realDirSegments.Length == 0
            ? root
            : Path.Combine(root, Path.Combine(realDirSegments));

        return (dir, filePattern, recursive);
    }
}


