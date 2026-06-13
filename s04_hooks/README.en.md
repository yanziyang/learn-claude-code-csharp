# s04: Hooks — Hang on the Loop, Don't Write into It

[中文](README.md) · [English](README.en.md) · [日本語](README.ja.md)

s01 → s02 → s03 → `s04` → [s05](../s05_todo_write/) → s06 → ... → s20

> *"Hang on the loop, don't write into it"* — Hooks inject extension logic before and after tool execution.
>
> **Harness Layer**: Hooks — Extension points that don't invade the loop.

---

## The Problem

The s03 Agent has permission checks. But every new check, "log every bash call", "auto git add after writes", requires modifying the `agent_loop` function.

The loop quickly becomes this:

```csharp
async Task AgentLoop(List<Message> messages)
{
    while (true)
    {
        // ... LLM call ...
        foreach (var block in response.Content.OfType<ToolUseBlock>())
        {
            LogToFile(block);
            CheckPermission(block);
            NotifySlack(block);
            var output = Execute(block);
            AutoGitAdd(block);
        }
    }
}
```

What you want to extend is the Agent's behavior, but what you're modifying is the loop itself. The loop should be a stable core; extensions should hang on the outside.

---

## The Solution

![Hooks Overview](images/hooks-overview.en.svg)

The s03 loop and permission logic are fully preserved. The only change is moving `check_permission()` from inside the loop body onto a hook. The loop no longer directly calls any check function. Instead it calls `trigger_hooks("PreToolUse", block)`, and the registry decides what to run.

Four events, covering a complete agent cycle:

| Event | Trigger Timing | Typical Use |
|-------|---------------|-------------|
| UserPromptSubmit | After user input, before entering LLM | Input validation, context injection |
| PreToolUse | Before tool execution | Permission checks, logging |
| PostToolUse | After tool execution | Side effects (auto git add etc.), output checking |
| Stop | When the loop is about to exit | Cleanup (CC also supports force continuation) |

Extensions are added via `register_hook()`. The loop only calls `trigger_hooks()`.

---

## How It Works

**Hook registry**: a dict mapping event names to callback lists.

```csharp
var hooks = new HookBus();

hooks.OnUserPromptSubmit(query => { });
hooks.OnPreToolUse(block => null);   // non-null return blocks the tool
hooks.OnPostToolUse((block, output) => { });
hooks.OnStop(() => null);   // non-null return forces continuation
```

In the teaching version, PreToolUse returning non-None means block execution; Stop returning non-None means force continuation. UserPromptSubmit and PostToolUse return values are unused.

**UserPromptSubmit**, triggers after user input, before entering the LLM. CC can intercept or modify input; the teaching version only logs:

```csharp
void ContextInjectHook(string query)
{
    Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit: working in {workDir}\u001b[0m");
}

agent.Hooks.OnUserPromptSubmit(ContextInjectHook);
```

In the main loop, triggered right after user input:

```csharp
var query = Console.ReadLine() ?? "";
agent.FireUserPromptSubmit(query);    // ← before entering LLM
history.Add(Message.UserText(query));
await agent.RunUntilDoneAsync(history);
```

**PreToolUse / PostToolUse**, hooks before and after tool execution. s03's permission check logic is now wrapped as a PreToolUse hook, plus a logging hook and a large-output reminder:

