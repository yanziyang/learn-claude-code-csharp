using AgentCommon.Agent;
using AgentCommon.Messages;

namespace AgentCommon.Util;

public static class Repl
{
    public static async Task RunAsync(AgentHarness agent, string prompt, int maxIterations = 32, CancellationToken ct = default)
    {
        var history = new List<Message>();
        while (true)
        {
            Console.Write(prompt);
            string? query;
            try
            {
                query = Console.ReadLine();
            }
            catch (IOException)
            {
                break;
            }
            if (query is null)
            {
                break;
            }
            var trimmed = query.Trim();
            if (trimmed.Length == 0 || trimmed.Equals("q", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            agent.FireUserPromptSubmit(trimmed);
            history.Add(Message.UserText(trimmed));
            await agent.RunUntilDoneAsync(history, maxIterations, ct);
            // Print final assistant text
            var last = history[^1];
            foreach (var block in last.Content.OfType<TextBlock>())
            {
                Console.WriteLine(block.Text);
            }
            Console.WriteLine();
        }
    }
}
