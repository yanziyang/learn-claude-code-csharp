using System.Text.Json;
using AgentCommon.Agent;
using AgentCommon.Util;

namespace AgentCommon.Permissions;

public sealed class PermissionRule
{
    public string[] Tools { get; init; } = Array.Empty<string>();
    public Func<JsonElement, string?> Check { get; init; } = _ => null;
    public string Message { get; init; } = "";
}

public sealed class InteractivePermissionPipeline : IPermissionChecker
{
    private readonly string _workDir;
    private readonly HashSet<string> _denyList;
    private readonly List<PermissionRule> _rules;
    private readonly HashSet<string> _sessionApprove = new();

    public InteractivePermissionPipeline(
        string workDir,
        IEnumerable<string> denyList,
        IEnumerable<PermissionRule> rules)
    {
        _workDir = workDir;
        _denyList = new HashSet<string>(denyList, StringComparer.OrdinalIgnoreCase);
        _rules = rules.ToList();
    }

    public PermissionDecision Check(string toolName, JsonElement input)
    {
        // Gate 1: hard deny list (applies only to bash-style "command" arguments)
        if (toolName == "bash"
            && input.TryGetProperty("command", out var cmd)
            && cmd.ValueKind == JsonValueKind.String)
        {
            var text = cmd.GetString() ?? "";
            foreach (var deny in _denyList)
            {
                if (text.Contains(deny, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"\n\u001b[31m\u26d4 Blocked: '{deny}' is on the deny list\u001b[0m");
                    return PermissionDecision.Deny;
                }
            }
        }

        // Gate 2: rule matching
        string? reason = null;
        foreach (var rule in _rules)
        {
            if (!rule.Tools.Contains(toolName)) continue;
            var r = rule.Check(input);
            if (r is not null) { reason = r; break; }
        }
        if (reason is null) return PermissionDecision.Allow;

        // Gate 3: ask the user; remember session approvals
        var signature = $"{toolName}:{reason}";
        if (_sessionApprove.Contains(signature)) return PermissionDecision.Allow;

        Console.WriteLine($"\n\u001b[33m\u26a0  {reason}\u001b[0m");
        Console.WriteLine($"   Tool: {toolName}({Compact(input)})");
        Console.Write("   Allow? [y/N/a=always-this-rule] ");
        var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (answer is "y" or "yes")
        {
            return PermissionDecision.Allow;
        }
        if (answer is "a" or "always")
        {
            _sessionApprove.Add(signature);
            return PermissionDecision.Allow;
        }
        return PermissionDecision.Deny;
    }

    private static string Compact(JsonElement el)
    {
        var s = el.ToString();
        return s.Length > 200 ? s[..200] + "..." : s;
    }
}
