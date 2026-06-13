# s06: Context Compact

`s01 > s02 > s03 > s04 > s05 > [ s06 ] | s07 > s08 > s09 > s10 > s11 > s12`

> *"コンテキストはいつか溢れる、空ける手段が要る"* -- 4層圧縮で無限セッションを実現。
>
> **Harness 層**: 圧縮 -- クリーンな記憶、無限のセッション。

## 問題

コンテキストウィンドウは有限だ。1000行のファイルに対する `read_file` 1回で約4000トークンを消費する。30ファイルを読み20回のbashコマンドを実行すると、100,000トークン超。圧縮なしでは、エージェントは大規模コードベースで作業できない。

## 解決策

積極性を段階的に上げる4層構成 (`AgentCommon/Compact/ContextCompactor.cs`):

```
Every turn:
+------------------+
| Tool call result |
+------------------+
        |
        v
[L3: ToolResultBudget]    (silent, every turn)
  Persist large tool_results to .task_outputs/tool-results/
  Replace in-context copy with a placeholder + file path
        |
        v
[L1: SnipCompact]         (silent, every turn)
  Drop middle messages when count > 50
        |
        v
[L2: MicroCompact]        (silent, every turn)
  Replace tool_result > 3 turns old with "[Previous: used {tool_name}]"
        |
        v
[Check: tokens > 50000?]
   |               |
   no              yes
   |               |
   v               v
continue    [L4: CompactHistory]
            Save transcript to .transcripts/
            LLM summarizes conversation.
            Replace all messages with [summary] + tail.
```

## 仕組み

1. **L3 -- ToolResultBudget**: 大きすぎる tool_result をディスクへ退避し、コンテキスト内のコピーはプレースホルダに置き換える。

```csharp
private void ToolResultBudget(List<Message> messages)
{
    var last = messages[^1];
    if (last.Role != "user") return;
    var blocks = last.Content.OfType<ToolResultBlock>().ToList();
    if (blocks.Count == 0) return;

    long total = blocks.Sum(b => b.Content?.Length ?? 0);
    if (total <= _toolResultBudgetBytes) return;

    var ranked = blocks.OrderByDescending(b => b.Content?.Length ?? 0).ToList();
    foreach (var block in ranked)
    {
        if (total <= _toolResultBudgetBytes) break;
        var path = PersistToDisk(block.Content);   // .task_outputs/ に書き出し
        block.Content = $"[See {path} for full output]";
        total -= block.Content.Length;
    }
}
```

2. **L1 + L2 -- Snip + MicroCompact**: 各LLM呼び出しの前に実行。

```csharp
public void PrepareBeforeLlm(List<Message> messages)
{
    ToolResultBudget(messages);
    SnipCompact(messages);    // count > maxMessages なら中身を切り詰め
    MicroCompact(messages);   // 古い tool_result をプレースホルダに
}
```

3. **L4 -- CompactHistory**: モデルが `prompt_too_long` を返したら、完全なトランスクリプトをディスクへ保存し、LLMに要約を依頼する。

```csharp
public async Task<List<Message>> EmergencyAsync(List<Message> messages, CancellationToken ct = default)
{
    var transcriptPath = WriteTranscript(messages);
    _onLog?.Invoke($"[transcript saved: {transcriptPath}]");
    var summary = await SummarizeHistoryAsync(messages, ct);

    var tailStart = Math.Max(0, messages.Count - 5);
    return new List<Message> { Message.UserText($"[Reactive compact]\n\n{summary}") }
        .Concat(messages.Skip(tailStart))
        .ToList();
}
```

4. ループが4層すべてを `AgentHarness.RunAsync` 経由で自動統合する。

```csharp
// AgentHarness.RunAsync 内 — 各反復で呼ばれる:
Compactor.PrepareBeforeLlm(messages);

try
{
    response = await Client.CreateMessageAsync(systemPrompt, messages, ...);
}
catch (InvalidOperationException ex) when (IsPromptTooLong(ex))
{
    messages.Clear();
    messages.AddRange(await Compactor.EmergencyAsync(messages, ct));
    response = await Client.CreateMessageAsync(systemPrompt, messages, ...);
}
```

トランスクリプトがディスク上に完全な履歴を保持する。何も真に失われず、アクティブなコンテキストの外に移動されるだけ。

## s05からの変更点

| Component      | Before (s05)     | After (s06)                |
|----------------|------------------|----------------------------|
| Tools          | 5                | 5 (unchanged)              |
| Context mgmt   | None             | Four-layer compression     |
| Micro-compact  | None             | Old results -> placeholders|
| Auto-compact   | None             | Reactive path on overflow  |
| Transcripts    | None             | Saved to .transcripts/     |

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s08_context_compact
```

1. `Read every C# file in the AgentCommon/ directory one by one` (L2 micro-compact が古い結果を置換するのを観察)
2. `Keep reading files until compression triggers automatically`
3. `Force a long context to overflow and observe the L4 reactive compact`
