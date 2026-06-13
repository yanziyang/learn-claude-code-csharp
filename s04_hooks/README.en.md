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

**Core abstraction**: `HookBus` is an event→subscribers map. The agent loop never calls any business logic directly — it just fires events, and subscribers decide what to do. There are two flavors of subscriber:

- **External commands**: read from the `hooks` section of `appsettings.json` and spawned at runtime. **The teaching version uses only this path.**
- **C# delegates**: registered via `OnPreToolUse` / `OnPostToolUse` / `OnStop` / `OnUserPromptSubmit` / `OnBeforeLlmCall`, in-process. The API is kept for the few cases that genuinely need in-process state or TTY interaction.

`s04_hooks/Program.cs` has zero `OnXxx` calls:

```csharp
var n = agent.Hooks.ConfigureExternal(
    config.Hooks, workDir,
    log: msg => Console.Error.WriteLine(msg),
    timeout: TimeSpan.FromSeconds(30));
```

All behavior comes from `appsettings.json`. The loop code is untouched.

**Four events** cover a complete agent cycle:

| Event | Trigger | stdin payload | Typical use |
|-------|---------|---------------|-------------|
| `UserPromptSubmit` | after user input, before LLM call | `{hookEventName, userPrompt}` | log, inject context |
| `PreToolUse` | before tool execution | `{hookEventName, toolName, toolInput}` | permission checks, allow/deny lists |
| `PostToolUse` | after tool execution | `{hookEventName, toolName, toolInput, toolOutput}` | audit, side effects, output checks |
| `Stop` | before the loop exits | `{hookEventName, sessionStats:{toolCalls}}` | cleanup, write `session.json` |

**Protocol** (CC-compatible):

| exit | meaning |
|------|---------|
| `0` | allow |
| `2` | block; stderr (or stdout) becomes the reason |
| other | non-blocking error; bus logs a warning, still allows |

Optional stdout JSON overrides exit-code behavior:

```jsonc
{ "decision": "block", "reason": "..." }
```

**Matcher syntax** (per group):

| pattern | matches |
|---------|---------|
| empty / `null` | every tool |
| `"bash"` | exact |
| `"bash*"` | prefix |
| `"/regex/"` | regex (wrapped in `/.../`) |

**In `AgentCommon/Agent/AgentHarness.cs` only one line in the loop changes**: s03 called `check_permission(block)` directly; s04 calls `await Hooks.FirePreToolUseAsync(block, ct)`. Other fire points: `BeforeLlmCall` (top of `RunAsync`), `PostToolUse` (after tool execution), `FireStopOnHistory` (before the loop exits). External scripts and in-process delegates run side-by-side — the first block reason wins, and stderr flows into the model conversation.

---

## Changes from s03

| Component | Before (s03) | After (s04) |
|-----------|-------------|-------------|
| Extension method | `check_permission()` hardcoded in the loop | `HookBus` + the `hooks` section of `appsettings.json` |
| Where hooks are defined | Inline C# delegates | JSON config pointing at external JS/TS/any script |
| When hooks are loaded | compile-time binding | dynamic, at startup via `ConfigureExternal` |
| New types | — | `HooksConfig` / `HookGroup` / `HookCommand` / `ExternalHookEntry` / `ExternalHookRunner` / `ExternalHookLoader` |
| Loop | Directly calls `check_permission()` | `await Hooks.FirePreToolUseAsync(block, ct)` |
| Exit control | None | `FireStopOnHistoryAsync` can prevent exit |
| Input interception | None | `FireUserPromptSubmitAsync` can inject context |
| Protocol | — | CC-compatible: stdin = event JSON, exit 0/2 = allow/block |

---

## External hook scripts: JS / TS hang on too

The hooks above are C# delegates (`OnPreToolUse(...)`, etc.). The real CC hook model goes further: it lets `settings.json` point an event at an **external command** — the command receives the event JSON on stdin and its exit code decides the outcome. Python CC treats the command as any executable (`.py`, `.sh`, `.ps1`, `.exe`…). The wider ecosystem (Cline, Roo, Continue) writes them in **JavaScript / TypeScript**, so this port makes JS/TS hooks first-class.

### Wiring in `appsettings.json`

```jsonc
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "bash",
        "hooks": [ { "type": "command", "command": "node hooks/block-rm.js" } ] }
    ],
    "PostToolUse": [
      { "hooks": [ { "type": "command", "command": "node hooks/audit-log.js" } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "npx tsx hooks/summary.ts" } ] }
    ]
  }
}
```

- `matcher` empty = match every tool; `"bash"` = exact; `"bash*"` = prefix; `"/regex/"` = regex.
- `command` is split with a quote-aware tokenizer into **argv**, no shell.
- The first token is the executable (`node` / `tsx` / `npx` / `py` / …), the rest are args. The runtime is the user's choice — we don't embed a JS engine.

### Protocol: stdin / exit code / optional stdout

The event is written to the script's stdin as JSON:

