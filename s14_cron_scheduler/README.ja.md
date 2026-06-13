# s14: Cron Scheduler — スケジュールに従って作業を生産

[中文](README.md) · [English](README.en.md) · [日本語](README.ja.md)

s01 → ... → s12 → s13 → `s14` → [s15](../s15_agent_teams/) → s16 → ... → s20
> *"スケジュールに従って作業を生産、スケジューリングと実行を分離"* — cron スケジューリング、永続またはセッションレベル。
>
> **Harness 層**: スケジューリング — 独立スレッドが時刻を判定、キューがトリガーを配信。

---

## 課題

目覚まし時計はあなたが見ていないと鳴らないわけではない。7:00 にセットすれば、7:00 に鳴る。寝ていても、シャワーを浴びていても、料理をしていても、鳴る。

s13 で Agent は遅い操作をバックグラウンドで実行できるようになった。しかし、すべての操作は手動でトリガーされる。一言言えば、Agent が動く。「毎朝 9 時にテストを実行」「30 分ごとに CI ステータスを確認」、これらの定期的なタスクに人が毎回押す必要はないはずだ。

---

## ソリューション

![Cron Scheduler Overview](images/cron-scheduler-overview.ja.svg)

教学版は S13 の簡易タスクシステム、バックグラウンド実行、プロンプト組み立てを踏襲。スケジューラに集中するため、完全なエラーリカバリ、メモリ、スキルシステムは省略。追加：独立した cron スケジューラスレッド、1 秒ごとにポーリング、時間が来たらタスクを `cron_queue` に投入し、queue processor が Agent のアイドル時に自動配信。

手動 vs スケジュール：

| | 手動 (s13) | スケジュール (s14) |
|---|---|---|
| トリガー | ユーザー入力 | スケジューラスレッド |
| トリガー時刻 | いつでも | cron 式で指定 |
| 人の関与 | あり | なし（スケジューラが自動キュー投入、アイドル時に自動配信） |
| 永続性 | — | durable は再起動後も保持 |

---

## 仕組み

### 4 層モデル

cron スケジューリングは 4 層に分かれる：

1. **Scheduler**：daemon スレッド、1 秒ごとにポーリング、時刻が来たか判定
2. **Queue**：`cron_queue`、スケジューラが発火済みタスクを書き込み
3. **Queue Processor**：キューが空でなく Agent がアイドルなら、一回の agent_loop を開始
4. **Consumer**：agent_loop がキューから消費、messages に注入

教学版は最小の queue processor を実装する。`agent_lock` で Agent がアイドルかを判定し、キューに入った cron 作業を自動配信する。実際の CC の `useQueueProcessor.ts` はさらに UI ブロック、キュープライオリティ、メッセージモードを扱う。

### CronJob: データ構造

各 cron タスクは `CronJob` オブジェクト：

```csharp
public sealed class CronJob
{
    public string Id { get; set; } = "";
    public string Cron { get; set; } = "";       // "0 9 * * *" (5-field cron expression)
    public string Prompt { get; set; } = "";     // Message injected to the agent when fired
    public bool Recurring { get; set; } = true;  // True=recurring, False=one-shot
    public bool Durable { get; set; } = true;    // True=write to disk, survives sessions
}
```

cron 式、5 フィールド、Unix で 50 年使われている：

```
分  時  日  月  曜日
 *   *   *   *   *      毎分
 0   9   *   *   *      毎日 9:00
*/5   *   *   *   *      5 分ごと
 0   9   *   *  1-5     平日 9:00
```

`*`、`*/N`、`N`、`N-M`、`N,M,...` をサポート。

### cron_matches: 5 フィールドマッチング

標準 cron セマンティクス：分、時、月はすべてマッチ必須。日（DOM）と曜日（DOW）が両方制約されている場合は、いずれかのマッチで十分（OR）：

```csharp
public static bool CronMatches(string cronExpr, DateTime dt)
{
    var fields = cronExpr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (fields.Length != 5) return false;
    var minute = fields[0];
    var hour = fields[1];
    var dom = fields[2];
    var month = fields[3];
    var dow = fields[4];
    var dowVal = (dt.DayOfWeek == DayOfWeek.Sunday) ? 0 : (int)dt.DayOfWeek;

    if (!FieldMatches(minute, dt.Minute, 0, 59)) return false;
    if (!FieldMatches(hour, dt.Hour, 0, 23)) return false;
    if (!FieldMatches(month, dt.Month, 1, 12)) return false;
    var domOk = FieldMatches(dom, dt.Day, 1, 31);
    var dowOk = FieldMatches(dow, dowVal, 0, 6);

    // DOM and DOW: both constrained → either matching is enough (OR)
    var domFree = dom == "*";
    var dowFree = dow == "*";
    if (domFree && dowFree) return true;
    if (domFree) return dowOk;
    if (dowFree) return domOk;
    return domOk || dowOk;
}
```

### 独立スケジューラスレッド：1 秒ポーリング

