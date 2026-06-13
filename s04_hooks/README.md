# s04: Hooks — 挂在循环上，不写进循环里

[中文](README.md) · [English](README.en.md) · [日本語](README.ja.md)

s01 → s02 → s03 → `s04` → [s05](../s05_todo_write/) → s06 → ... → s20

> *"挂在循环上, 不写进循环里"* — hook 在工具执行前后注入扩展逻辑。
>
> **Harness 层**: hook — 扩展点不侵入循环。

---

## 问题

s03 的 Agent 有权限检查了。但每次加一个新检查，比如"记录每次 bash 调用"、"操作后自动 git add"，都要修改 `agent_loop` 函数。

循环很快就变成了这样：

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

你想扩展的是 Agent 的行为，但你改的却是循环本身。循环应该是一个稳定的核心，扩展应该挂在外面。

---

## 解决方案

![Hooks Overview](images/hooks-overview.svg)

s03 的循环和权限逻辑完全保留。唯一的变动是把 `check_permission()` 从循环体内移到了 hook 上，循环不再直接调用任何检查函数，改为 `trigger_hooks("PreToolUse", block)`，由注册表决定跑什么。

四个事件，覆盖一个完整的 agent cycle：

| 事件 | 触发时机 | 典型用途 |
|------|---------|---------|
| UserPromptSubmit | 用户输入提交后、进入 LLM 前 | 输入验证、注入上下文 |
| PreToolUse | 工具执行前 | 权限检查、日志记录 |
| PostToolUse | 工具执行后 | 副作用（自动 git add 等）、输出检查 |
| Stop | 循环即将退出时 | 收尾清理（CC 还支持强制续跑） |

扩展通过 `register_hook()` 添加，循环只调用 `trigger_hooks()`。

---

## 工作原理

**核心抽象**：`HookBus` 是事件→订阅者列表的映射。Agent 循环不直接调用任何业务逻辑——只 fire 事件，由订阅者决定做什么。订阅者分两类：

- **外部命令**：从 `appsettings.json` 的 `hooks` 段读出来，运行时 spawn 进程跑。**教学版只走这条路。**
- **C# 委托**：通过 `OnPreToolUse` / `OnPostToolUse` / `OnStop` / `OnUserPromptSubmit` / `OnBeforeLlmCall` 注册，留在进程内。保留这条 API 是给"需要进程内状态/TTY 交互"的少数场景。

s04 的 `Program.cs` 一行 `OnXxx` 都没有：

```csharp
var n = agent.Hooks.ConfigureExternal(
    config.Hooks, workDir,
    log: msg => Console.Error.WriteLine(msg),
    timeout: TimeSpan.FromSeconds(30));
```

——全部行为从 `appsettings.json` 读，循环代码原封不动。

**四个事件**覆盖完整 agent cycle：

| 事件 | 触发时机 | stdin 载荷 | 典型用途 |
|------|---------|-----------|---------|
| `UserPromptSubmit` | 用户输入后、LLM 调用前 | `{hookEventName, userPrompt}` | 日志、注入上下文 |
| `PreToolUse` | 工具执行前 | `{hookEventName, toolName, toolInput}` | 权限检查、白名单/黑名单 |
| `PostToolUse` | 工具执行后 | `{hookEventName, toolName, toolInput, toolOutput}` | 审计、副作用、输出检查 |
| `Stop` | 循环即将退出 | `{hookEventName, sessionStats:{toolCalls}}` | 收尾、写 session.json |

**协议**（与 Python CC 一致）：

| exit | 含义 |
|------|------|
| `0` | 允许 |
| `2` | 阻止；stderr（或 stdout）作原因 |
| 其它 | 非阻塞错误；bus 打警告，仍然允许 |

可选 stdout JSON 可覆盖 exit code 行为：

```jsonc
{ "decision": "block", "reason": "..." }
```

**Matcher 语法**（每条 group 上）：

| 模式 | 匹配 |
|------|------|
| 留空 / `null` | 所有工具 |
| `"bash"` | 精确 |
| `"bash*"` | 前缀 |
| `"/regex/"` | 正则（`/.../` 包裹） |

**AgentCommon/Agent/AgentHarness.cs 的循环只改了一处**：s03 直接调用 `check_permission(block)`，s04 改为 `await Hooks.FirePreToolUseAsync(block, ct)`。其他 fire 点位是 `BeforeLlmCall`（在 `RunAsync` 开头）、`PostToolUse`（工具执行后）、`FireStopOnHistory`（循环退出前）。外部脚本与内联委托并排运行——第一个返回 block reason 的胜出，stderr 进模型对话。

