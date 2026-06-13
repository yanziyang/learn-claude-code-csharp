using AgentCommon.Config;
using AgentCommon.Llm;
using AgentCommon.Messages;

namespace AgentCommon.Compact;

public interface IContextCompactor
{
    /// <summary>
    /// Called by the agent harness before each LLM request.
    /// May mutate <paramref name="messages"/> in place (e.g. snip, placeholder).
    /// </summary>
    void PrepareBeforeLlm(List<Message> messages);

    /// <summary>
    /// Called when the LLM call fails with a context-length error.
    /// Returns a new compacted message list.
    /// </summary>
    Task<List<Message>> EmergencyAsync(List<Message> messages, CancellationToken ct = default);
}

public sealed class NullCompactor : IContextCompactor
{
    public void PrepareBeforeLlm(List<Message> messages) { }
    public Task<List<Message>> EmergencyAsync(List<Message> messages, CancellationToken ct = default) =>
        Task.FromResult(messages);
}
