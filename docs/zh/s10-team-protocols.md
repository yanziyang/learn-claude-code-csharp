# s10: Team Protocols (团队协议)

`s01 > s02 > s03 > s04 > s05 > s06 | s07 > s08 > s09 > [ s10 ] s11 > s12`

> *"队友之间要有统一的沟通规矩"* -- 一个 request-response 模式驱动所有协商。
>
> **Harness 层**: 协议 -- 模型之间的结构化握手。

## 问题

s09 中队友能干活能通信, 但缺少结构化协调:

**关机**: 直接杀线程会留下写了一半的文件和过期的 bus 状态。需要握手 -- 领导请求, 队友批准 (收尾退出) 或拒绝 (继续干)。

**计划审批**: 领导说 "重构认证模块", 队友立刻开干。高风险变更应该先过审。

两者结构一样: 一方发带唯一 ID 的请求, 另一方引用同一 ID 响应。

## 解决方案

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

## 工作原理

1. 领导生成 `request_id`, 通过收件箱发起关机请求。

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

2. 队友收到请求后, 用 approve/reject 响应。

```csharp
// 队友的工具处理器内:
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

3. 计划审批遵循完全相同的模式。队友提交计划(生成 `request_id`), 领导审查(引用同一个 `request_id`)。

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

一个 FSM, 两种用途。同样的 `pending -> approved | rejected` 状态机可以套用到任何请求-响应协议上。

## 相对 s09 的变更

| 组件           | 之前 (s09)       | 之后 (s10)                           |
|----------------|------------------|--------------------------------------|
| Tools          | 9                | 12 (+shutdown_req/resp +plan)        |
| 关机           | 仅自然退出       | 请求-响应握手                        |
| 计划门控       | 无               | 提交/审查与审批                      |
| 关联           | 无               | 每个请求一个 request_id              |
| FSM            | 无               | pending -> approved/rejected         |

## 试一试

```sh
cd learn-claude-code-csharp
dotnet run --project s16_team_protocols
```

试试这些 prompt (英文 prompt 对 LLM 效果更好, 也可以用中文):

1. `Spawn alice as a coder. Then request her shutdown.`
2. `List teammates to see alice's status after shutdown approval`
3. `Spawn bob with a risky refactoring task. Review and reject his plan.`
4. `Spawn charlie, have him submit a plan, then approve it.`
5. 输入 `/team` 监控状态
