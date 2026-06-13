# s08: Context Compact — コンテキストはいつか満杯になる、場所を空ける方法が必要

[中文](README.md) · [English](README.en.md) · [日本語](README.ja.md)

s01 → s02 → s03 → s04 → s05 → s06 → s07 → `s08` → [s09](../s09_memory/) → s10 → ... → s20
> *"Context will fill up — have a way to make room"* — 4層圧縮戦略、安価なものを先に、高価なものを後に実行。
>
> **Harness レイヤー**: 圧縮 — クリーンな記憶、無限のセッション。

---

## 課題

Agent が動いている途中で、止まってしまう。

bash、read、write は揃っており、能力は十分。しかし 1000 行のファイル（~4000 token）を読み、さらに 30 のファイルを読み、20 のコマンドを実行したとします。各コマンドの出力、各ファイルの内容がすべて `messages` リストに蓄積されます。

コンテキストウィンドウには上限があります。満杯になると、API は即座に拒否します：`prompt_too_long`。

圧縮しなければ、Agent は大規模プロジェクトではまともに動けません。

---

## ソリューション

![Compact Overview](images/compact-overview.ja.svg)

s07 のフック構造、スキルロード、サブ Agent の骨格を維持し、圧縮に焦点を当てるため一部のツールは省略。コアの変更点：各 LLM 呼び出し前に 3 層のプリプロセッサ（0 API）を挿入し、token が閾値を超えた場合は LLM 要約（1 API）をトリガー、API エラー時には緊急トリムを実行。

コア設計：安価なものを先に、高価なものを後に。

---

## 仕組み

![4層圧縮パイプライン](images/compaction-layers.ja.svg)

### L1: snip_compact — 無関係な古い会話を切り捨て

Agent が 80 ラウンドの会話を実行し、`messages` が 160 件まで溜まった。先頭の「hello.py を作って」は現在の作業とほぼ無関係だが、スペースを占有し続けている。

メッセージ数が 50 を超えた場合 → 先頭 3 件（初期コンテキスト）と末尾 47 件（現在の作業）を保持して中間を切り詰める。ただし切れ目だけは調整し、`assistant(tool_use)` と後続の `user(tool_result)` を分断しない：

```csharp
private void SnipCompact(List<Message> messages)
{
    if (messages.Count <= _maxMessages) return;
    var keepHead = _keepHead;
    var tailStart = messages.Count - _keepTail;

    if (keepHead > 0 && keepHead < messages.Count && MessageHasToolUse(messages[keepHead - 1]))
    {
        while (keepHead < messages.Count && IsToolResultMessage(messages[keepHead]))
        {
            keepHead++;
        }
    }
    if (tailStart > 0 && tailStart < messages.Count
        && IsToolResultMessage(messages[tailStart])
        && MessageHasToolUse(messages[tailStart - 1]))
    {
        tailStart--;
    }
    if (keepHead >= tailStart) return;

    var snipped = tailStart - keepHead;
    messages.RemoveRange(keepHead, snipped);
    messages.Insert(keepHead, Message.UserText($"[snipped {snipped} messages]"));
}
```

切り捨て自体は単純なままで、境界だけを保護する。残ったメッセージ内の `tool_result` 内容はまだ蓄積され続けている。34 番目のメッセージに 30KB の古いファイル内容が残っているかもしれない。→ L2。

### L2: micro_compact — 古いツール結果をプレースホルダに置換

![古い結果のプレースホルダ](images/micro-compact.ja.svg)

Agent が連続して 10 個のファイルを読んだ。1〜7 回目の完全な内容はまだコンテキストに残っており、もう不要だが、大量のスペースを占有している。

直近 3 件の `tool_result` の完全な内容のみを保持し、それより古いものは 1 行のプレースホルダに置換：

