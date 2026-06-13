# s01: The Agent Loop

`[ s01 ] s02 > s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"One loop & Bash is all you need"* -- one tool + one loop = an agent.
>
> **Harness layer**: The loop -- the model's first connection to the real world.

## Problem

A language model can reason about code, but it can't *touch* the real world -- can't read files, run tests, or check errors. Without a loop, every tool call requires you to manually copy-paste results back. You become the loop.

## Solution

```
+--------+      +-------+      +---------+
|  User  | ---> |  LLM  | ---> |  Tool   |
| prompt |      |       |      | execute |
+--------+      +---+---+      +----+----+
                    ^                |
                    |   tool_result  |
                    +----------------+
                    (loop until stop_reason != "tool_use")
```

One exit condition controls the entire flow. The loop runs until the model stops calling tools.

## How It Works

1. User prompt becomes the first message.

```csharp
messages.Add(Message.UserText(query));
```

2. Send messages + tool definitions to the LLM.

```csharp
var response = await client.CreateMessageAsync(
    system, messages, tools.AllSpecs().ToList(), maxTokens: 8000);
```

3. Append the assistant response. Check `stop_reason` -- if the model didn't call a tool, we're done.

```csharp
messages.Add(Message.Assistant(response.Content));
if (response.StopReason != "tool_use")
    return response;
```

4. Execute each tool call, collect results, append as a user message. Loop back to step 2.

```csharp
var results = new List<ToolResultBlock>();
foreach (var block in response.Content.OfType<ToolUseBlock>())
{
    var output = tools.Invoke(block.Name, block.Input);
    results.Add(new ToolResultBlock(block.Id, output));
}
messages.Add(Message.UserToolResults(results));
```

Assembled into one function (`AgentCommon/Agent/AgentHarness.cs`):

```csharp
public async Task<LlmResponse> RunAsync(
    List<Message> messages, int? maxTokensOverride = null, CancellationToken ct = default)
{
    Hooks.FireBeforeLlmCall(messages);
    Compactor.PrepareBeforeLlm(messages);

    var systemPrompt = SystemPromptProvider?.Invoke() ?? "";
    var response = await Client.CreateMessageAsync(
        systemPrompt, messages, Tools.AllSpecs().ToList(),
        maxTokensOverride, modelOverride: null, ct);
    messages.Add(Message.Assistant(response.Content));

    if (response.StopReason != "tool_use")
        return response;

    var results = new List<ToolResultBlock>();
    foreach (var block in response.Content.OfType<ToolUseBlock>())
    {
        var output = Tools.Invoke(block.Name, block.Input);
        results.Add(new ToolResultBlock(block.Id, output));
    }
    messages.Add(Message.UserToolResults(results));
    return response;
}
```

That's the entire agent. Everything else in this course layers on top -- without changing the loop.

## What Changed

| Component     | Before     | After                          |
|---------------|------------|--------------------------------|
| Agent loop    | (none)     | `while stop_reason == "tool_use"` |
| Tools         | (none)     | `bash` (one tool)              |
| Messages      | (none)     | Accumulating list              |
| Control flow  | (none)     | `StopReason != "tool_use"`     |

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s01_agent_loop
```

1. `Create a file called hello.cs that prints "Hello, World!"`
2. `List all C# files in this directory`
3. `What is the current git branch?`
4. `Create a directory called test_output and write 3 files in it`
