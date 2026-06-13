# s15: Agent Teams — One Agent Isn't Enough, Form a Team

[中文](README.md) · [English](README.en.md) · [日本語](README.ja.md)

s01 → ... → s13 → s14 → `s15` → [s16](../s16_team_protocols/) → s17 → s18 → s19 → s20
> *"One agent isn't enough, form a team"* — File-based inboxes + teammate threads.
>
> **Harness Layer**: Teams — Multi-agent collaboration, message bus.

---

## The Problem

"Refactor the entire backend" touches auth, database layer, API routes, and tests. One agent working on API routes no longer has auth module details in context. The context window is limited, a single agent can't cover every module.

s06's sub-agents are temps, called in for one job, then gone. Some tasks need teammates that can communicate and collaborate.

---

## The Solution

![Agent Teams Overview](images/agent-teams-overview.en.svg)

Teaching code carries forward S14's capabilities (prompt assembly, task system, background execution, cron scheduling). To stay focused on the team mechanism, it omits full error recovery, memory, and skill systems. Added: **MessageBus** (file-based inboxes), **spawn_teammate_thread** (launch teammate threads), **inbox injection** (Lead receives teammate messages and injects into history).

Sub-agent vs Teammate:

| | s06 Sub-agent | s15 Teammate |
|---|---|---|
| Lifetime | One-shot, destroyed after use | Multi-turn (teaching: 10 rounds; real CC: idle loop) |
| Communication | Only returns conclusion | Async inbox, communicate anytime |
| Context | Fully isolated | Shared via messages |
| Count | One lead + occasional sub-agent | One Lead + multiple teammates |

---

## How It Works

![Team Topology](images/team-topology.en.svg)

### MessageBus: File-Based Inboxes

Each agent (including Lead and teammates) has a `.jsonl` inbox. Send = append a JSON line to the target's file. Read = read file + delete (consumption):

```csharp
public sealed class MessageBus
{
    private readonly string _mailboxDir;
    private readonly object _lock = new();

    public void Send(string from, string to, string content, string msgType = "message")
    {
        var msg = new MailboxMessage
        {
            From = from,
            To = to,
            Content = content,
            Type = msgType,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };
        var inbox = Path.Combine(_mailboxDir, $"{to}.jsonl");
        lock (_lock)
        {
            File.AppendAllText(inbox, JsonSerializer.Serialize(msg) + "\n");
        }
    }

    public List<MailboxMessage> ReadInbox(string agent)
    {
        var inbox = Path.Combine(_mailboxDir, $"{agent}.jsonl");
        if (!File.Exists(inbox)) return new List<MailboxMessage>();
        lock (_lock)
        {
            var msgs = File.ReadAllText(inbox)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonSerializer.Deserialize<MailboxMessage>(l)!)
                .ToList();
            File.Delete(inbox);  // consume: read + delete
            return msgs;
        }
    }
}
```

Why files instead of in-memory queues? Teaching code uses files because they're intuitive and observable across threads. Real CC also uses file inboxes (`~/.claude/teams/{team}/inboxes/`) but adds `proper-lockfile` for concurrent write safety. The teaching version's `read_inbox` has a read + unlink race, concurrent reads could lose messages, acceptable for teaching purposes.

### spawn_teammate_thread: Launching a Teammate

Lead calls the `spawn_teammate` tool to start a teammate. The teammate runs in its own daemon thread with its own system prompt, messages, and simplified tool set:

