using AgentCommon.Messages;

namespace AgentCommon.Hooks;

public enum HookEvent
{
    UserPromptSubmit,
    PreToolUse,
    PostToolUse,
    Stop,
    BeforeLlmCall,
}

public sealed record ExternalHookEntry(HookEvent Event, string? Matcher, string Command);

/// <summary>
/// Per-event subscribers. The agent harness fires these around the loop.
/// Returning a non-null string from a PreToolUse or Stop handler blocks / forces
/// the loop in a specific way; other events return void.
///
/// Two flavors of subscribers:
///   1. Typed delegates registered via OnXxx (synchronous, in-process).
///   2. External commands registered via RegisterExternal; the bus spawns the
///      process via <see cref="ExternalHookRunner"/> when the event fires.
/// </summary>
public sealed class HookBus
{
    private readonly Dictionary<HookEvent, List<Delegate>> _subs = new();
    private readonly List<ExternalHookEntry> _external = new();

    public ExternalHookRunner? ExternalRunner { get; set; }
    public string WorkDir { get; set; } = Directory.GetCurrentDirectory();

    public void OnUserPromptSubmit(Action<string> handler) => Add(HookEvent.UserPromptSubmit, handler);
    public void OnPreToolUse(Func<ToolUseBlock, string?> handler) => Add(HookEvent.PreToolUse, handler);
    public void OnPostToolUse(Action<ToolUseBlock, string> handler) => Add(HookEvent.PostToolUse, handler);
    public void OnStop(Func<string?> handler) => Add(HookEvent.Stop, handler);
    public void OnStop(Func<IReadOnlyList<Message>?, string?> handler) => Add(HookEvent.Stop, handler);
    public void OnBeforeLlmCall(Action<List<Message>> handler) => Add(HookEvent.BeforeLlmCall, handler);

    public void RegisterExternal(HookEvent ev, string? matcher, string command)
    {
        _external.Add(new ExternalHookEntry(ev, matcher, command));
    }

    /// <summary>
    /// One-call startup hookup: set the runner + work dir and load every
    /// external command declared in <paramref name="config"/>. Idempotent —
    /// calling it twice appends, so call it once during application startup.
    /// Returns the number of external hooks registered.
    /// </summary>
    public int ConfigureExternal(
        AgentCommon.Config.HooksConfig? config,
        string workDir,
        Action<string>? log = null,
        TimeSpan? timeout = null,
        AgentCommon.Util.AppLogger? logger = null)
    {
        WorkDir = workDir;
        if (ExternalRunner is null)
        {
            ExternalRunner = new ExternalHookRunner(log, timeout);
        }
        if (logger is not null)
        {
            ExternalRunner.Logger = logger;
        }
        return ExternalHookLoader.Load(config, this);
    }

    public async Task FireUserPromptSubmitAsync(string query, CancellationToken ct = default)
    {
        foreach (var d in Subs(HookEvent.UserPromptSubmit).OfType<Action<string>>())
        {
            d(query);
        }
        foreach (var e in _external.Where(x => x.Event == HookEvent.UserPromptSubmit))
        {
            await RunOneAsync(e, new
            {
                hookEventName = "UserPromptSubmit",
                userPrompt = query,
            }, HookEvent.UserPromptSubmit, ct);
        }
    }

    /// <summary>Returns the first non-null block-reason, or null to allow the tool call.</summary>
    public async Task<string?> FirePreToolUseAsync(ToolUseBlock block, CancellationToken ct = default)
    {
        foreach (var d in Subs(HookEvent.PreToolUse).OfType<Func<ToolUseBlock, string?>>())
        {
            var r = d(block);
            if (r is not null) return r;
        }
        foreach (var e in _external.Where(x => x.Event == HookEvent.PreToolUse && Matcher.Matches(x.Matcher, block.Name)))
        {
            var r = await RunOneAsync(e, new
            {
                hookEventName = "PreToolUse",
                toolName = block.Name,
                toolInput = block.Input,
            }, HookEvent.PreToolUse, ct);
            if (r.BlockReason is not null) return r.BlockReason;
        }
        return null;
    }

    public async Task FirePostToolUseAsync(ToolUseBlock block, string output, CancellationToken ct = default)
    {
        foreach (var d in Subs(HookEvent.PostToolUse).OfType<Action<ToolUseBlock, string>>())
        {
            d(block, output);
        }
        foreach (var e in _external.Where(x => x.Event == HookEvent.PostToolUse && Matcher.Matches(x.Matcher, block.Name)))
        {
            await RunOneAsync(e, new
            {
                hookEventName = "PostToolUse",
                toolName = block.Name,
                toolInput = block.Input,
                toolOutput = output,
            }, HookEvent.PostToolUse, ct);
        }
    }

    public Task<string?> FireStopAsync() => FireStopOnHistoryAsync(null, CancellationToken.None);

    public async Task<string?> FireStopOnHistoryAsync(IReadOnlyList<Message>? history, CancellationToken ct = default)
    {
        foreach (var d in Subs(HookEvent.Stop))
        {
            string? r = d switch
            {
                Func<string?> f => f(),
                Func<IReadOnlyList<Message>?, string?> fg => fg(history),
                _ => null,
            };
            if (r is not null) return r;
        }
        var toolCount = 0;
        if (history is not null)
        {
            foreach (var msg in history)
            {
                foreach (var b in msg.Content.OfType<ToolResultBlock>()) toolCount++;
            }
        }
        foreach (var e in _external.Where(x => x.Event == HookEvent.Stop))
        {
            var r = await RunOneAsync(e, new
            {
                hookEventName = "Stop",
                sessionStats = new { toolCalls = toolCount },
            }, HookEvent.Stop, ct);
            if (r.ContinueMessage is not null) return r.ContinueMessage;
        }
        return null;
    }

    public async Task FireBeforeLlmCallAsync(List<Message> messages, CancellationToken ct = default)
    {
        foreach (var d in Subs(HookEvent.BeforeLlmCall).OfType<Action<List<Message>>>())
        {
            d(messages);
        }
    }

    private async Task<ExternalHookResult> RunOneAsync(ExternalHookEntry e, object payload, HookEvent ev, CancellationToken ct)
    {
        if (ExternalRunner is null)
        {
            return new ExternalHookResult { Warning = "ExternalRunner is not configured" };
        }
        return await ExternalRunner.RunAsync(ev, e.Command, WorkDir, payload, ct);
    }

    private void Add(HookEvent ev, Delegate d)
    {
        if (!_subs.TryGetValue(ev, out var list))
        {
            list = new List<Delegate>();
            _subs[ev] = list;
        }
        list.Add(d);
    }

    private IEnumerable<Delegate> Subs(HookEvent ev) =>
        _subs.TryGetValue(ev, out var list) ? list : Enumerable.Empty<Delegate>();
}