スケジューラは独立した daemon スレッドで動作、agent_loop が実行中かどうかに依存しない。個々のジョブエラーはスレッド全体を殺さない：

```csharp
void Loop()
{
    while (!_cts.IsCancellationRequested)
    {
        Thread.Sleep(1000);
        var now = DateTime.Now;
        var marker = now.ToString("yyyy-MM-dd HH:mm");
        foreach (var job in _jobs.Values.ToList())
        {
            try
            {
                if (!CronMatches(job.Cron, now)) continue;
                if (_lastFired.TryGetValue(job.Id, out var prev) && prev == marker) continue;
                _queue.Enqueue(job);
                _lastFired[job.Id] = marker;
                if (!job.Recurring)
                {
                    _jobs.TryRemove(job.Id, out _);
                    SaveDurable();
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[cron error] {job.Id}: {ex.Message}");
            }
        }
    }
}
```

重要な設計：
- **agent_loop から独立**：agent_loop が動いていなくても、スケジューラはバックグラウンドで時刻をチェック
- **日付認識 minute_marker**：`"YYYY-MM-DD HH:MM"` を使用、同じ分の重複発火を防ぎつつ翌日のスキップも防止
- **ジョブ単位の try/except**：一つの悪いジョブがスケジューラスレッド全体をクラッシュさせない
- **一回限りジョブ**：発火後、scheduled_jobs から自動削除

### Queue Processor + agent_loop: 配信側

queue processor は時刻をチェックしない。キューに作業があり、Agent がアイドルの時だけ一回の実行を開始する：

```csharp
void QueueProcessorLoop()
{
    while (true)
    {
        Thread.Sleep(200);
        if (!HasCronQueue()) continue;
        if (!agentLock.WaitOne(0)) continue;
        try
        {
            if (HasCronQueue()) RunAgentTurnLocked();
        }
        finally
        {
            agentLock.Release();
        }
    }
}
```

agent_loop も時刻をチェックしない。`cron_queue` から発火済みタスクを取り出し、messages に注入するだけ：

```csharp
var fired = cron.DrainQueue();
foreach (var j in fired)
    messages.Add(Message.UserText($"<cron-fire id=\"{j.Id}\">{j.Prompt}</cron-fire>"));
```

生産者（スケジューラスレッド）、配信者（queue processor）、消費者（agent_loop）は `cron_queue`、`cron_lock`、`agent_lock` で分離されている。

### バリデーション：不正 cron がスケジューラを殺すのを防止

`schedule_job` は登録前に cron 式をバリデーションし、不正な場合はエラーを返す：

```csharp
public string Schedule(string cron, string prompt, bool recurring = true, bool durable = true)
{
    var err = ValidateCron(cron);
    if (err is not null) return $"Error: {err}";
    // ... register job
}
```

ディスクから durable ジョブを読み込む際も不正な式をスキップし、一つの悪いタスクが起動を妨げない。

### Durable vs Session-only

- **Durable**：タスク定義を `.scheduled_tasks.json` に書き込み。Agent 再起動後にファイルから復元。
- **Session-only**：メモリ内のみ。Agent 終了で消失。

> **重要な前提**：cron スケジューラは Agent プロセス内で実行される必要がある。プロセスが終了するとスケジューラも停止。Durable はタスク定義が再起動後も保持されることを意味するだけで、次回 Agent 起動時にスケジューラが「発火すべき」と判定して初めて発火する。「アプリケーションが閉じていても定期的に実行」が必要な場合は、システム crontab または systemd timer を使用。

### 組み合わせて実行

```
1. 起動時：
   load_durable_jobs() → .scheduled_tasks.json から永続タスクを復元
   Thread(cron_scheduler_loop, daemon=True).start() → スケジューラスレッドがポーリング開始
   Thread(queue_processor_loop, daemon=True).start() → processor が配信待機

2. タスク登録：
   schedule_cron(cron="*/2 * * * *", prompt="run date", durable=True)
   → CronJob を scheduled_jobs + .scheduled_tasks.json に書き込み

3. 2 分ごと：
   スケジューラチェック → cron_matches が True → cron_queue.append(job)
   → queue processor がアイドル状態を検知 → agent_loop consume_cron_queue
   → "[Scheduled] run date" を注入
   → LLM がメッセージを受信、date コマンドを実行

4. プロセス終了：
   スケジューラスレッドも停止（daemon=True）
   .scheduled_tasks.json はディスクに残存
   次回起動 → load_durable_jobs → タスク復元
```

---

## s13 からの変更

| コンポーネント | 変更前 (s13) | 変更後 (s14) |
|--------------|------------|------------|
| トリガー方式 | ユーザー手動トリガー | スケジューラスレッドが自動キュー投入 |
| 新規型 | — | CronJob データクラス (id, cron, prompt, recurring, durable) |
| 新規関数 | — | cron_matches, validate_cron, schedule_job, cancel_job, cron_scheduler_loop, queue_processor_loop |
| 新規ストレージ | — | .scheduled_tasks.json (durable) + メモリ (session-only) |
| スレッド | バックグラウンド実行スレッド | + スケジューラスレッド (daemon, 1s ポーリング) + queue processor スレッド |
| キュー | background_results | + cron_queue（スケジューラ書き込み、queue processor 配信、agent_loop 消費） |
| ツール | 8 (s12/s13) | + schedule_cron, list_crons, cancel_cron (11) |