```csharp
// PreToolUse: permission check (s03 logic, moved from loop to hook)
string? PermissionHook(ToolUseBlock block)
{
    if (block.Name == "bash")
    {
        var cmd = block.Input.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
        foreach (var pattern in denyList)
        {
            if (cmd.Contains(pattern, StringComparison.Ordinal))
                return "Permission denied by deny list";
        }
    }
    if (block.Name is "write_file" or "edit_file"
        && block.Input.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
    {
        try { _ = PathGuard.SafePath(workDir, p.GetString() ?? ""); }
        catch
        {
            Console.Write("   Allow? [y/N] ");
            var choice = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (choice is not ("y" or "yes")) return "Permission denied by user";
        }
    }
    return null;
}

// PreToolUse: logging
void LogHook(ToolUseBlock block)
{
    Console.WriteLine($"[HOOK] {block.Name}(...)");
}

// PostToolUse: large output reminder
void LargeOutputHook(ToolUseBlock block, string output)
{
    if (output.Length > 100_000)
        Console.WriteLine($"[HOOK] ⚠ Large output from {block.Name}");
}

agent.Hooks.OnPreToolUse(PermissionHook);
agent.Hooks.OnPreToolUse(LogHook);
agent.Hooks.OnPostToolUse(LargeOutputHook);
```

**Stop**, triggers when the loop is about to exit (`stop_reason != "tool_use"`). The teaching version prints a cleanup summary:

```csharp
string? SummaryHook(IReadOnlyList<Message>? messages)
{
    var toolCount = messages?
        .SelectMany(m => m.Content)
        .OfType<ToolResultBlock>()
        .Count() ?? 0;
    Console.WriteLine($"\u001b[90m[HOOK] Stop: session used {toolCount} tool calls\u001b[0m");
    return null;   // return null = allow stop, return string = force continuation
}

agent.Hooks.OnStop(SummaryHook);
```

In agent_loop, triggered before exit:

```csharp
if (response.StopReason != "tool_use")
{
    var force = agent.Hooks.FireStopOnHistory(messages);   // ← before exiting
    if (force is not null)
    {
        // hook returned a message → inject it and continue
        messages.Add(Message.UserText(force));
        continue;
    }
    return;
}
```

**Only one change in the loop**: s03 directly called `check_permission(block)`, s04 replaces it with `trigger_hooks("PreToolUse", block)`:

```csharp
foreach (var block in response.Content.OfType<ToolUseBlock>())
{
    // s03: if (!CheckPermission(block)) { ... }
    // s04: hooks replace hardcoding
    var blocked = agent.Hooks.FirePreToolUse(block);
    if (blocked is not null)
    {
        results.Add(new ToolResultBlock(block.Id, blocked));
        continue;
    }

    var output = tools.Invoke(block.Name, block.Input);
    agent.Hooks.FirePostToolUse(block, output);
    results.Add(new ToolResultBlock(block.Id, output));
}
```

Four hooks cover the critical nodes of the agent cycle: input → before execution → after execution → exit. The loop only calls trigger_hooks(); all logic lives in hook callbacks.

---

## Changes from s03

| Component | Before (s03) | After (s04) |
|-----------|-------------|-------------|
| Extension method | check_permission() hardcoded in the loop | HOOKS registry + trigger_hooks() |
| New functions | — | register_hook, trigger_hooks |
| Hook callbacks | — | context_inject_hook, permission_hook, log_hook, large_output_hook, summary_hook |
| Loop | Directly calls check_permission() | Calls trigger_hooks("PreToolUse", ...) |
| Exit control | None | trigger_hooks("Stop", ...) can prevent exit |
| Input interception | None | trigger_hooks("UserPromptSubmit", ...) can inject context |

---

## Try It

```sh
cd learn-claude-code
dotnet run --project s04_hooks
```

Try these prompts:

1. `Read the file README.md` (should pass directly, observe hook logs)
2. `Create a file called test.txt` (after creation, observe if PostToolUse fires)
3. `Delete all temporary files in /tmp` (bash + rm triggers permission hook)

What to watch for: Before each tool execution, does the `[HOOK]` log appear? When permission is denied, was it intercepted by a hook or hardcoded in the loop?

---

## What's Next

The Agent can now safely execute operations. But does it ever stop to think "what should I do first, and what next?" Given a complex task, does it jump straight in, or plan first?

→ s05 TodoWrite: Give the Agent a planning tool. Make a list first, then execute.

<details>
<summary>Dive into CC Source Code</summary>