---

## 相对 s03 的变更

| 组件 | 之前 (s03) | 之后 (s04) |
|------|-----------|-----------|
| 扩展方式 | `check_permission()` 硬编码在循环里 | `HookBus` + `appsettings.json` 里的 `hooks` 段 |
| hook 定义位置 | C# 代码里的内联委托 | JSON 配置指向外部 JS/TS/任意脚本 |
| 加载时机 | 编译期绑定 | 启动时由 `ConfigureExternal` 动态加载 |
| 新类型 | — | `HooksConfig` / `HookGroup` / `HookCommand` / `ExternalHookEntry` / `ExternalHookRunner` / `ExternalHookLoader` |
| 循环 | 直接调用 `check_permission()` | `await Hooks.FirePreToolUseAsync(block, ct)` |
| 退出控制 | 无 | `FireStopOnHistoryAsync` 可阻止退出 |
| 输入拦截 | 无 | `FireUserPromptSubmitAsync` 可注入上下文 |
| 协议 | — | CC 兼容：stdin = 事件 JSON，exit 0/2 决定 allow/block |

---

## 外部 hook 脚本：JS / TS 也能挂上

教学版上面的 hook 全部是 C# 委托（`OnPreToolUse(...)` 等）。CC 的真实 hook 模型不仅如此——它允许在 `settings.json` 里把事件指向一个**外部命令**，命令的 stdin 收到事件 JSON，exit code 决定结果。Python CC 里这些外部命令是任意可执行文件：`.py`、`.sh`、`.ps1`、`.exe`… 主流生态（Cline、Roo、Continue）清一色用 **JavaScript / TypeScript** 写，所以本节把它做成 C# 版的"第一类公民"。

### 配置：在 `appsettings.json` 里挂命令

```jsonc
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "bash",
        "hooks": [ { "type": "command", "command": "node hooks/block-rm.js" } ] }
    ],
    "PostToolUse": [
      { "hooks": [ { "type": "command", "command": "node hooks/audit-log.js" } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "npx tsx hooks/summary.ts" } ] }
    ]
  }
}
```

- `matcher` 留空 = 匹配所有工具；`"bash"` = 精确匹配；`"bash*"` = 前缀匹配；`"/regex/"` = 正则。
- `command` 走**不带 shell** 的 argv 解析，单/双引号分组空白字符。
- 第一个 token 是可执行文件（`node`/`tsx`/`npx`/`py`/…），后面是参数。运行时由用户决定，本项目不内嵌 JS 引擎。

### 协议：stdin / exit code / 可选 stdout

事件以 JSON 形式从 stdin 写入脚本：

| 事件 | 写入 stdin 的 JSON |
|------|------|
| `PreToolUse` | `{ "hookEventName":"PreToolUse", "toolName":"bash", "toolInput":{ "command":"..." } }` |
| `PostToolUse` | `{ "hookEventName":"PostToolUse", "toolName":"bash", "toolInput":{...}, "toolOutput":"..." }` |
| `UserPromptSubmit` | `{ "hookEventName":"UserPromptSubmit", "userPrompt":"..." }` |
| `Stop` | `{ "hookEventName":"Stop", "sessionStats":{ "toolCalls": 5 } }` |

退出码语义（与 Python CC 一致）：

| exit | 含义 |
|------|------|
| `0` | 允许 |
| `2` | 阻止；stderr（或 stdout）作为原因 |
| 其它 | 非阻塞错误；bus 打印警告，仍然允许 |

可选 stdout JSON 可覆盖 exit code 行为：

```jsonc
// 写到 stdout
{ "decision": "block", "reason": "rm -rf / is not allowed" }
```

TS 用户用 `npx tsx hooks/foo.ts` 即可，运行时不挑——`node`/`bun`/`deno`/`tsx`/`ts-node` 都行，配置时换第一个 token 即可。

### 完整示例：阻止 `rm -rf`

`hooks/block-rm.js`：

```javascript
const DENY = [/\brm\s+-rf?\s+\//, /\bsudo\b/, /\bshutdown\b|\breboot\b/, /\bmkfs\b/, /\bdd\s+if=/];

(async () => {
  const event = JSON.parse(await readStdin());
  if (event.toolName !== "bash") process.exit(0);
  const cmd = (event.toolInput && event.toolInput.command) || "";
  for (const pat of DENY) {
    if (pat.test(cmd)) {
      console.error(`denied by ${pat}`);
      process.exit(2);  // 2 = block, stderr becomes the reason
    }
  }
  process.exit(0);
})();
```

