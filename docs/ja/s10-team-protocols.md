# s10: Team Protocols

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > s09 > [ s10 ] s11 > s12`

> *"チームメイト間には統一の通信ルールが必要"* -- 1つの request-response パターンが全交渉を駆動。
>
> **Harness 層**: プロトコル -- モデル間の構造化されたハンドシェイク。

## 問題

s09ではチームメイトが作業し通信するが、構造化された協調がない:

**シャットダウン**: スレッドを強制終了するとファイルが中途半端に書かれ、busの状態が不正になる。ハンドシェイクが必要 -- リーダーが要求し、チームメイトが承認(完了して退出)か拒否(作業継続)する。

**プラン承認**: リーダーが「認証モジュールをリファクタリングして」と言うと、チームメイトは即座に開始する。リスクの高い変更では、実行前にリーダーが計画をレビューすべきだ。

両方とも同じ構造: 一方がユニークIDを持つリクエストを送り、他方がそのIDで応答する。

## 解決策

```
Shutdown Protocol            Plan Approval Protocol
==================           ======================

Lead             Teammate    Teammate           Lead
  |                 |           |                 |
  |--shutdown_req-->|           |--plan_req------>|
  | {req_id:"abc"}  |           | {req_id:"xyz"}  |
  |                 |           |                 |
  |<--shutdown_resp-|           |<--plan_resp-----|
  | {req_id:"abc",  |           | {req_id:"xyz",  |
  |  approve:true}  |           |  approve:true}  |

Shared FSM:
  [pending] --approve--> [approved]
  [pending] --reject---> [rejected]

Trackers:
  shutdown_requests = {req_id: {target, status}}
  plan_requests     = {req_id: {from, plan, status}}
```

## 仕組み

1. リーダーが `request_id` を生成し、インボックス経由でシャットダウンを開始する。

```csharp
var shutdownRequests = new ConcurrentDictionary<string, ShutdownRequest>();

void HandleShutdownRequest(string teammate)
{
    var reqId = $"shr_{Guid.NewGuid():N}"[..11];
    shutdownRequests[reqId] = new ShutdownRequest(teammate, "pending");
    bus.Send("lead", teammate, "Please shut down gracefully.",
             "shutdown_request", new Dictionary<string, object>
             {
                 ["request_id"] = reqId,
             });
}
```

2. チームメイトがリクエストを受信し、承認または拒否で応答する。

```csharp
// チームメイトのツールハンドラ内:
if (toolName == "shutdown_response")
{
    var reqId = args["request_id"];
    var approve = (bool)args["approve"];
    shutdownRequests[reqId].Status = approve ? "approved" : "rejected";
    bus.Send(teammateName, "lead", args.GetValueOrDefault("reason", ""),
             "shutdown_response", new Dictionary<string, object>
             {
                 ["request_id"] = reqId,
                 ["approve"] = approve,
             });
}
```

3. プラン承認も同一パターン。チームメイトがプランを提出(`request_id` を生成)、リーダーがレビュー(同じ `request_id` を参照)。

```csharp
void HandlePlanReview(string requestId, bool approve, string feedback = "")
{
    var req = planRequests[requestId];
    req.Status = approve ? "approved" : "rejected";
    bus.Send("lead", req.From, feedback,
             "plan_approval_response", new Dictionary<string, object>
             {
                 ["request_id"] = requestId,
                 ["approve"] = approve,
             });
}
```

1つのFSM、2つの応用。同じ `pending -> approved | rejected` 状態機械が、あらゆるリクエスト-レスポンスプロトコルに適用できる。

## s09からの変更点

| Component      | Before (s09)     | After (s10)                  |
|----------------|------------------|------------------------------|
| Tools          | 9                | 12 (+shutdown_req/resp +plan)|
| Shutdown       | Natural exit only| Request-response handshake   |
| Plan gating    | None             | Submit/review with approval  |
| Correlation    | None             | request_id per request       |
| FSM            | None             | pending -> approved/rejected |

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s16_team_protocols
```

1. `Spawn alice as a coder. Then request her shutdown.`
2. `List teammates to see alice's status after shutdown approval`
3. `Spawn bob with a risky refactoring task. Review and reject his plan.`
4. `Spawn charlie, have him submit a plan, then approve it.`
5. `/team`と入力してステータスを監視する
