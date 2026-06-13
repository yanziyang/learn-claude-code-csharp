# s04: Hooks — ループに掛ける、ループには書き込まない

[中文](README.md) · [English](README.en.md) · [日本語](README.ja.md)

s01 → s02 → s03 → `s04` → [s05](../s05_todo_write/) → s06 → ... → s20

> *"ループに掛ける、ループには書き込まない"* — フックがツール実行の前後に拡張ロジックを注入する。
>
> **Harness レイヤー**: フック — ループを侵襲しない拡張ポイント。

---

## 課題

s03 の Agent には権限チェックがある。しかし新しいチェックを追加するたび、「bash 呼び出しを毎回ログに記録」「操作後に自動 git add」、`agent_loop` 関数を修正する必要がある。

ループはすぐにこうなる：

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

拡張したいのは Agent の振る舞いなのに、変更しているのはループそのもの。ループは安定した核心であるべき。拡張は外側に掛ける。

---

## ソリューション

![Hooks Overview](images/hooks-overview.ja.svg)

s03 のループと権限ロジックは完全に保持される。唯一の変更点は `check_permission()` をループ本体内からフックに移動したこと。ループはもうチェック関数を直接呼び出さず、代わりに `trigger_hooks("PreToolUse", block)` を呼び、登録済みのフックが何を実行するかを決める。

4 つのイベントで、完全な agent cycle をカバー：

| イベント | 発火タイミング | 典型的な用途 |
|----------|--------------|-------------|
| UserPromptSubmit | ユーザー入力後、LLM に入る前 | 入力バリデーション、コンテキスト注入 |
| PreToolUse | ツール実行前 | 権限チェック、ログ記録 |
| PostToolUse | ツール実行後 | 副作用（自動 git add など）、出力チェック |
| Stop | ループが終了する直前 | クリーンアップ（CC は強制続行もサポート） |

拡張は `register_hook()` で追加する。ループは `trigger_hooks()` を呼ぶだけ。

---

## 仕組み

**フック登録簿**：イベント名をコールバックリストにマッピングする辞書。

```csharp
var hooks = new HookBus();

hooks.OnUserPromptSubmit(query => { });
hooks.OnPreToolUse(block => null);   // non-null return blocks the tool
hooks.OnPostToolUse((block, output) => { });
hooks.OnStop(() => null);   // non-null return forces continuation
```

教学版では、PreToolUse の非 None 戻り値は実行阻止を意味し、Stop の非 None 戻り値は強制続行を意味する。UserPromptSubmit と PostToolUse の戻り値は未使用。

**UserPromptSubmit**、ユーザー入力後、LLM に入る前に発火。CC では入力の横取りや変更が可能、教学版はログ出力のみ：

```csharp
void ContextInjectHook(string query)
{
    Console.WriteLine($"\u001b[90m[HOOK] UserPromptSubmit: working in {workDir}\u001b[0m");
}

agent.Hooks.OnUserPromptSubmit(ContextInjectHook);
```

メインループでは、ユーザー入力直後に発火：

```csharp
var query = Console.ReadLine() ?? "";
agent.FireUserPromptSubmit(query);    // ← before entering LLM
history.Add(Message.UserText(query));
await agent.RunUntilDoneAsync(history);
```

**PreToolUse / PostToolUse**、ツール実行の前後のフック。s03 の権限チェックロジックは PreToolUse フックに包まれ、さらにログフックと大出力リマインダーが追加される：

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

**Stop**、ループが終了する直前に発火（`stop_reason != "tool_use"`）。教学版ではクリーンアップ統計を印刷：

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

agent_loop 内では、終了前に発火：

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

**ループ内で変更されたのは一箇所だけ**：s03 は直接 `check_permission(block)` を呼び出していたが、s04 は `trigger_hooks("PreToolUse", block)` に置き換えた：

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

4 つのフックが agent cycle の重要ノードをカバー：入力→実行前→実行後→終了。ループは trigger_hooks() を呼ぶだけで、具体的なロジックは全てフックコールバックにある。

---

## s03 からの変更

| コンポーネント | 変更前 (s03) | 変更後 (s04) |
|--------------|-------------|-------------|
| 拡張方式 | check_permission() をループ内にハードコード | HOOKS 登録簿 + trigger_hooks() |
| 新規関数 | — | register_hook, trigger_hooks |
| フックコールバック | — | context_inject_hook, permission_hook, log_hook, large_output_hook, summary_hook |
| ループ | check_permission() を直接呼び出し | trigger_hooks("PreToolUse", ...) を呼び出し |
| 終了制御 | なし | trigger_hooks("Stop", ...) が終了を阻止可能 |
| 入力横取り | なし | trigger_hooks("UserPromptSubmit", ...) がコンテキスト注入可能 |

---

## 試してみよう

```sh
cd learn-claude-code
dotnet run --project s04_hooks
```

以下のプロンプトを試してみよう：

1. `Read the file README.md`（そのまま通過するはず、フックログを観察）
2. `Create a file called test.txt`（作成後、PostToolUse が発火するか観察）
3. `Delete all temporary files in /tmp`（bash + rm で権限フックが発動）