`appsettings.json`：

```jsonc
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "bash",
        "hooks": [ { "type": "command", "command": "node hooks/block-rm.js" } ] }
    ]
  }
}
```

s04 的 `Program.cs` 启动时做一件事——把所有外部 hook 一次性挂上：

```csharp
var n = agent.Hooks.ConfigureExternal(
    config.Hooks, workDir,
    log: msg => Console.Error.WriteLine(msg),
    timeout: TimeSpan.FromSeconds(30));
if (n > 0) Console.WriteLine($"[host] loaded {n} external hook(s) from appsettings.json");
```

`ConfigureExternal(config, workDir, log?, timeout?)` 在 `HookBus` 上做三件事：注入 `ExternalRunner`、设置 `WorkDir`、调用 `ExternalHookLoader` 把 `hooks.<Event>[]` 段下的每条 `command` 注册为外部订阅者。`appsettings.json` 没有 `hooks` 段就什么都不做，return 0。启动时调一次即可——之后内联委托和外部脚本并排运行，第一个拒绝的 hook 胜出。

### 不在范围内（教学版简化）

- **内嵌 V8/Jint**：会引入重量级依赖，TS 还得我们内嵌转译器。
- **CSX（Roslyn scripting）**：C# 限定，与"AI 工具生态以 JS/TS 为主"的现实不符。
- **进程级沙箱**：timeout 默认 30s（可配），不做 cgroup/AppContainer 隔离。
- **hook allow vs deny/ask 优先级**：本项目无 settings.json 层，没有 CC `toolHooks.ts:325-331` 的优先级合并逻辑。

---

## 自带的脚本：把示例跑起来

`hooks/` 下放了七个示例脚本，复制 `appsettings.example.json` → `appsettings.json` 即可全部启用。**教学版默认就是这种"全部 hook 来自配置"的形态**——`Program.cs` 一行 `OnXxx` 都没有。

| 脚本 | 事件 | 匹配 | 行为 |
|------|------|------|------|
| `block-rm.js` | PreToolUse | `bash` | 黑名单：阻断 `rm -rf /`、`sudo`、`shutdown`、`reboot`、`mkfs`、`dd if=` |
| `path-guard.js` | PreToolUse | `write_file\|edit_file` | 拒绝写到 cwd 之外的目标（exit 2） |
| `log-pretool.js` | PreToolUse | 全部 | 把 `toolName(key=val, …)` 打到 stderr |
| `log-prompt.js` | UserPromptSubmit | — | 把 `cwd=` 和 prompt 截断 60 字打到 stderr |
| `large-output.js` | PostToolUse | 全部 | 工具输出 > 100_000 字符时打警告 |
| `audit-log.js` | PostToolUse | 全部 | 每个 tool call 追加一行 JSONL 到 `.memory/audit.log` |
| `summary.ts` | Stop | — | 写 `.memory/session.json`（需要 `npx tsx`） |

每个脚本都可独立运行做单元测试：

```sh
echo '{"hookEventName":"PreToolUse","toolName":"bash","toolInput":{"command":"sudo apt update"}}' | node hooks/block-rm.js
# → exit 2, stderr: "block-rm: denied by pattern /\bsudo\b/"
```

要禁用某一类 hook：注释掉 `appsettings.json` 里对应的那一段；要换实现：把 `command` 指向你的脚本。循环代码不用动。

---

## 试一下

```sh
cd learn-claude-code
dotnet run --project s04_hooks
```

试试这些 prompt：

1. `Read the file README.md`（应该直接通过，观察 hook 日志）
2. `Create a file called test.txt`（通过后观察 PostToolUse 是否触发）
3. `Delete all temporary files in /tmp`（bash + rm 触发权限 hook）
4. `Write something to ../../../etc/passwd`（path-guard 拒绝，exit 2）

观察重点：每次工具执行前，是否出现了 `[HOOK]` 日志？权限被拒时，是 `block-rm.js` / `path-guard.js` 拦截的，还是循环里硬编码的？——答案应该是前者：s04 的 `Program.cs` 里没有任何权限相关代码。

---

## 接下来

Agent 现在能安全执行操作了。但它有没有停下来想过"我应该先做什么，再做什么"？给它一个复杂任务，它是一上来就动手，还是先列个计划？

