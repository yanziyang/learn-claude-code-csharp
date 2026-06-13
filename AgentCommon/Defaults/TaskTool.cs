using System.Text.Json;
using AgentCommon.Subagent;
using AgentCommon.Tools;

namespace AgentCommon.Defaults;

public static class TaskTool
{
    /// <summary>
    /// Register a "task" tool on the parent's registry. When the parent
    /// model calls task, the subagent runs against the sub-tools and only
    /// the final text is returned to the parent.
    /// </summary>
    public static void Register(
        ToolRegistry parentTools,
        Func<SubagentRunner> spawnSubagent)
    {
        var schema = SchemaBuilder.Object(
            "Launch a subagent to handle a complex subtask. Returns only the final conclusion.",
            new Dictionary<string, (string, string, bool)>
            {
                ["description"] = ("string", "Detailed description of the subtask", true),
            });

        parentTools.Register("task", "Launch a subagent to handle a complex subtask. Returns only the final conclusion.", schema, input =>
        {
            var description = input.GetProperty("description").GetString() ?? "";
            var runner = spawnSubagent();
            return runner.RunAsync(description).GetAwaiter().GetResult();
        });
    }
}
