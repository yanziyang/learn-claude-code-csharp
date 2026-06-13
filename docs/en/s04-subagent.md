# s04: Subagents

`s01 > s02 > s03 > [ s04 ] s05 > s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"Break big tasks down; each subtask gets a clean context"* -- subagents use independent messages[], keeping the main conversation clean.
>
> **Harness layer**: Context isolation -- protecting the model's clarity of thought.

## Problem

As the agent works, its messages array grows. Every file read, every bash output stays in context permanently. "What testing framework does this project use?" might require reading 5 files, but the parent only needs the answer: "xUnit."

## Solution

```
Parent agent                     Subagent
+------------------+             +------------------+
| messages=[...]   |             | messages=[]      | <-- fresh
|                  |  dispatch   |                  |
| tool: task       | ----------> | while tool_use:  |
|   prompt="..."   |             |   call tools     |
|                  |  summary    |   append results |
|   result = "..." | <---------- | return last text |
+------------------+             +------------------+

Parent context stays clean. Subagent context is discarded.
```

## How It Works

1. The parent gets a `task` tool. The child gets all base tools except `task` (no recursive spawning).

```csharp
// Parent toolset = base + task
var parentTools = new ToolRegistry();
BashTool.Register(parentTools, workDir);
FileTools.Register(parentTools, workDir);

// Child toolset = base only
var childTools = new ToolRegistry();
BashTool.Register(childTools, workDir);
FileTools.Register(childTools, workDir);

TaskTool.Register(parentTools, () =>
    new SubagentRunner(client, config, childTools,
        systemPrompt: "You are a focused sub-agent. Return a concise summary."));
```

2. The subagent starts with `messages = []` and runs its own loop. Only the final text returns to the parent (`AgentCommon/Subagent/SubagentRunner.cs`).

```csharp
public async Task<string> RunAsync(string description, CancellationToken ct = default)
{
    var messages = new List<Message> { Message.UserText(description) };

    LlmResponse? last = null;
    for (var i = 0; i < _maxIterations; i++)
    {
        last = await _client.CreateMessageAsync(
            _systemPrompt, messages, _tools.AllSpecs().ToList(), ct: ct);
        messages.Add(Message.Assistant(last.Content));

        if (last.StopReason != "tool_use")
            break;

        var results = new List<ToolResultBlock>();
        foreach (var block in last.Content.OfType<ToolUseBlock>())
        {
            var output = _tools.Invoke(block.Name, block.Input);
            results.Add(new ToolResultBlock(block.Id, output));
        }
        messages.Add(Message.UserToolResults(results));
    }

    return ExtractFinalText(last, messages);
}
```

The child's entire message history (possibly 30+ tool calls) is discarded. The parent receives a one-paragraph summary as a normal `tool_result`.

## What Changed From s03

| Component      | Before (s03)     | After (s04)               |
|----------------|------------------|---------------------------|
| Tools          | 5                | 5 (base) + task (parent)  |
| Context        | Single shared    | Parent + child isolation  |
| Subagent       | None             | `SubagentRunner` class    |
| Return value   | N/A              | Summary text only         |

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s06_subagent
```

1. `Use a subtask to find what testing framework this project uses`
2. `Delegate: read all .cs files and summarize what each one does`
3. `Use a task to create a new module, then verify it from here`