s05 TodoWrite → 给 Agent 一个计划工具。先列清单，再做。

<details>
<summary>深入 CC 源码</summary>

> 以下基于 CC 源码 `toolHooks.ts`（650 行）、`hooks.ts`、`stopHooks.ts`、`coreTypes.ts` 的完整分析。

### 一、Hook 事件：不止这 4 个，而是 27 个

教学版只讲了 PreToolUse 和 PostToolUse。CC 实际有 27 个 hook 事件（`coreTypes.ts:25-53`）：

| 类别 | 事件 |
|------|------|
| 工具相关 | `PreToolUse`, `PostToolUse`, `PostToolUseFailure` |
| 会话相关 | `SessionStart`, `SessionEnd`, `Stop`, `StopFailure`, `Setup` |
| 用户交互 | `UserPromptSubmit`, `Notification`, `PermissionRequest`, `PermissionDenied` |
| 子 Agent | `SubagentStart`, `SubagentStop` |
| 压缩相关 | `PreCompact`, `PostCompact` |
| 团队相关 | `TeammateIdle`, `TaskCreated`, `TaskCompleted` |
| 其他 | `Elicitation`, `ElicitationResult`, `ConfigChange`, `WorktreeCreate`, `WorktreeRemove`, `InstructionsLoaded`, `CwdChanged`, `FileChanged` |

教学版只讲 4 个核心事件（UserPromptSubmit、PreToolUse、PostToolUse、Stop），因为它们覆盖了一个完整 agent cycle 的关键节点。其他 23 个都是同样的模式。

### 二、HookResult 常用字段摘录

CC 的 `HookResult`（`types/hooks.ts:260-275`）有 14 个字段，以下是常用字段：

| 字段 | 类型 | 用途 |
|------|------|------|
| `message` | Message | 可选 UI 消息 |
| `blockingError` | HookBlockingError | 阻塞错误 → 注入对话让模型自纠 |
| `outcome` | success/blocking/non_blocking_error/cancelled | 执行结果 |
| `preventContinuation` | boolean | 阻止后续执行 |
| `stopReason` | string | 停止原因描述 |
| `permissionBehavior` | allow/deny/ask/passthrough | hook 返回权限决策 |
| `updatedInput` | Record | 修改工具输入 |
| `additionalContext` | string | 附加上下文 |
| `updatedMCPToolOutput` | unknown | MCP 工具输出修改 |

### 三、关键不变式：Hook 'allow' 不能绕过 deny/ask 规则

这是 CC 权限系统最重要的安全设计（`toolHooks.ts:325-331`）：**hook 返回 allow 时，仍然要检查 settings.json 的 deny/ask 规则**。即使用户的 hook 脚本说"允许"，如果在 settings.json 中禁用了这个工具，操作仍然会被阻止。

教学版没有这个层次，只把 PreToolUse 的非 None 返回值解释为阻止本次工具执行。这在教学场景中够了，但在生产环境中会形成安全漏洞。

### 四、stopHookActive 机制

CC 的 Stop hooks 有一个防无限循环机制（`query.ts:212,1300`）：`stopHookActive` 状态字段。当 stop hooks 产生 blockingError 时，循环带 `stopHookActive: true` 重入下一轮。后续迭代中 stop hooks 看到这个标志就不会再次触发。这防止了一个永不停机的 bug：模型自纠后 stop hook 再次报错 → 模型再自纠 → stop hook 再报错...

### 五、hook_stopped_continuation

PostToolUse hooks 返回 `preventContinuation: true` 时，会产生一个 `hook_stopped_continuation` 附件（`toolHooks.ts:117-130`）。query.ts（L1388-1393）检测到后设置 `shouldPreventContinuation = true`，循环退出。这是 "hook 优雅地让 Agent 停机" 的机制，不是崩溃，是完成。

### 教学版的简化是刻意的

- 27 个事件 → 4 个（UserPromptSubmit/PreToolUse/PostToolUse/Stop）：覆盖 agent cycle 关键节点
- 14 个字段 → 简单的返回值（None = 继续，非 None = 阻止/续跑）：心智负担降到最低
- Hook allow vs deny/ask 不变式 → 省略：教学版没有 settings.json 层
- stopHookActive → 省略：教学版 Stop hook 只做简单续跑，不涉及防无限循环机制

</details>

<!-- translation-sync: zh@v1, en@v1, ja@v0 -->