```csharp
private const int KEEP_RECENT_TOOL_RESULTS = 3;

private void MicroCompact(List<Message> messages)
{
    var allResults = new List<(int mi, int bi, ToolResultBlock block)>();
    for (var mi = 0; mi < messages.Count; mi++)
    {
        var m = messages[mi];
        if (m.Role != "user") continue;
        for (var bi = 0; bi < m.Content.Count; bi++)
        {
            if (m.Content[bi] is ToolResultBlock tr) allResults.Add((mi, bi, tr));
        }
    }
    if (allResults.Count <= _keepRecentToolResults) return;

    foreach (var (mi, bi, block) in allResults.Take(allResults.Count - _keepRecentToolResults))
    {
        if ((block.Content?.Length ?? 0) > 120)
        {
            var replaced = new ToolResultBlock(
                block.ToolUseId,
                "[Earlier tool result compacted. Re-run if needed.]",
                block.IsError);
            messages[mi].Content[bi] = replaced;
        }
    }
}
```

古い結果はクリーンアップされたが、1 件の新しい結果だけで 500KB の可能性がある。大きなファイルを `cat` するだけでコンテキストがいっぱいになる。→ L3。

### L3: tool_result_budget — 大きな結果をディスクに退避

![大きな結果のディスク退避](images/layer1-budget.ja.svg)

モデルが一度に 5 つの大きなファイルを読み、1 つの user メッセージ内の全 `tool_result` の合計が 500KB に達した。

最後の user メッセージ内のすべての `tool_result` の合計サイズを集計。200KB を超えた場合 → サイズ順にソートし、最大のものから順に `.task_outputs/tool-results/` に退避。コンテキストには `<persisted-output>` マーカー + 先頭 2000 文字のプレビューのみを残す。モデルはマーカーを見て完全な内容がディスク上にあることを認識し、必要に応じて再読み込みできる。

```csharp
private void ToolResultBudget(List<Message> messages)
{
    if (messages.Count == 0) return;
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
        var content = block.Content ?? "";
        if (content.Length <= _persistThreshold) continue;
        var tid = string.IsNullOrEmpty(block.ToolUseId) ? Guid.NewGuid().ToString("n") : block.ToolUseId;
        var persisted = PersistLargeOutput(tid, content);
        var idx = last.Content.IndexOf(block);
        if (idx >= 0)
        {
            last.Content[idx] = new ToolResultBlock(block.ToolUseId, persisted, block.IsError);
            total = last.Content.OfType<ToolResultBlock>().Sum(b => b.Content?.Length ?? 0);
        }
    }
}
```

最初の 3 層はすべて純粋なテキスト/構造操作（0 API 呼び出し）だが、会話内容を「理解」することはできない。コンテキストがまだ大きすぎる可能性がある。→ L4。

### L4: compact_history — LLM 全量要約

![LLM 全量要約](images/auto-compact.ja.svg)

最初の 3 層がすべて実行されたが、超大規模プロジェクトで 30 分間連続作業すると、token がまだ閾値を超えている。

3 ステップのフロー：

1. **transcript を保存**：完全な会話を `.transcripts/` に JSONL 形式で書き出す。transcript は回復可能な記録として保存されるが、モデルのアクティブなコンテキストには要約しか残らない。モデルの現在の推論にとって、詳細はすでにコンテキストにない。教学コードは transcript 検索ツールを提供しない。
2. **LLM で要約を生成**：会話履歴を LLM に送り、現在の目標、重要な発見、変更済みファイル、残りの作業、ユーザーの制約などの重要な情報を保持するよう指示。
3. **メッセージリストを置換**：すべての古いメッセージが 1 件の要約に置き換えられる。教学版は要約のみを保持する。実際の Claude Code は compact 後に直近のファイル、計画、agent/skill/tool などのコンテキストを再付加する。

```csharp
public async Task<List<Message>> CompactHistoryAsync(List<Message> messages, CancellationToken ct = default)
{
    var transcriptPath = WriteTranscript(messages);  // Save full conversation first
    var summary = await SummarizeHistoryAsync(messages, ct);  // LLM generates summary
    return new List<Message> { Message.UserText($"[Compacted]\n\n{summary}") };
}
```