---

## 試してみる

```sh
cd learn-claude-code
dotnet run --project s14_cron_scheduler
```

以下のプロンプトを試してください：

1. `Schedule a task to print the current date every 2 minutes`
2. `List all cron jobs`
3. `Create a one-shot reminder in 1 minute to check the build status`
4. `Cancel the recurring job and verify with list_crons`

観察ポイント：スケジューラスレッドが独立して動いているか？cron タスクが正しい時刻に発火しているか？新しい prompt を入力しなくても `[queue processor]` が出て自動実行されるか？durable ジョブが `.scheduled_tasks.json` に書き込まれているか？

---

## 次の章

一つの Agent でできることは増えた。計画、圧縮、バックグラウンド、スケジューリング。しかし、一部のタスクは一つの Agent では大きすぎる。

「バックエンド全体をリファクタリング」、認証モジュール、データベース層、API ルート、テストを全面的に刷新。一つの Agent の注意力には限界がある。これにはチームが必要だ。

s15 Agent Teams → 一人の Agent では足りない、チームを組もう。永続的なチームメイト + 非同期受信箱。

<details>
<summary>CC ソースコード深掘り</summary>

> 以下は CC ソースコード `CronCreateTool.ts`、`cronScheduler.ts`、`cron.ts`、`cronTasks.ts`、`cronTasksLock.ts`、`useScheduledTasks.ts`（139 行）の完全分析に基づく。

### 一、3 つの Cron ツール

CC はモデルに 3 つの cron ツールを公開：`CronCreate`、`CronDelete`、`CronList`。すべてコンパイル時ゲート `feature('AGENT_TRIGGERS')` とランタイム GrowthBook フラグ `tengu_kairos_cron` で制御。`CLAUDE_CODE_DISABLE_CRON` 環境変数でローカル上書きも可能。

### 二、ストレージ：`.claude/scheduled_tasks.json`

```json
{ "tasks": [{ "id": "abc12345", "cron": "0 9 * * *", "prompt": "...", "recurring": true, "durable": true, "createdAt": 1714567890000 }] }
```

durable タスクはディスクに書き込み。session-only タスクは `STATE.sessionCronTasks` メモリ配列に格納（プロセス再起動で消失）。`.scheduled_tasks.lock` ファイルで同じプロジェクトの複数セッション間の重複発火を防止。

### 三、スケジューラ：1 秒ポーリング

`cronScheduler.ts` は毎秒チェック（`CHECK_INTERVAL_MS = 1000`）。ロックを保持しているセッションがファイルタスクをトリガー。すべてのセッションが session-only タスクをトリガー。`chokidar` ファイルウォッチャーが `scheduled_tasks.json` の変更を監視。

### 四、cron 式：標準 5 フィールド

分 時 日 月 曜日。`*`、`*/N`、`N`、`N-M`、`N-M/S`、`N,M,...` をサポート。`L`、`W`、`?` は非サポート。すべての時間はローカルタイムゾーンで解釈。day-of-month と day-of-week が両方制約されている場合は OR セマンティクス。

### 五、ジッター（サンダリングハード防止）

- 定期タスク：トリガー遅延は期間の最大 10%（上限 15 分）、タスク ID ベースの決定的ハッシュ
- 一回限りタスク：発火時刻が `:00` または `:30` の場合、最大 90 秒早く発火
- ジッター設定は GrowthBook でリアルタイム調整可能、60 秒ごとにリフレッシュ

### 六、自動期限切れ

定期タスクは 7 日後に自動期限切れ（設定可能、上限 30 日）。期限切れ前に最後の一回を発火、その後自動削除。

### 七、ジョブ数上限

`MAX_JOBS = 50`（`CronCreateTool.ts:25`）。超過時はエラーを返す："Too many scheduled jobs (max 50). Cancel one first."

### 八、トリガー注入

発火後、`enqueuePendingNotification()` で `priority: 'later'` としてコマンドキューにエンキュー。`workload: WORKLOAD_CRON` タグ付き、API は容量が逼迫している時に cron 発信リクエストを低い QoS で処理。

### 九、Queue Processor：自動配信

実際の CC は `useQueueProcessor.ts:48-60` により、アクティブな query がなく、UI がブロックされておらず、キューが空でない場合に自動的に処理をトリガーする。`queueProcessor.ts:52-87` がキュープライオリティに従ってコマンドを `handlePromptSubmit()` にディスパッチ。教学版は `queue_processor_loop` で核心動作を保つ：キューに作業があり Agent がアイドルなら、自動的に一回の agent_loop を開始する。

</details>

<!-- translation-sync: zh@v1, en@v1, ja@v1 -->
