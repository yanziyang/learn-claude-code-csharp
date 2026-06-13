using System.Text.Json;
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

/// <summary>
/// Per-event subscribers. The agent harness fires these around the loop.
/// Returning a non-null string from a PreToolUse or Stop handler blocks / forces
/// the loop in a specific way; other events return void.
/// </summary>
public sealed class HookBus
{
    private readonly Dictionary<HookEvent, List<Delegate>> _subs = new();

    public void OnUserPromptSubmit(Action<string> handler) => Add(HookEvent.UserPromptSubmit, handler);
    public void OnPreToolUse(Func<ToolUseBlock, string?> handler) => Add(HookEvent.PreToolUse, handler);
    public void OnPostToolUse(Action<ToolUseBlock, string> handler) => Add(HookEvent.PostToolUse, handler);
    public void OnStop(Func<string?> handler) => Add(HookEvent.Stop, handler);
    public void OnStop(Func<IReadOnlyList<Message>?, string?> handler) => Add(HookEvent.Stop, handler);
    public void OnBeforeLlmCall(Action<List<Message>> handler) => Add(HookEvent.BeforeLlmCall, handler);

    public void FireUserPromptSubmit(string query)
    {
        foreach (var d in Subs(HookEvent.UserPromptSubmit).OfType<Action<string>>())
        {
            d(query);
        }
    }

    /// <summary>Returns the first non-null block-reason, or null to allow the tool call.</summary>
    public string? FirePreToolUse(ToolUseBlock block)
    {
        foreach (var d in Subs(HookEvent.PreToolUse).OfType<Func<ToolUseBlock, string?>>())
        {
            var r = d(block);
            if (r is not null) return r;
        }
        return null;
    }

    public void FirePostToolUse(ToolUseBlock block, string output)
    {
        foreach (var d in Subs(HookEvent.PostToolUse).OfType<Action<ToolUseBlock, string>>())
        {
            d(block, output);
        }
    }

    /// <summary>Returns a force-continue message, or null to stop normally.</summary>
    public string? FireStop() => FireStopOnHistory(null);

    public string? FireStopOnHistory(IReadOnlyList<Message>? history)
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
        return null;
    }

    /// <summary>
    /// Fired right before the LLM call. The handler may inject messages
    /// (e.g. background-task notifications) into the list.
    /// </summary>
    public void FireBeforeLlmCall(List<Message> messages)
    {
        foreach (var d in Subs(HookEvent.BeforeLlmCall).OfType<Action<List<Message>>>())
        {
            d(messages);
        }
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
