using System.Text.Json;
using AgentCommon.Background;
using AgentCommon.Tools;
using AgentCommon.Util;

namespace AgentCommon.Defaults;

public static class BashTool
{
    public static void Register(ToolRegistry tools, string workDir, Action<string>? onLog = null, BackgroundRunner? background = null)
    {
        var schema = SchemaBuilder.Object("Run a shell command.",
            new Dictionary<string, (string, string, bool)>
            {
                ["command"] = ("string", "The command to execute", true),
                ["run_in_background"] = ("boolean", "If true, run the command in a background thread and return a placeholder immediately", false),
            });

        tools.Register("bash", "Run a shell command.", schema, input =>
        {
            var cmd = input.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            if (BashGuards.IsDangerous(cmd))
            {
                return "Error: Dangerous command blocked";
            }

            var runInBackground = input.TryGetProperty("run_in_background", out var rb)
                && rb.ValueKind == JsonValueKind.True;

            if (runInBackground && background is not null)
            {
                var toolUseId = Guid.NewGuid().ToString("n");
                var bgId = background.Start(toolUseId, cmd, () =>
                {
                    var r = BashRunner.Run(cmd, workDir);
                    var combined = (r.StdOut + r.StdErr).Trim();
                    return string.IsNullOrEmpty(combined) ? "(no output)" : combined;
                });
                return $"<background-task id=\"{bgId}\" status=\"running\"/>\n" +
                       $"Command dispatched in background. You will be notified on completion.";
            }

            var result = BashRunner.Run(cmd, workDir);
            var output = (result.StdOut + result.StdErr).Trim();
            var outp = string.IsNullOrEmpty(output) ? "(no output)" : output;
            onLog?.Invoke(outp.Length > 200 ? outp[..200] : outp);
            return outp;
        });
    }
}

public static class BashGuards
{
    private static readonly string[] Dangerous =
    {
        "rm -rf /", "sudo", "shutdown", "reboot", "> /dev/",
    };

    public static bool IsDangerous(string command) =>
        Dangerous.Any(d => command.Contains(d, StringComparison.Ordinal));
}