**サーキットブレーカー**：連続 3 回失敗したらリトライを停止し、無限ループによる API 呼び出しの浪費を防止。

### 緊急: reactive_compact

API がまだ `prompt_too_long`（413）を返すことがある。コンテキストの増加速度が圧縮のトリガー速度を上回る場合。

この時 **reactive_compact** がトリガーされる：compact_history よりもさらに積極的だが、末尾を残す際も孤立した `tool_result` を残さないようにする。

```csharp
public async Task<List<Message>> EmergencyAsync(List<Message> messages, CancellationToken ct = default)
{
    var transcriptPath = WriteTranscript(messages);
    var summary = await SummarizeHistoryAsync(messages, ct);

    var tailStart = Math.Max(0, messages.Count - 5);
    if (tailStart > 0 && tailStart < messages.Count
        && IsToolResultMessage(messages[tailStart])
        && MessageHasToolUse(messages[tailStart - 1]))
    {
        tailStart -= 1;
    }
    return new List<Message> { Message.UserText($"[Reactive compact]\n\n{summary}") }
        .Concat(messages.Skip(tailStart))
        .ToList();
}
```

reactive compact にはリトライ上限がある（デフォルト 1 回）。さらに失敗した場合は例外をスローし、無限ループしない。完全なエラー回復ロジックは s11 に委ねる。

### 合わせて実行

```csharp
public async Task<LlmResponse> RunAsync(List<Message> messages, CancellationToken ct = default)
{
    // L1, L2, L3: 0 API calls
    Hooks.FireBeforeLlmCall(messages);
    Compactor.PrepareBeforeLlm(messages);

    var systemPrompt = SystemPromptProvider?.Invoke() ?? "";
    LlmResponse response;
    try
    {
        response = await Client.CreateMessageAsync(
            systemPrompt, messages, Tools.AllSpecs().ToList(), ct: ct);
    }
    catch (InvalidOperationException ex) when (IsPromptTooLong(ex))
    {
        // emergency
        messages.Clear();
        messages.AddRange(await Compactor.EmergencyAsync(messages, ct));
        response = await Client.CreateMessageAsync(
            systemPrompt, messages, Tools.AllSpecs().ToList(), ct: ct);
        // retry limit exceeded, raise exception
    }

    messages.Add(Message.Assistant(response.Content));

    if (response.StopReason != "tool_use") return response;

    // tool execution
    var results = new List<ToolResultBlock>();
    foreach (var block in response.Content.OfType<ToolUseBlock>())
    {
        OnLog?.Invoke($"\u001b[33m> {block.Name}\u001b[0m");
        var blocked = Hooks.FirePreToolUse(block);
        var output = blocked ?? Tools.Invoke(block.Name, block.Input);
        Hooks.FirePostToolUse(block, output);
        OnLog?.Invoke(output.Length > 200 ? output[..200] : output);
        results.Add(new ToolResultBlock(block.Id, output));
    }

    messages.Add(Message.UserToolResults(results));
    return response;
}
```

**順序は変えられない。** L3（budget）が L2（micro）の前に実行される理由：micro は古い大きな tool_result を 1 行のプレースホルダに置換するため、budget はその前に完全な内容を退避させる必要がある。CC ソースが `applyToolResultBudget` を最初に配置する理由も同じ。

---

## s07 からの変更点

| コンポーネント | 変更前 (s07) | 変更後 (s08) |
|------|-----------|-----------|
| コンテキスト管理 | なし（コンテキストが無限に膨張） | 4 層圧縮パイプライン + 緊急対応 |
| 新規関数 | — | snip_compact, micro_compact, tool_result_budget, compact_history, reactive_compact |
| ツール | bash, read_file, write_file, edit_file, glob, todo_write, task, load_skill (8) | 8 + compact (9) |
| ループ | LLM 呼び出し → ツール実行 | 各ラウンド前に 3 層プリプロセッサを実行 + 閾値で compact_history をトリガー |
| 設計原則 | — | 安価なものを先に、高価なものを後に |