> The following is based on a complete analysis of CC source code `toolHooks.ts` (650 lines), `hooks.ts`, `stopHooks.ts`, and `coreTypes.ts`.

### 1. Hook Events: Not Just 4, but 27

The teaching version covers only PreToolUse and PostToolUse. CC actually has 27 hook events (`coreTypes.ts:25-53`):

| Category | Events |
|----------|--------|
| Tool-related | `PreToolUse`, `PostToolUse`, `PostToolUseFailure` |
| Session-related | `SessionStart`, `SessionEnd`, `Stop`, `StopFailure`, `Setup` |
| User interaction | `UserPromptSubmit`, `Notification`, `PermissionRequest`, `PermissionDenied` |
| Sub-agents | `SubagentStart`, `SubagentStop` |
| Compaction-related | `PreCompact`, `PostCompact` |
| Team-related | `TeammateIdle`, `TaskCreated`, `TaskCompleted` |
| Other | `Elicitation`, `ElicitationResult`, `ConfigChange`, `WorktreeCreate`, `WorktreeRemove`, `InstructionsLoaded`, `CwdChanged`, `FileChanged` |

The teaching version covers only 4 core events (UserPromptSubmit, PreToolUse, PostToolUse, Stop) because they cover every critical node of a complete agent cycle. The other 23 follow the same pattern.

### 2. HookResult Common Fields

CC's `HookResult` (`types/hooks.ts:260-275`) has 14 fields. Common ones:

| Field | Type | Purpose |
|-------|------|---------|
| `message` | Message | Optional UI message |
| `blockingError` | HookBlockingError | Blocking error → injected into conversation for model self-correction |
| `outcome` | success/blocking/non_blocking_error/cancelled | Execution result |
| `preventContinuation` | boolean | Prevent subsequent execution |
| `stopReason` | string | Stop reason description |
| `permissionBehavior` | allow/deny/ask/passthrough | Hook returns permission decision |
| `updatedInput` | Record | Modify tool input |
| `additionalContext` | string | Additional context |
| `updatedMCPToolOutput` | unknown | MCP tool output modification |

### 3. Key Invariant: Hook 'allow' Cannot Bypass deny/ask Rules

This is the most important security design in CC's permission system (`toolHooks.ts:325-331`): **when a hook returns allow, it still checks settings.json deny/ask rules.** Even if the user's hook script says "allow", if the tool is disabled in settings.json, the operation is still blocked.

The teaching version doesn't have this layer; hooks returning non-None directly interrupt. This is sufficient for teaching, but would create a security vulnerability in production.

### 4. stopHookActive Mechanism

CC's Stop hooks have an infinite-loop prevention mechanism (`query.ts:212,1300`): the `stopHookActive` state field. When stop hooks produce a blockingError, the loop re-enters with `stopHookActive: true`. Subsequent iterations see this flag and don't trigger stop hooks again. This prevents a never-stopping bug: model self-corrects → stop hook errors again → model self-corrects again → stop hook errors again...

### 5. hook_stopped_continuation

When PostToolUse hooks return `preventContinuation: true`, a `hook_stopped_continuation` attachment is produced (`toolHooks.ts:117-130`). query.ts (L1388-1393) detects it and sets `shouldPreventContinuation = true`, causing the loop to exit. This is the mechanism for "hooks gracefully shut down the Agent" — not a crash, but a completion.

### Teaching Version Simplifications Are Intentional

- 27 events → 4 (UserPromptSubmit/PreToolUse/PostToolUse/Stop): covers agent cycle critical nodes
- 14 fields → simple return values (None = continue, non-None = interrupt/continue): minimal cognitive load
- Hook allow vs deny/ask invariant → omitted: teaching version has no settings.json layer
- stopHookActive → omitted: teaching version Stop hook only does simple continuation, no infinite-loop prevention needed

</details>

<!-- translation-sync: zh@v1, en@v1, ja@v1 -->