```csharp
string SpawnTeammateThread(string name, string role, string prompt)
{
    var system = $"You are '{name}', a {role}. Use tools to complete tasks.";
    var messages = new List<Message> { Message.UserText(prompt) };
    var subTools = new ToolRegistry();
    BashTool.Register(subTools, _workDir);
    FileTools.Register(subTools, _workDir);

    new Thread(async () =>
    {
        for (var i = 0; i < 10; i++)           // max 10 rounds
        {
            var inbox = _bus.ReadInbox(name);
            if (inbox.Count > 0)
            {
                var json = JsonSerializer.Serialize(inbox);
                messages.Add(Message.UserText($"<inbox>{json}</inbox>"));
            }
            var response = await _client.CreateMessageAsync(
                system, messages, subTools.AllSpecs().ToList(), maxTokens: 8000);
            // ... execute tools, process results
        }
        // Send final summary to Lead
        _bus.Send(name, "lead", summary, "result");
    }) { IsBackground = true }.Start();

    return $"Spawned teammate '{name}' (role: {role})";
}
```

Key design:
- **Simplified tool set**: bash, read, write, send_message. Teaching code omits tasks and cron to focus on communication. Real CC teammates also have TaskCreate, TaskUpdate, etc., the task system is shared across the team
- **Teaching: 10 rounds max**: prevents infinite loops. Real CC uses idle loop: after each round, send `idle_notification`, wait for inbox messages, resume on arrival, exit only on `shutdown_request`
- **Auto-report on completion**: `BUS.send(name, "lead", summary)` sends the final result to Lead's inbox

### Lead's Inbox Injection

Lead checks inbox after each main loop iteration. Teammate messages are injected into history so the LLM can see and react to them:

```csharp
// After main loop iteration
var inbox = bus.ReadInbox("lead");
if (inbox.Count > 0)
{
    messages.Add(Message.UserText("<inbox>\n" +
        JsonSerializer.Serialize(inbox, new JsonSerializerOptions { WriteIndented = true }) +
        "\n</inbox>"));
}
```

Teaching code injects in the user input loop. Real CC is more refined, Lead's `useInboxPoller` checks every 1 second, submitting messages as new turns without waiting for user input.

### Permission Bubbling

Teaching code omits permission bubbling. Real CC's flow (`permissionSync.ts`, `useSwarmPermissionPoller.ts`):

1. Teammate encounters an operation needing approval → sends `permission_request` to Lead's inbox
2. Lead's `useInboxPoller` detects the request → routes to approval queue
3. User approves → Lead sends `permission_response` back to teammate
4. Teammate's `useSwarmPermissionPoller` (polls every 500ms) receives reply → continue or reject

### Putting It Together

```
1. Lead: "Build the backend: one agent isn't enough, form a team"
2. Lead → spawn_teammate("alice", "backend dev", "Create database schema")
3. Lead → spawn_teammate("bob", "frontend dev", "Write API client")
4. Alice thread starts → her own LLM call → bash "python manage.py migrate"
5. Bob thread starts → his own LLM call → write_file("client.ts", ...)
6. Alice done → BUS.send("alice", "lead", "Schema done: users, orders tables")
7. Bob done → BUS.send("bob", "lead", "Client written with types")
8. Lead next iteration → inbox injected into history → LLM sees both results
```

Two teammates work in parallel.

---

## Changes from s14