---

## 試してみよう

```sh
cd learn-claude-code
dotnet run --project s08_context_compact
```

以下のプロンプトを試してみてください：

1. `Read the file README.md, then read Program.cs, then read s01_agent_loop/README.md`（連続して複数のファイルを読み、L2 の古い結果圧縮を観察）
2. `Read every file in s08_context_compact/`（一度に大量の内容を読み込み、L3 のディスク退避を観察）
3. 20+ ラウンドの対話を繰り返し、`[auto compact]` または `[reactive compact]` が表示されるか観察

観察のポイント：ツール実行のたびに、古い tool_result は圧縮されているか？連続対話で token が閾値を超えたとき、要約が自動的にトリガーされたか？

---

## 次へ

コンテキスト圧縮により、Agent は長時間クラッシュせずに動けるようになった。しかし、圧縮のたびにユーザーが以前に伝えた偏好や制約も一緒に失われてしまう。Agent が重要なことを選択的に記憶できるようにできないか？

s09 Memory → 3 つのサブシステム：何を記憶するかの選択、重要情報の抽出、整理と統合。圧縮を越え、セッションを越えて。

<details>
<summary>CC ソースコードの詳細</summary>

> 以下は CC ソースコード `compact.ts`、`autoCompact.ts`、`microCompact.ts`、`query.ts` の分析に基づく。

### 実行順序の対応

教学版は説明の便宜上 L1/L2/L3/L4 と番号を振っているが、実際の実行順序は番号と完全には一致しない：

| 項目 | 教学版 | Claude Code |
|------|--------|-------------|
| 実行順序 | budget → snip → micro → auto | budget → snip → micro → collapse → auto（`query.ts:379-468`） |
| snip_compact | 先頭 3 + 末尾 47 を保持 | CC はメインスレッドのみ有効；実装はオープンソースリポジトリにない（`HISTORY_SNIP` feature gate）、インターフェースは確認可能：`snipCompactIfNeeded(messages)` → `{ messages, tokensFreed, boundaryMessage? }`、`SnipTool` もモデルが能動的に呼び出し可能。教学版の 3/47 は簡略パラメータ |
| micro_compact | テキストプレースホルダで置換 | 2 つのパス：time-based は直接内容をクリア、cached は API の `cache_edits` を使用（legacy パスは削除済み） |
| micro_compact ホワイトリスト | 位置による（直近 3 件） | time-based は時間閾値でトリガー、cached はカウントでトリガー（`microCompact.ts`） |
| tool_result_budget | 200KB 文字 | 200,000 文字（`toolLimits.ts:49`） |
| compact_history 閾値 | 文字数で推定 | 精密な token 数：`contextWindow - maxOutputTokens - 13_000` |
| 要約の要求 | 5 種類の情報 | 9 つのセクション + `<analysis>`/`<summary>` デュアルタグ |
| 圧縮プロンプト | シンプルなプロンプト | 先頭と末尾に二重の安全ガードでツール呼び出しを禁止 |
| PTL retry | あり（簡略版） | `truncateHeadForPTLRetry()` がメッセージグループ単位でロールバック（`compact.ts:243-290`） |
| 圧縮後のリカバリ | なし（教学版は要約のみ保持） | 直近のファイル、計画、agent/skill/tool などの自動再付加 |
| サーキットブレーカー | 3 回 | 3 回（`autoCompact.ts:70`） |
| reactive リトライ | 1 回 | CC にはより精緻な段階別リトライがある |

### 実行順序の詳細

CC ソース `query.ts` での実際の順序：

1. `applyToolResultBudget`（L379）：まず大きな結果を処理し、完全な内容を退避
2. `snipCompact`（L403）：中間メッセージを切り捨て
3. `microcompact`（L414）：古い結果のプレースホルダ化
4. `contextCollapse`（L441）：独立したコンテキスト管理システム（教学版にはなし）
5. `autoCompact`（L454）：LLM 全量要約