| Event | JSON on stdin |
|-------|---------------|
| `PreToolUse` | `{ "hookEventName":"PreToolUse", "toolName":"bash", "toolInput":{ "command":"..." } }` |
| `PostToolUse` | `{ "hookEventName":"PostToolUse", "toolName":"bash", "toolInput":{...}, "toolOutput":"..." }` |
| `UserPromptSubmit` | `{ "hookEventName":"UserPromptSubmit", "userPrompt":"..." }` |
| `Stop` | `{ "hookEventName":"Stop", "sessionStats":{ "toolCalls": 5 } }` |

Exit code semantics (compatible with Python CC):

| exit | meaning |
|------|---------|
| `0` | allow |
| `2` | block; stderr (or stdout) becomes the reason |
| other | non-blocking error; bus logs a warning and allows |

An optional stdout JSON can override exit-code behavior:

```jsonc
// written to stdout
{ "decision": "block", "reason": "rm -rf / is not allowed" }
```

For TypeScript, point the command at `npx tsx hooks/foo.ts`. The runtime is interchangeable — `node`, `bun`, `deno`, `tsx`, `ts-node` all work; swap the first token.

### Full example: block `rm -rf`

`hooks/block-rm.js`:

```javascript
const DENY = [/\brm\s+-rf?\s+\//, /\bsudo\b/, /\bshutdown\b|\breboot\b/, /\bmkfs\b/, /\bdd\s+if=/];

(async () => {
  const event = JSON.parse(await readStdin());
  if (event.toolName !== "bash") process.exit(0);
  const cmd = (event.toolInput && event.toolInput.command) || "";
  for (const pat of DENY) {
    if (pat.test(cmd)) {
      console.error(`denied by ${pat}`);
      process.exit(2);  // 2 = block, stderr becomes the reason
    }
  }
  process.exit(0);
})();
```

`appsettings.json`:

```jsonc
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "bash",
        "hooks": [ { "type": "command", "command": "node hooks/block-rm.js" } ] }
    ]
  }
}
```

`s04_hooks/Program.cs` does one thing at startup — wire every external hook in one call:

```csharp
var n = agent.Hooks.ConfigureExternal(
    config.Hooks, workDir,
    log: msg => Console.Error.WriteLine(msg),
    timeout: TimeSpan.FromSeconds(30));
if (n > 0) Console.WriteLine($"[host] loaded {n} external hook(s) from appsettings.json");
```

`ConfigureExternal(config, workDir, log?, timeout?)` on the bus injects the `ExternalRunner`, sets `WorkDir`, and runs `ExternalHookLoader` to register every `command` declared under `hooks.<Event>[]` as an external subscriber. If `appsettings.json` has no `hooks` section it's a no-op and returns 0. Call it once during startup; in-process delegates and external scripts then run side-by-side, and the first rejection wins.

### Out of scope (teaching-version simplifications)

- **Embedded V8/Jint** — would pull a heavy dependency; TS would need an in-process transpiler.
- **CSX (Roslyn scripting)** — C#-only, doesn't match the JS/TS-first AI-tool ecosystem.
- **Process sandboxing** — timeout defaults to 30s (configurable); no cgroup/AppContainer isolation.
- **Allow vs deny/ask priority** — this port has no settings.json layer, so we don't reproduce CC `toolHooks.ts:325-331`'s priority merge.

---

## Shipped scripts: make the example runnable

`hooks/` ships seven example scripts. Copy `appsettings.example.json` → `appsettings.json` and they're all wired up. **The teaching version uses this "everything-from-config" shape** — `Program.cs` has zero `OnXxx` calls.

| script | event | matcher | behavior |
|--------|-------|---------|----------|
| `block-rm.js` | PreToolUse | `bash` | deny list: blocks `rm -rf /`, `sudo`, `shutdown`, `reboot`, `mkfs`, `dd if=` |
| `path-guard.js` | PreToolUse | `write_file\|edit_file` | rejects writes outside cwd (exit 2) |
| `log-pretool.js` | PreToolUse | *all* | prints `toolName(key=val, …)` to stderr |
| `log-prompt.js` | UserPromptSubmit | — | prints `cwd=` and the prompt (truncated 60 chars) to stderr |
| `large-output.js` | PostToolUse | *all* | warns when tool output > 100_000 chars |
| `audit-log.js` | PostToolUse | *all* | appends one JSONL line per tool call to `.memory/audit.log` |
| `summary.ts` | Stop | — | writes `.memory/session.json` (requires `npx tsx`) |

Each script is independently runnable for unit testing:

```sh
echo '{"hookEventName":"PreToolUse","toolName":"bash","toolInput":{"command":"sudo apt update"}}' | node hooks/block-rm.js
# → exit 2, stderr: "block-rm: denied by pattern /\bsudo\b/"
```

To disable a category, comment out the matching block in `appsettings.json`; to swap an implementation, point the `command` at your own script. The loop code doesn't change.

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
4. `Write something to ../../../etc/passwd` (path-guard rejects, exit 2)

What to watch for: before each tool execution, does the `[HOOK]` log appear? When permission is denied, was it intercepted by `block-rm.js` / `path-guard.js`, or hardcoded in the loop? — The answer should be the former: s04's `Program.cs` contains zero permission-related code.

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

<!-- translation-sync: zh@v1, en@v1, ja@v0 -->
