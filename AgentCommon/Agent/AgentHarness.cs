using AgentCommon.Compact;
using AgentCommon.Hooks;
using AgentCommon.Llm;
using AgentCommon.Messages;
using AgentCommon.Tools;

namespace AgentCommon.Agent;

public interface IPermissionChecker
{
    PermissionDecision Check(string toolName, System.Text.Json.JsonElement input);
}

public enum PermissionDecision
{
    Allow,
    Deny,
    Ask,
}

public sealed class AllowAllPermissions : IPermissionChecker
{
    public PermissionDecision Check(string toolName, System.Text.Json.JsonElement input) => PermissionDecision.Allow;
}

public sealed class AgentHarness
{
    public DeepSeekClient Client { get; }
    public ToolRegistry Tools { get; }
    public Func<string>? SystemPromptProvider { get; set; }
    public string SystemPrompt
    {
        get => SystemPromptProvider?.Invoke() ?? "";
        set => SystemPromptProvider = () => value;
    }
    public IPermissionChecker Permissions { get; set; } = new AllowAllPermissions();
    public IContextCompactor Compactor { get; set; } = new NullCompactor();
    public HookBus Hooks { get; } = new();
    public Action<string>? OnLog { get; set; }
    public int? MaxTokensEscalation { get; set; }
    public string WorkDir { get; set; } = Directory.GetCurrentDirectory();

    public AgentHarness(DeepSeekClient client, ToolRegistry tools, string systemPrompt)
    {
        Client = client;
        Tools = tools;
        SystemPrompt = systemPrompt;
    }

    public AgentHarness(DeepSeekClient client, ToolRegistry tools, Func<string> provider)
    {
        Client = client;
        Tools = tools;
        SystemPromptProvider = provider;
    }

    public async Task<LlmResponse> RunAsync(
        List<Message> messages,
        int? maxTokensOverride = null,
        string? modelOverride = null,
        CancellationToken ct = default)
    {
        await Hooks.FireBeforeLlmCallAsync(messages, ct);
        Compactor.PrepareBeforeLlm(messages);

        var systemPrompt = SystemPromptProvider?.Invoke() ?? "";
        LlmResponse response;
        try
        {
            response = await Client.CreateMessageAsync(
                systemPrompt, messages, Tools.AllSpecs().ToList(),
                maxTokensOverride, modelOverride, ct);
        }
        catch (InvalidOperationException ex) when (IsPromptTooLong(ex))
        {
            messages.Clear();
            messages.AddRange(await Compactor.EmergencyAsync(messages, ct));
            response = await Client.CreateMessageAsync(
                systemPrompt, messages, Tools.AllSpecs().ToList(),
                maxTokensOverride, modelOverride, ct);
        }

        messages.Add(Message.Assistant(response.Content));

        // s11: max_tokens — append a continuation nudge; the loop can escalate the
        // next call to a higher max_tokens if MaxTokensEscalation is configured.
        if (response.StopReason == "max_tokens")
        {
            messages.Add(Message.UserText(
                "Output token limit hit. Resume directly — no apology, no recap. " +
                "Pick up mid-thought."));
        }

        if (response.StopReason != "tool_use")
        {
            return response;
        }

        var results = new List<ToolResultBlock>();
        foreach (var block in response.Content.OfType<ToolUseBlock>())
        {
            OnLog?.Invoke($"\u001b[33m> {block.Name}\u001b[0m");

            // Hooks first; any non-null return from PreToolUse blocks execution
            var blocked = await Hooks.FirePreToolUseAsync(block, ct);
            string output;
            if (blocked is not null)
            {
                output = blocked;
            }
            else
            {
                // Permission pipeline runs after hooks; only check if hooks allowed it
                var decision = Permissions.Check(block.Name, block.Input);
                output = decision switch
                {
                    PermissionDecision.Deny => "Error: Permission denied",
                    PermissionDecision.Ask => "Error: Permission required (user approval not implemented)",
                    _ => Tools.Invoke(block.Name, block.Input),
                };
            }

            await Hooks.FirePostToolUseAsync(block, output, ct);
            OnLog?.Invoke(output.Length > 200 ? output[..200] : output);
            results.Add(new ToolResultBlock(block.Id, output));
        }

        messages.Add(Message.UserToolResults(results));
        return response;
    }

    private static bool IsPromptTooLong(Exception ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("prompt_too_long", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("\"type\":\"error\"", StringComparison.OrdinalIgnoreCase)
               && msg.Contains("too long", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Run the loop until the model stops calling tools.
    /// Honors the Stop hook: a non-null return forces another iteration.
    /// Escalates max_tokens after the first max_tokens stop (s11).
    /// </summary>
    public async Task RunUntilDoneAsync(
        List<Message> messages,
        int maxIterations = 32,
        CancellationToken ct = default)
    {
        var hasEscalated = false;
        for (var i = 0; i < maxIterations; i++)
        {
            int? overrideTokens = null;
            if (hasEscalated && MaxTokensEscalation is int escalated)
            {
                overrideTokens = escalated;
            }

            var resp = await RunAsync(messages, maxTokensOverride: overrideTokens, ct: ct);

            // Stop hook can request continuation
            var force = await Hooks.FireStopOnHistoryAsync(messages, ct);
            if (force is not null)
            {
                messages.Add(Message.UserText(force));
                continue;
            }

            if (resp.StopReason == "max_tokens" && !hasEscalated)
            {
                hasEscalated = true;
                // The harness inside RunAsync already appended a continuation nudge.
                // The next call uses a larger max_tokens budget.
                continue;
            }

            if (resp.StopReason != "tool_use")
            {
                return;
            }
        }
        OnLog?.Invoke("(loop limit reached)");
    }

    /// <summary>
    /// Run a single turn — the UserPromptSubmit hooks fire here, then exactly one
    /// LLM call. Used by the REPL when the user submits a new prompt.
    /// </summary>
    public Task FireUserPromptSubmitAsync(string query, CancellationToken ct = default) =>
        Hooks.FireUserPromptSubmitAsync(query, ct);
}