教学版の budget → snip → micro の順序はこれと一致する。教学版には contextCollapse メカニズムがない。

### read_file のトレードオフ

教学版の `micro_compact` は、古い `tool_result` を一律にプレースホルダへ置き換える。`read_file` も例外ではない。これは通常、機能的な正しさには影響しない。後でファイル内容が必要になれば、モデルはもう一度そのファイルを読めばよい。代償は、追加のツール呼び出しが発生し得ることと、prompt cache のヒット率が下がり得ること。

Claude Code は、この問題を教学版のような単純なルールでは処理していない。`Read` も microcompact 可能なツール集合に入れる一方で、別途 `readFileState` を維持している。変更されていないファイルの再読込では `FILE_UNCHANGED_STUB` を返し、compact 後には予算内で直近に読んだファイル内容を復元する（例：最大 5 ファイル、1 ファイル 5K token、合計 50K token）。これは本番実装向けのキャッシュと復元メカニズムである。教学版ではそこまで展開せず、「古い結果を圧縮し、必要なら再読込する」という単純な trade-off を残している。

### 完全な定数リファレンス

| 定数 | 値 | ソースファイル |
|------|-----|--------|
| `AUTOCOMPACT_BUFFER_TOKENS` | 13,000 | `autoCompact.ts:62` |
| `MAX_CONSECUTIVE_AUTOCOMPACT_FAILURES` | 3 | `autoCompact.ts:70` |
| `MAX_OUTPUT_TOKENS_FOR_SUMMARY` | 20,000 | `autoCompact.ts:30` |
| `POST_COMPACT_TOKEN_BUDGET` | 50,000 | `compact.ts:123` |
| `POST_COMPACT_MAX_FILES_TO_RESTORE` | 5 | `compact.ts:122` |
| `POST_COMPACT_MAX_TOKENS_PER_FILE` | 5,000 | `compact.ts:124` |
| 時間ベース micro_compact 間隔 | 60 分 | `timeBasedMCConfig.ts` |
| `MAX_COMPACT_STREAMING_RETRIES` | 2 | `compact.ts:131` |

### contextCollapse と sessionMemoryCompact

CC ソースコードには、この教学版では展開していない 2 つのメカニズムが存在する：

- **contextCollapse**：独立したコンテキスト管理システム。有効時には proactive autocompact を抑制し（`autoCompact.ts:215-222`）、collapse の commit/blocking フローがコンテキスト管理を引き継ぐ。ただし manual `/compact` と reactive fallback は独立パスのままで、contextCollapse の影響を受けない。
- **sessionMemoryCompact**：compact_history の前に、CC は既存の session memory（s09 で解説）を使った軽量要約を先に試みる。LLM を呼び出さない。このメカニズムは s09 を学んだ後に振り返るとより理解しやすい。

### 圧縮プロンプトの中身

CC の圧縮プロンプトには 2 つの厳格な要件がある：

1. **ツール呼び出しの絶対禁止**：冒頭が `CRITICAL: Respond with TEXT ONLY. Do NOT call any tools.` で、末尾にも再度 REMINDER がある
2. **先に分析してから要約**：モデルはまず `<analysis>` タグで思考を整理し、その後 `<summary>` タグで正式な要約を出力する。analysis はフォーマット時に除去される

### 教学版の簡略化は意図的

- micro_compact でテキストプレースホルダを使用 → API 層の `cache_edits` 権限がないため
- read_file は特別扱いしない → 教学版では必要時の再読込を受け入れ、readFileState と圧縮後復元の仕組みを導入しない
- token を文字数で推定 → 精密な tokenizer は教学の対象外
- 圧縮後のリカバリを省略 → 教学版は要約のみを保持し、ファイルの自動再付加を行わない
- 2 つの補助メカニズムを展開しない → 10% の細部に属する

コア設計思想、安価なものを先に高価なものを後に、は完全に保持されている。

</details>

<!-- translation-sync: zh@v2, en@v2, ja@v2 -->
