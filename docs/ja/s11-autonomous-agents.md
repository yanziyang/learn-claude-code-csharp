# s11: Autonomous Agents

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > [ s11 ] s12`

> *"チームメイトが自らボードを見て、仕事を取る"* -- リーダーが逐一割り振る必要はない。
>
> **Harness 層**: 自律 -- 指示なしで仕事を見つけるモデル。

## 問題

s09-s10では、チームメイトは明示的に指示された時のみ作業する。リーダーは各チームメイトを特定のプロンプトで spawn しなければならない。タスクボードに未割り当てのタスクが10個あっても、リーダーが手動で各タスクを割り当てる。これはスケールしない。

真の自律性とは、チームメイトが自分で作業を見つけること: タスクボードをスキャンし、未確保のタスクを確保し、作業し、完了したら次を探す。

もう1つの問題: コンテキスト圧縮(s06)後にエージェントが自分の正体を忘れる可能性がある。アイデンティティ再注入がこれを解決する。

## 解決策

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

## 仕組み

1. チームメイトのループは WORK と IDLE の2フェーズ。LLM がツール呼び出しを止めた時(または `idle` ツールを呼んだ時)、IDLE フェーズに入る。

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

2. IDLE フェーズがインボックスとタスクボードをポーリングする。

```csharp
async Task<bool> IdlePollAsync(List<Message> messages, CancellationToken ct)
{
    for (var i = 0; i < IdleTimeoutSeconds / PollIntervalSeconds; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct);

        // 1. インボックス?
        var inbox = bus.ReadInbox(_name);
        if (inbox.Count > 0)
        {
            messages.Add(Message.UserText(
                "<inbox>\n" + JsonSerializer.Serialize(inbox, new JsonSerializerOptions { WriteIndented = true })
                + "\n</inbox>"));
            return true;
        }

        // 2. 未確保タスク?
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

3. タスクボードスキャン: pending かつ未割り当てかつブロックされていないタスクを探す。

```csharp
var candidate = _store.List().FirstOrDefault(t =>
    t.Status == "pending"
    && string.IsNullOrEmpty(t.Owner)
    && _store.CanStart(t));
```

4. アイデンティティ再注入: コンテキストが短すぎる(圧縮が起きた)場合にアイデンティティブロックを挿入する。

```csharp
if (messages.Count <= 3)
{
    messages.Insert(0, Message.UserText(
        $"<identity>You are '{_name}', role: {_role}, " +
        $"team: {_teamName}. Continue your work.</identity>"));
    messages.Insert(1, Message.AssistantText($"I am {_name}. Continuing."));
}
```

## s10からの変更点

| Component      | Before (s10)     | After (s11)                |
|----------------|------------------|----------------------------|
| Tools          | 12               | 14 (+idle, +claim_task)    |
| Autonomy       | Lead-directed    | Self-organizing            |
| Idle phase     | None             | Poll inbox + task board    |
| Task claiming  | Manual only      | Auto-claim unclaimed tasks |
| Identity       | System prompt    | + re-injection after compact|
| Timeout        | None             | 60s idle -> auto shutdown  |

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s17_autonomous_agents
```

1. `Create 3 tasks on the board, then spawn alice and bob. Watch them auto-claim.`
2. `Spawn a coder teammate and let it find work from the task board itself`
3. `Create tasks with dependencies. Watch teammates respect the blocked order.`
4. `/tasks`と入力してオーナー付きのタスクボードを確認する
5. `/team`と入力して誰が作業中でアイドルかを監視する
