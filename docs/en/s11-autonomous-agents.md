# s11: Autonomous Agents

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > [ s11 ] s12`

> *"Teammates scan the board and claim tasks themselves"* -- no need for the lead to assign each one.
>
> **Harness layer**: Autonomy -- models that find work without being told.

## Problem

In s09-s10, teammates only work when explicitly told to. The lead must spawn each one with a specific prompt. 10 unclaimed tasks on the board? The lead assigns each one manually. Doesn't scale.

True autonomy: teammates scan the task board themselves, claim unclaimed tasks, work on them, then look for more.

One subtlety: after context compression (s06), the agent might forget who it is. Identity re-injection fixes this.

## Solution

```
Teammate lifecycle with idle cycle:

+-------+
| spawn |
+---+---+
    |
    v
+-------+   tool_use     +-------+
| WORK  | <------------- |  LLM  |
+---+---+                +-------+
    |
    | stop_reason != tool_use (or idle tool called)
    v
+--------+
|  IDLE  |  poll every 5s for up to 60s
+---+----+
    |
    +---> check inbox --> message? ----------> WORK
    |
    +---> scan .tasks/ --> unclaimed? -------> claim -> WORK
    |
    +---> 60s timeout ----------------------> SHUTDOWN

Identity re-injection after compression:
  if len(messages) <= 3:
    messages.insert(0, identity_block)
```

## How It Works

1. The teammate loop has two phases: WORK and IDLE. When the LLM stops calling tools (or calls an `idle` tool), the teammate enters IDLE.

```csharp
public async Task RunAsync(string prompt, CancellationToken ct = default)
{
    var messages = new List<Message> { Message.UserText(prompt) };

    // -- WORK PHASE --
    for (var i = 0; i < _maxRounds; i++)
    {
        var response = await _client.CreateMessageAsync(
            _systemPrompt, messages, _tools.AllSpecs().ToList(), ct: ct);
        messages.Add(Message.Assistant(response.Content));

        if (response.StopReason != "tool_use") break;

        var results = new List<ToolResultBlock>();
        foreach (var block in response.Content.OfType<ToolUseBlock>())
        {
            var output = _tools.Invoke(block.Name, block.Input);
            results.Add(new ToolResultBlock(block.Id, output));
        }
        messages.Add(Message.UserToolResults(results));
    }

    // -- IDLE PHASE --
    while (!ct.IsCancellationRequested)
    {
        var resumed = await IdlePollAsync(messages, ct);
        if (!resumed) break;     // timeout -> SHUTDOWN
    }
}
```

2. The idle phase polls the inbox and the task board in a loop.

```csharp
async Task<bool> IdlePollAsync(List<Message> messages, CancellationToken ct)
{
    for (var i = 0; i < IdleTimeoutSeconds / PollIntervalSeconds; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct);

        // 1. Inbox?
        var inbox = bus.ReadInbox(_name);
        if (inbox.Count > 0)
        {
            messages.Add(Message.UserText(
                "<inbox>\n" + JsonSerializer.Serialize(inbox, new JsonSerializerOptions { WriteIndented = true })
                + "\n</inbox>"));
            return true;
        }

        // 2. Unclaimed task?
        var candidate = _store.List().FirstOrDefault(t =>
            t.Status == "pending"
            && string.IsNullOrEmpty(t.Owner)
            && _store.CanStart(t));
        if (candidate is not null)
        {
            var (ok, _) = _store.Claim(candidate.Id, _name);
            if (ok)
            {
                messages.Add(Message.UserText(
                    $"<auto-claimed>Task #{candidate.Id}: {candidate.Subject}</auto-claimed>"));
                return true;
            }
        }
    }
    return false;   // timeout -> SHUTDOWN
}
```

3. Task board scanning: find pending, unowned, unblocked tasks.

```csharp
var candidate = _store.List().FirstOrDefault(t =>
    t.Status == "pending"
    && string.IsNullOrEmpty(t.Owner)
    && _store.CanStart(t));
```

4. Identity re-injection: when context is too short (compression happened), insert an identity block.

```csharp
if (messages.Count <= 3)
{
    messages.Insert(0, Message.UserText(
        $"<identity>You are '{_name}', role: {_role}, " +
        $"team: {_teamName}. Continue your work.</identity>"));
    messages.Insert(1, Message.AssistantText($"I am {_name}. Continuing."));
}
```

## What Changed From s10

| Component      | Before (s10)     | After (s11)                |
|----------------|------------------|----------------------------|
| Tools          | 12               | 14 (+idle, +claim_task)    |
| Autonomy       | Lead-directed    | Self-organizing            |
| Idle phase     | None             | Poll inbox + task board    |
| Task claiming  | Manual only      | Auto-claim unclaimed tasks |
| Identity       | System prompt    | + re-injection after compact|
| Timeout        | None             | 60s idle -> auto shutdown  |

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s17_autonomous_agents
```

1. `Create 3 tasks on the board, then spawn alice and bob. Watch them auto-claim.`
2. `Spawn a coder teammate and let it find work from the task board itself`
3. `Create tasks with dependencies. Watch teammates respect the blocked order.`
4. Type `/tasks` to see the task board with owners
5. Type `/team` to monitor who is working vs idle
