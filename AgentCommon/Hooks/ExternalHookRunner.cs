using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentCommon.Util;

namespace AgentCommon.Hooks;

public sealed class ExternalHookRunner
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Action<string>? _log;
    private readonly TimeSpan _timeout;

    public AppLogger? Logger { get; set; }

    public ExternalHookRunner(Action<string>? log = null, TimeSpan? timeout = null)
    {
        _log = log;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Run a single external hook command. The event JSON is written to the
    /// script's stdin; the script's exit code (and optional stdout JSON)
    /// determine the result.
    ///
    /// Exit code semantics (CC-compatible):
    ///   0 = allow
    ///   2 = block; stderr (or stdout) is the reason
    ///   other = non-blocking error; warning is logged
    ///
    /// Optional stdout JSON overrides:
    ///   { "decision": "block", "reason": "..." }    -- PreToolUse only
    ///   { "decision": "block", "message": "..." }  -- Stop (force continue)
    /// </summary>
    public async Task<ExternalHookResult> RunAsync(
        HookEvent ev,
        string command,
        string workDir,
        object payload,
        CancellationToken ct = default)
    {
        var argv = TokenizeCommand(command);
        if (argv.Count == 0)
        {
            return Warn("hook command is empty", ev, command, payload, exitCode: -1, stdout: "", stderr: "(empty command)");
        }

        var fileName = ResolveExecutable(argv[0]);
        var args = argv.Skip(1).ToArray();
        var json = JsonSerializer.Serialize(payload);

        Logger?.Hook($"call event={ev} command=\"{command}\" cwd=\"{workDir}\" args={args.Length}");
        Logger?.Hook($"payload {ev} {Truncate(json, 2048)}");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return Warn($"failed to start hook '{fileName}': {ex.Message}", ev, command, payload, exitCode: -1, stdout: "", stderr: ex.Message);
        }
        if (proc is null)
        {
            return Warn($"failed to start hook '{fileName}': Process.Start returned null", ev, command, payload, exitCode: -1, stdout: "", stderr: "Process.Start returned null");
        }

        try
        {
            try
            {
                await proc.StandardInput.WriteAsync(json);
                proc.StandardInput.Close();
            }
            catch (IOException io)
            {
                // The process may have exited before reading stdin (e.g. a
                // missing module). Continue so we can still capture stderr.
                Logger?.Error("hook", $"stdin write failed for '{fileName}': {io.Message}");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return Warn($"hook '{fileName}' timed out after {_timeout.TotalSeconds:N0}s", ev, command, payload, exitCode: -1, stdout: "", stderr: "timeout");
            }

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            var code = proc.ExitCode;

            Logger?.Hook($"result event={ev} command=\"{command}\" exit={code} stdoutLen={stdout.Length} stderrLen={stderr.Length}");
            Logger?.Hook($"stdout {ev} {Truncate(stdout, 2048)}");
            Logger?.Hook($"stderr {ev} {Truncate(stderr, 2048)}");

            if (code == 0)
            {
                return ParseDecision(stdout, ev, allowOnNoDecision: true);
            }
            if (code == 2)
            {
                var reason = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                            : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                            : "blocked by hook (exit 2)";
                return ev == HookEvent.Stop
                    ? new ExternalHookResult { ContinueMessage = reason }
                    : new ExternalHookResult { BlockReason = reason };
            }
            return Warn($"hook '{fileName}' exited with code {code}: {stderr.Trim()}", ev, command, payload, code, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return Warn($"hook '{fileName}' cancelled", ev, command, payload, exitCode: -1, stdout: "", stderr: "cancelled");
        }
        finally
        {
            proc.Dispose();
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + $"...<{s.Length - max} more chars>");

    private ExternalHookResult ParseDecision(string stdout, HookEvent ev, bool allowOnNoDecision)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new ExternalHookResult();
        }
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ExternalHookResult();
            if (!root.TryGetProperty("decision", out var d) || d.ValueKind != JsonValueKind.String)
            {
                return new ExternalHookResult();
            }
            var decision = d.GetString();
            if (decision == "block" || decision == "deny")
            {
                var reason = TryString(root, "reason")
                          ?? TryString(root, "message")
                          ?? "blocked by hook";
                return ev == HookEvent.Stop
                    ? new ExternalHookResult { ContinueMessage = reason }
                    : new ExternalHookResult { BlockReason = reason };
            }
            return new ExternalHookResult();
        }
        catch (JsonException)
        {
            return new ExternalHookResult();
        }
    }

    private static string? TryString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private ExternalHookResult Warn(string message, HookEvent ev, string command, object payload, int exitCode, string stdout, string stderr)
    {
        _log?.Invoke($"[hook] {message}");
        Logger?.Error("hook", $"warn event={ev} command=\"{command}\" exit={exitCode} message=\"{message}\"");
        if (exitCode >= 0)
        {
            Logger?.Hook($"stdout {ev} {Truncate(stdout, 2048)}");
            Logger?.Hook($"stderr {ev} {Truncate(stderr, 2048)}");
        }
        return new ExternalHookResult { Warning = message };
    }

    /// <summary>
    /// On Windows, prefer a concrete executable when the user wrote a bare
    /// name like <c>npx</c>: <c>Process.Start</c> with
    /// <c>UseShellExecute = false</c> doesn't fall back to <c>PATHEXT</c>
    /// (e.g. <c>npx.cmd</c>), so a script-only entry on PATH would fail.
    /// Walk PATH/PATHEXT and return the first hit, preserving the original
    /// name elsewhere (Linux/macOS, or when an explicit path/extension was
    /// given).
    /// </summary>
    public static string ResolveExecutable(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName;
        if (Path.HasExtension(fileName)) return fileName;
        if (!OperatingSystem.IsWindows()) return fileName;
        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            return fileName;
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathDirs)
        {
            foreach (var ext in pathExt)
            {
                var candidate = Path.Combine(dir, fileName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return fileName;
    }

    /// <summary>
    /// Quote-aware argv tokenizer. Single or double quotes group whitespace;
    /// no backslash escaping (matches CC's command field).
    /// </summary>
    public static IReadOnlyList<string> TokenizeCommand(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var hasAny = false;
        foreach (var c in command)
        {
            hasAny = true;
            if (quote is not null)
            {
                if (c == quote) { quote = null; }
                else { current.Append(c); }
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        if (!hasAny) return Array.Empty<string>();
        return tokens;
    }
}

public sealed class ExternalHookResult
{
    public string? BlockReason { get; init; }
    public string? ContinueMessage { get; init; }
    public string? Warning { get; init; }
}
