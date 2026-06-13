# s09: Agent Teams

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > [ s09 ] s10 > s11 > s12`

> *"一人で終わらないなら、チームメイトに任せる"* -- 永続チームメイト + 非同期メールボックス。
>
> **Harness 層**: チームメールボックス -- 複数モデルをファイルで協調。

## 問題

サブエージェント(s04)は使い捨てだ: 生成し、作業し、要約を返し、消滅する。アイデンティティもなく、呼び出し間の記憶もない。バックグラウンドタスク(s08)はシェルコマンドを実行するが、LLM誘導の意思決定はできない。

本物のチームワークには: (1)単一プロンプトを超えて存続する永続エージェント、(2)アイデンティティとライフサイクル管理、(3)エージェント間の通信チャネルが必要だ。

## 解決策

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

## 仕組み

1. `MessageBus` は `.mailboxes/` 配下にエージェントごとに1つのJSONLファイルを書き出す (`AgentCommon/Teams/MessageBus.cs`)。

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

2. `ReadInbox` はインボックス全体を1回の呼び出しで消費する: 全行を読み、ファイルを削除。

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
        File.Delete(inbox);   // 消費: 読み取り + 削除
    }
    return msgs;
}
```

3. `TeamTools` がリード側に3つのツールを登録する: `spawn_teammate`, `send_message`, `check_inbox`。

```csharp
var bus = new MessageBus(workDir);
TeamTools.Register(tools, bus, client, config, workDir);
// → spawn_teammate: バックグラウンドスレッドで TeammateRunner を起動
// → send_message:  bus.Send("lead", to, content)
// → check_inbox:   bus.ReadInbox("lead") (ドレイン)
```

4. 各 `TeammateRunner` は各LLM呼び出しの前に自身のインボックスを確認し、受信メッセージをコンテキストに注入する。

```csharp
// TeammateRunner フック内 (要約):
var msgs = bus.ReadInbox(name);
if (msgs.Count > 0)
    messages.Add(Message.UserText(
        "<inbox>\n" + JsonSerializer.Serialize(msgs, new JsonSerializerOptions { WriteIndented = true })
        + "\n</inbox>"));
```

## s08からの変更点

| Component      | Before (s08)     | After (s09)                |
|----------------|------------------|----------------------------|
| Tools          | 6                | 9 (+spawn/send/check_inbox)|
| Agents         | Single           | Lead + N teammates         |
| Persistence    | None             | JSONL inboxes under .mailboxes/ |
| Threads        | Background cmds  | Full agent loops per thread|
| Lifecycle      | Fire-and-forget  | idle -> working -> idle    |
| Communication  | None             | message + broadcast        |

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s15_agent_teams
```

1. `Spawn alice (coder) and bob (tester). Have alice send bob a message.`
2. `Broadcast "status update: phase 1 complete" to all teammates`
3. `Check the lead inbox for any messages`
4. `/team`と入力してステータス付きのチーム名簿を確認する
5. `/inbox`と入力してリーダーのインボックスを手動確認する
