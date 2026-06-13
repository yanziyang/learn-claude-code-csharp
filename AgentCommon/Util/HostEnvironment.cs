using System.Runtime.InteropServices;

namespace AgentCommon.Util;

public static class HostEnvironment
{
    public enum HostOs
    {
        Unknown,
        Windows,
        Linux,
        MacOS,
    }

    private static readonly Lazy<HostOs> _os = new(DetectOs);

    public static HostOs Current => _os.Value;

    public static string OsName => Current switch
    {
        HostOs.Windows => "Windows",
        HostOs.Linux => "Linux",
        HostOs.MacOS => "macOS",
        _ => "Unknown",
    };

    public static string Shell => Current switch
    {
        HostOs.Windows => "cmd.exe",
        HostOs.Linux => "bash (POSIX sh-compatible)",
        HostOs.MacOS => "zsh (bash-compatible)",
        _ => "the host's default shell",
    };

    public static char PathSeparator => Current == HostOs.Windows ? '\\' : '/';

    public static string PromptFragment => Current switch
    {
        HostOs.Windows =>
            "## Hosting environment\n" +
            "- OS: Windows. The bash tool spawns `cmd.exe /c <command>` directly — cmd.exe is " +
            "the ONLY shell available to you. Do not call `powershell`, `pwsh`, `bash`, or " +
            "`cmd.exe` yourself; the tool already wraps in cmd.exe, and spawning another " +
            "process inside it just adds a failure mode (e.g. `Access Denied` from a locked-" +
            "down PowerShell on this machine).\n" +
            "- cmd.exe built-ins / standard externals on PATH: `dir`, `where`, `type`, `copy`, " +
            "`move`, `del`, `find`, `findstr`, `set`, `for`, `if`, `echo`, `more`, `sort`, " +
            "`xcopy`, `robocopy`. Pipes `|` and redirects `>`, `>>`, `<`, `2>nul`, `2>&1` " +
            "work natively.\n" +
            "- Common recipes (use these directly, do not improvise with cmdlets):\n" +
            "    list files (bare names):    dir /A-D /B\n" +
            "    list files (formatted):     dir\n" +
            "    count files:                dir /A-D /B 2^>nul | find /c /v \"\"\n" +
            "    recursive list:             dir /S /A-D /B\n" +
            "    search inside files:        findstr /S /I /R \"pattern\" *.cs\n" +
            "    read a file:                type file.txt\n" +
            "    check a program exists:     where xxx\n" +
            "- Do NOT issue PowerShell cmdlets (`Get-ChildItem`, `Select-String`, " +
            "`Where-Object`, `Get-Content`) or Unix tools (`ls`, `cat`, `grep`, `sed`, " +
            "`awk`, `wc`, `head`, `tail`, `rg`). They return 'not recognized' in cmd.exe and " +
            "waste a round-trip. Use the cmd.exe recipes above.\n" +
            "- If a cmd.exe command fails, fix the syntax (quoting, escape `|` as `^|`, " +
            "escape `&` as `^&`) — do NOT switch to a different shell.\n" +
            "- Paths: Windows-style (`C:\\Users\\me`). Quote any path containing spaces. " +
            "Forward slashes also work in most cmd built-ins.",

        HostOs.Linux =>
            "## Hosting environment\n" +
            "- OS: Linux\n" +
            "- Default shell: bash (POSIX sh-compatible)\n" +
            "- Use forward-slash paths and standard Unix utilities. The filesystem is case-sensitive. " +
            "GNU coreutils (grep, sed, find, awk) are available; use them directly.",

        HostOs.MacOS =>
            "## Hosting environment\n" +
            "- OS: macOS\n" +
            "- Default shell: zsh (bash-compatible)\n" +
            "- Use forward-slash paths. Some BSD variants of utilities (sed, find, awk) differ from " +
            "GNU; prefer portable commands (e.g. macOS `sed -i ''` for in-place edit).",

        _ =>
            "## Hosting environment\n" +
            $"- OS: unknown ({RuntimeInformation.OSDescription})\n" +
            "- Detect the actual host shell and adjust commands accordingly.",
    };

    public static void Initialize() => _ = _os.Value;

    private static HostOs DetectOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return HostOs.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return HostOs.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return HostOs.Linux;
        return HostOs.Unknown;
    }
}
