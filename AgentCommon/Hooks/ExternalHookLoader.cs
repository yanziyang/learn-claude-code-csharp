using AgentCommon.Config;

namespace AgentCommon.Hooks;

/// <summary>
/// Walks <see cref="AgentConfig.Hooks"/> and registers each external hook
/// command into a <see cref="HookBus"/>. The bus must already have its
/// <see cref="HookBus.ExternalRunner"/> and <see cref="HookBus.WorkDir"/>
/// configured before this is called.
/// </summary>
public static class ExternalHookLoader
{
    public static int Load(HooksConfig? config, HookBus bus)
    {
        if (config is null) return 0;
        var count = 0;
        count += LoadGroups(config.PreToolUse, HookEvent.PreToolUse, bus);
        count += LoadGroups(config.PostToolUse, HookEvent.PostToolUse, bus);
        count += LoadGroups(config.UserPromptSubmit, HookEvent.UserPromptSubmit, bus);
        count += LoadGroups(config.Stop, HookEvent.Stop, bus);
        return count;
    }

    private static int LoadGroups(List<HookGroup>? groups, HookEvent ev, HookBus bus)
    {
        if (groups is null) return 0;
        var n = 0;
        foreach (var g in groups)
        {
            if (g.Hooks is null) continue;
            foreach (var cmd in g.Hooks)
            {
                if (!string.Equals(cmd.Type, "command", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(cmd.Command)) continue;
                bus.RegisterExternal(ev, g.Matcher, cmd.Command);
                n++;
            }
        }
        return n;
    }
}
