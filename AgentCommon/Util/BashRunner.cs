using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentCommon.Util;

public static class BashRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    public static Result Run(string command, string workDir, int timeoutSeconds = 120, int maxOutputBytes = 50_000)
    {
        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding = System.Text.Encoding.UTF8;

        using var p = new Process { StartInfo = psi };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutSeconds * 1000))
            {
                try { p.Kill(true); } catch { }
                return new Result(-1, "", "Error: Timeout (" + timeoutSeconds + "s)", true);
            }
        }
        catch (Exception ex)
        {
            return new Result(-1, "", $"Error: {ex.GetType().Name}: {ex.Message}", false);
        }

        var combined = (stdout.ToString() + stderr.ToString()).Trim();
        if (combined.Length > maxOutputBytes)
        {
            combined = combined[..maxOutputBytes];
        }
        return new Result(p.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }
}