| Component | Before (s14) | After (s15) |
|-----------|-------------|-------------|
| Agent count | 1 | 1 Lead + N teammate threads |
| Communication | None | MessageBus + .mailboxes/*.jsonl |
| New classes | — | MessageBus, active_teammates dict |
| New functions | — | spawn_teammate_thread, run_send_message, run_check_inbox |
| Lead tools | 11 (s14) | + spawn_teammate, send_message, check_inbox (14) |
| Teammate tools | — | bash, read_file, write_file, send_message (4) |
| Permissions | Local decisions | Teaching code omits (real CC has bubbling) |

---

## Try It

```sh
cd learn-claude-code
dotnet run --project s15_agent_teams
```

Try these prompts:

1. `Spawn alice as a backend developer. Ask her to create a file called schema.sql with a users table.`
2. `Check your inbox for alice's result.`
3. `Spawn bob as a tester. Ask him to check if schema.sql exists and list its contents.`

What to observe: How does Lead spawn teammates? What do the `.mailboxes/` JSONL files look like? After teammates finish, is Lead's inbox injected into history?

---

## What's Next

Teammates can work and communicate. But if Lead wants Alice to shut down, killing the thread outright could leave half-written files. A graceful shutdown protocol is needed: Lead sends shutdown_request, teammate wraps up and exits.

s16 Team Protocols → Shutdown handshake and message conventions.

<details>
<summary>Deep Dive into CC Source</summary>

> The following is a complete analysis based on CC source code `spawnMultiAgent.ts`, `useInboxPoller.ts` (969 lines), `useSwarmPermissionPoller.ts` (330 lines), `teammateMailbox.ts`, `teamHelpers.ts`.

### 1. No Central Message Bus, It's the Filesystem

Teaching code uses a `MessageBus` class to send and receive messages. Real CC is more direct, each agent writes directly to other agents' inbox files.

Inbox path: `~/.claude/teams/{teamName}/inboxes/{agentName}.json`

Writes use `proper-lockfile` for concurrent write safety (up to 10 retries). Each file is a JSON array; appending reads → appends → writes back.

### 2. 15 Message Types

CC team communication has 15 structured message types (`teammateMailbox.ts`):

| Type | Direction | Purpose |
|------|-----------|---------|
| `plain text` | Both ways | Normal inter-teammate communication |
| `idle_notification` | Teammate→Lead | Teammate finished a turn, now idle |
| `permission_request` | Teammate→Lead | Teammate needs operation approval |
| `permission_response` | Lead→Teammate | Lead's approval result |
| `plan_approval_request` | Teammate→Lead | Teammate submits plan for review |
| `plan_approval_response` | Lead→Teammate | Lead's plan review |
| `shutdown_request` | Lead→Teammate | Request graceful shutdown |
| `shutdown_approved` | Teammate→Lead | Confirm shutdown |
| `shutdown_rejected` | Teammate→Lead | Reject shutdown (with reason) |
| `task_assignment` | Lead→Teammate | Assign a task |
| `team_permission_update` | Lead→Teammate | Broadcast permission changes |
| `mode_set_request` | Lead→Teammate | Change teammate's permission mode |
| `sandbox_permission_*` | Both ways | Network permission request/reply |
| `teammate_terminated` | System | Teammate removed notification |

Text messages are wrapped in `<teammate-message>` XML tags for delivery to the model.

### 3. Permission Bubbling: Bidirectional Polling

Teaching code omits permission bubbling. Real CC's flow (`permissionSync.ts`):

1. **Teammate** encounters operation needing approval → sends `permission_request` to Lead's inbox
2. **Lead's** `useInboxPoller` (polls every 1s) detects request → routes to `ToolUseConfirmQueue`
3. Lead's UI shows approval dialog with teammate name and color
4. User approves → Lead sends `permission_response` back to teammate's inbox
5. **Teammate's** `useSwarmPermissionPoller` (polls every 500ms) receives reply → continue or reject

### 4. Teammate Lifecycle

CC teammates are created by `spawnTeammate()` (`spawnMultiAgent.ts`):

1. **Spawn**: Create tmux pane (or in-process), assign color, write team config
2. **Work**: `useInboxPoller` checks inbox every 1s → submit as new turn when messages arrive
3. **Idle**: Stop hook fires → send `idle_notification` to Lead
4. **Shutdown**: Lead sends `shutdown_request` → teammate replies `shutdown_approved` → Lead cleans up

### 5. Team Config

Team registry at `~/.claude/teams/{teamName}/config.json` (`teamHelpers.ts`):

```json
{
  "name": "my-team",
  "leadAgentId": "lead@my-team",
  "members": [{
    "agentId": "researcher@my-team",
    "name": "researcher",
    "agentType": "general-purpose",
    "color": "blue",
    "isActive": true
  }]
}
```

Teammates cannot be nested (`AgentTool.tsx:273` explicitly forbids "teammates spawning other teammates").

</details>

<!-- translation-sync: zh@v1, en@v1, ja@v1 -->
