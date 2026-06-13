# s09: Agent Teams

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > [ s09 ] s10 > s11 > s12`

> *"When the task is too big for one, delegate to teammates"* -- persistent teammates + async mailboxes.
>
> **Harness layer**: Team mailboxes -- multiple models, coordinated through files.

## Problem

Subagents (s04) are disposable: spawn, work, return summary, die. No identity, no memory between invocations. Background tasks (s08) run shell commands but can't make LLM-guided decisions.

Real teamwork needs: (1) persistent agents that outlive a single prompt, (2) identity and lifecycle management, (3) a communication channel between agents.

## Solution

```
Teammate lifecycle:
  spawn -> WORKING -> IDLE -> WORKING -> ... -> SHUTDOWN

Communication:
  .mailboxes/
    lead.jsonl            <- append-only, drain-on-read
    alice.jsonl
    bob.jsonl

              +--------+    bus.Send("alice","bob","...")   +--------+
              | alice  | -----------------------------> |  bob   |
              | loop   |    bob.jsonl << {json_line}    |  loop  |
              +--------+                                +--------+
                   ^                                         |
                   |        bus.ReadInbox("alice")          |
                   +---- alice.jsonl -> read + drain ---------+
```

## How It Works

1. The `MessageBus` writes one JSONL file per agent under `.mailboxes/` (`AgentCommon/Teams/MessageBus.cs`).

```csharp
public sealed class MessageBus
{
    private readonly string _mailboxDir;

    public MessageBus(string workDir, Action<string>? onLog = null)
    {
        _mailboxDir = Path.Combine(workDir, ".mailboxes");
        Directory.CreateDirectory(_mailboxDir);
    }

    public void Send(string from, string to, string content, string msgType = "message")
    {
        var msg = new MailboxMessage
        {
            From = from, To = to, Content = content, Type = msgType,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };
        var inbox = Path.Combine(_mailboxDir, $"{to}.jsonl");
        lock (_lock)
        {
            File.AppendAllText(inbox, JsonSerializer.Serialize(msg) + "\n");
        }
    }
}
```

2. `ReadInbox` consumes the whole inbox in one call: read all lines, delete the file.

```csharp
public List<MailboxMessage> ReadInbox(string agent)
{
    var inbox = Path.Combine(_mailboxDir, $"{agent}.jsonl");
    if (!File.Exists(inbox)) return new List<MailboxMessage>();

    List<MailboxMessage> msgs;
    lock (_lock)
    {
        var lines = File.ReadAllText(inbox).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        msgs = lines.Select(l => JsonSerializer.Deserialize<MailboxMessage>(l)!).ToList();
        File.Delete(inbox);   // consume: read + delete
    }
    return msgs;
}
```

3. `TeamTools` registers three tools on the lead: `spawn_teammate`, `send_message`, `check_inbox`.

```csharp
var bus = new MessageBus(workDir);
TeamTools.Register(tools, bus, client, config, workDir);
// → spawn_teammate: starts a TeammateRunner on a background thread
// → send_message:  bus.Send("lead", to, content)
// → check_inbox:   bus.ReadInbox("lead") (drained)
```

4. Each `TeammateRunner` checks its own inbox before every LLM call, injecting received messages into context.

```csharp
// In a TeammateRunner hook (paraphrased):
var msgs = bus.ReadInbox(name);
if (msgs.Count > 0)
    messages.Add(Message.UserText(
        "<inbox>\n" + JsonSerializer.Serialize(msgs, new JsonSerializerOptions { WriteIndented = true })
        + "\n</inbox>"));
```

## What Changed From s08

| Component      | Before (s08)     | After (s09)                |
|----------------|------------------|----------------------------|
| Tools          | 6                | 9 (+spawn/send/check_inbox)|
| Agents         | Single           | Lead + N teammates         |
| Persistence    | None             | JSONL inboxes under .mailboxes/ |
| Threads        | Background cmds  | Full agent loops per thread|
| Lifecycle      | Fire-and-forget  | idle -> working -> idle    |
| Communication  | None             | message + broadcast        |

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s15_agent_teams
```

1. `Spawn alice (coder) and bob (tester). Have alice send bob a message.`
2. `Broadcast "status update: phase 1 complete" to all teammates`
3. `Check the lead inbox for any messages`
4. Type `/team` to see the team roster with statuses
5. Type `/inbox` to manually check the lead's inbox
