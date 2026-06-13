# s09: Agent Teams (Agent 团队)

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > [ s09 ] s10 > s11 > s12`

> *"任务太大一个人干不完, 要能分给队友"* -- 持久化队友 + JSONL 邮箱。
>
> **Harness 层**: 团队邮箱 -- 多个模型, 通过文件协调。

## 问题

Subagent (s04) 是一次性的: 生成、干活、返回摘要、消亡。没有身份, 没有跨调用的记忆。Background Tasks (s08) 能跑 shell 命令, 但做不了 LLM 引导的决策。

真正的团队协作需要三样东西: (1) 能跨多轮对话存活的持久 Agent, (2) 身份和生命周期管理, (3) Agent 之间的通信通道。

## 解决方案

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

## 工作原理

1. `MessageBus` 在 `.mailboxes/` 下为每个 Agent 写一个 JSONL 文件 (`AgentCommon/Teams/MessageBus.cs`)。

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

2. `ReadInbox` 在一次调用中消费整个收件箱: 读取全部行, 删除文件。

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
        File.Delete(inbox);   // 消费: 读 + 删
    }
    return msgs;
}
```

3. `TeamTools` 在 lead 端注册三个工具: `spawn_teammate`, `send_message`, `check_inbox`。

```csharp
var bus = new MessageBus(workDir);
TeamTools.Register(tools, bus, client, config, workDir);
// → spawn_teammate: 后台线程启动 TeammateRunner
// → send_message:  bus.Send("lead", to, content)
// → check_inbox:   bus.ReadInbox("lead") (已排空)
```

4. 每个 `TeammateRunner` 在每次 LLM 调用前检查自己的收件箱, 把消息注入上下文。

```csharp
// TeammateRunner 钩子内 (摘要):
var msgs = bus.ReadInbox(name);
if (msgs.Count > 0)
    messages.Add(Message.UserText(
        "<inbox>\n" + JsonSerializer.Serialize(msgs, new JsonSerializerOptions { WriteIndented = true })
        + "\n</inbox>"));
```

## 相对 s08 的变更

| 组件           | 之前 (s08)       | 之后 (s09)                         |
|----------------|------------------|------------------------------------|
| Tools          | 6                | 9 (+spawn/send/check_inbox)        |
| Agent 数量     | 单一             | 领导 + N 个队友                    |
| 持久化         | 无               | JSONL inboxes under .mailboxes/   |
| 线程           | 后台命令         | 每线程完整 agent loop              |
| 生命周期       | 一次性           | idle -> working -> idle            |
| 通信           | 无               | message + broadcast                |

## 试一试

```sh
cd learn-claude-code-csharp
dotnet run --project s15_agent_teams
```

试试这些 prompt (英文 prompt 对 LLM 效果更好, 也可以用中文):

1. `Spawn alice (coder) and bob (tester). Have alice send bob a message.`
2. `Broadcast "status update: phase 1 complete" to all teammates`
3. `Check the lead inbox for any messages`
4. 输入 `/team` 查看团队名册和状态
5. 输入 `/inbox` 手动检查领导的收件箱
