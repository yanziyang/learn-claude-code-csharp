# s11: Autonomous Agents (Autonomous Agent)

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > [ s11 ] s12`

> *"队友自己看看板, 有活就认领"* -- 不需要领导逐个分配, 自组织。
>
> **Harness 层**: 自治 -- 模型自己找活干, 无需指派。

## 问题

s09-s10 中, 队友只在被明确指派时才动。领导得给每个队友写 prompt, 任务看板上 10 个未认领的任务得手动分配。这扩展不了。

真正的自治: 队友自己扫描任务看板, 认领没人做的任务, 做完再找下一个。

一个细节: Context Compact (s06) 后 Agent 可能忘了自己是谁。身份重注入解决这个问题。

## 解决方案

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

## 工作原理

1. 队友循环分两个阶段: WORK 和 IDLE。LLM 停止调用工具 (或调用了 `idle`) 时, 进入 IDLE。

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

2. 空闲阶段循环轮询收件箱和任务看板。

```csharp
async Task<bool> IdlePollAsync(List<Message> messages, CancellationToken ct)
{
    for (var i = 0; i < IdleTimeoutSeconds / PollIntervalSeconds; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct);

        // 1. 收件箱?
        var inbox = bus.ReadInbox(_name);
        if (inbox.Count > 0)
        {
            messages.Add(Message.UserText(
                "<inbox>\n" + JsonSerializer.Serialize(inbox, new JsonSerializerOptions { WriteIndented = true })
                + "\n</inbox>"));
            return true;
        }

        // 2. 未认领任务?
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

3. 任务看板扫描: 找 pending 状态、无 owner、未被阻塞的任务。

```csharp
var candidate = _store.List().FirstOrDefault(t =>
    t.Status == "pending"
    && string.IsNullOrEmpty(t.Owner)
    && _store.CanStart(t));
```

4. 身份重注入: 上下文过短 (说明发生了压缩) 时, 在开头插入身份块。

```csharp
if (messages.Count <= 3)
{
    messages.Insert(0, Message.UserText(
        $"<identity>You are '{_name}', role: {_role}, " +
        $"team: {_teamName}. Continue your work.</identity>"));
    messages.Insert(1, Message.AssistantText($"I am {_name}. Continuing."));
}
```

## 相对 s10 的变更

| 组件           | 之前 (s10)       | 之后 (s11)                       |
|----------------|------------------|----------------------------------|
| Tools          | 12               | 14 (+idle, +claim_task)          |
| 自治性         | 领导指派         | 自组织                           |
| 空闲阶段       | 无               | 轮询收件箱 + 任务看板            |
| 任务认领       | 仅手动           | 自动认领未分配任务               |
| 身份           | 系统提示         | + 压缩后重注入                   |
| 超时           | 无               | 60 秒空闲 -> 自动关机            |

## 试一试

```sh
cd learn-claude-code-csharp
dotnet run --project s17_autonomous_agents
```

试试这些 prompt (英文 prompt 对 LLM 效果更好, 也可以用中文):

1. `Create 3 tasks on the board, then spawn alice and bob. Watch them auto-claim.`
2. `Spawn a coder teammate and let it find work from the task board itself`
3. `Create tasks with dependencies. Watch teammates respect the blocked order.`
4. 输入 `/tasks` 查看带 owner 的任务看板
5. 输入 `/team` 监控谁在工作、谁在空闲