観察のポイント：各ツール実行前に `[HOOK]` ログが表示されるか？ 権限が拒否されたとき、フックが拦截したのか、ループ内のハードコードが拦截したのか？

---

## 次へ

Agent は安全に操作を実行できるようになった。しかし「まず何をして、次に何をすべきか」を立ち止まって考えたことはあるか？ 複雑なタスクを与えたとき、すぐに取り掛かるのか、まず計画を立てるのか？

→ s05 TodoWrite：Agent に計画ツールを与える。まずリストを作り、それから実行。

<details>
<summary>CC ソースコードを深掘り</summary>

> 以下は CC ソースコード `toolHooks.ts`（650 行）、`hooks.ts`、`stopHooks.ts`、`coreTypes.ts` の完全分析に基づく。

### 一、Hook イベント：4 つではなく 27 個

教育版は PreToolUse と PostToolUse のみを取り上げる。CC には実際に 27 のフックイベントがある（`coreTypes.ts:25-53`）：

| カテゴリ | イベント |
|----------|---------|
| ツール関連 | `PreToolUse`, `PostToolUse`, `PostToolUseFailure` |
| セッション関連 | `SessionStart`, `SessionEnd`, `Stop`, `StopFailure`, `Setup` |
| ユーザー対話 | `UserPromptSubmit`, `Notification`, `PermissionRequest`, `PermissionDenied` |
| サブエージェント | `SubagentStart`, `SubagentStop` |
| 圧縮関連 | `PreCompact`, `PostCompact` |
| チーム関連 | `TeammateIdle`, `TaskCreated`, `TaskCompleted` |
| その他 | `Elicitation`, `ElicitationResult`, `ConfigChange`, `WorktreeCreate`, `WorktreeRemove`, `InstructionsLoaded`, `CwdChanged`, `FileChanged` |

教育版は 4 つのコアイベント（UserPromptSubmit、PreToolUse、PostToolUse、Stop）のみを取り上げる。これらで agent cycle の重要ノードを全てカバーできる。残り 23 個は同じパターン。

### 二、HookResult よく使うフィールド抜粋

CC の `HookResult`（`types/hooks.ts:260-275`）には 14 のフィールドがある。よく使うもの：

| フィールド | 型 | 用途 |
|-----------|-----|------|
| `message` | Message | オプションの UI メッセージ |
| `blockingError` | HookBlockingError | ブロッキングエラー → 会話に注入してモデルが自己修正 |
| `outcome` | success/blocking/non_blocking_error/cancelled | 実行結果 |
| `preventContinuation` | boolean | 後続実行を阻止 |
| `stopReason` | string | 停止理由の説明 |
| `permissionBehavior` | allow/deny/ask/passthrough | フックが権限決定を返す |
| `updatedInput` | Record | ツール入力の変更 |
| `additionalContext` | string | 追加コンテキスト |
| `updatedMCPToolOutput` | unknown | MCP ツール出力の変更 |

### 三、重要な不変条件：Hook 'allow' は deny/ask ルールをバイパスできない

これは CC 権限システムで最も重要なセキュリティ設計（`toolHooks.ts:325-331`）：**フックが allow を返しても、settings.json の deny/ask ルールをチェックする。** ユーザーのフックスクリプトが「許可」と言っても、settings.json でそのツールが無効になっていれば、操作は阻止される。

教育版にはこの階層がない。フックが非 None を返せば直接中断。教育目的では十分だが、本番環境ではセキュリティホールになる。

### 四、stopHookActive 機構

CC の Stop フックには無限ループ防止機構がある（`query.ts:212,1300`）：`stopHookActive` 状態フィールド。Stop フックが blockingError を発生させると、ループは `stopHookActive: true` で次のラウンドに再入する。後続のイテレーションではこのフラグを見て Stop フックを再トリガーしない。これで「永久に止まらない」バグを防ぐ：モデルが自己修正 → Stop フックが再度エラー → モデルが再修正 → Stop フックが再度エラー... を防止。

### 五、hook_stopped_continuation

PostToolUse フックが `preventContinuation: true` を返すと、`hook_stopped_continuation` アタッチメントが生成される（`toolHooks.ts:117-130`）。query.ts（L1388-1393）はそれを検出して `shouldPreventContinuation = true` を設定し、ループが終了する。これは「フックが Agent を優雅に停止させる」機構 — クラッシュではなく、完了。

### 教育版の簡略化は意図的

- 27 イベント → 4（UserPromptSubmit/PreToolUse/PostToolUse/Stop）：agent cycle の重要ノードをカバー
- 14 フィールド → 単純な戻り値（None = 続行、非 None = 中断/続行）：認知負荷を最小限に
- Hook allow vs deny/ask の不変条件 → 省略：教育版に settings.json 層はない
- stopHookActive → 省略：教育版の Stop フックは単純な続行のみ、無限ループ防止は不要

</details>

<!-- translation-sync: zh@v1, en@v1, ja@v1 -->
