# s04: Subagents

`s01 > s02 > s03 > [ s04 ] s05 > s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"大きなタスクを分割し、各サブタスクにクリーンなコンテキストを"* -- サブエージェントは独立した messages[] を使い、メイン会話を汚さない。
>
> **Harness 層**: コンテキスト隔離 -- モデルの思考の明晰さを守る。

## 問題

エージェントが作業するにつれ、messages配列は膨張し続ける。すべてのファイル読み取り、すべてのbash出力がコンテキストに永久に残る。「このプロジェクトはどのテストフレームワークを使っているか」という質問は5つのファイルを読む必要があるかもしれないが、親に必要なのは「xUnit」という答えだけだ。

## 解決策

```
Parent agent                     Subagent
+------------------+             +------------------+
| messages=[...]   |             | messages=[]      | <-- fresh
|                  |  dispatch   |                  |
| tool: task       | ----------> | while tool_use:  |
|   prompt="..."   |             |   call tools     |
|                  |  summary    |   append results |
|   result = "..." | <---------- | return last text |
+------------------+             +------------------+

Parent context stays clean. Subagent context is discarded.
```

## 仕組み

1. 親に `task` ツールを追加する。子は `task` を除くすべての基本ツールを取得する(再帰的な生成は不可)。

```csharp
// 親ツールセット = 基本 + task
var parentTools = new ToolRegistry();
BashTool.Register(parentTools, workDir);
FileTools.Register(parentTools, workDir);

// 子ツールセット = 基本のみ
var childTools = new ToolRegistry();
BashTool.Register(childTools, workDir);
FileTools.Register(childTools, workDir);

TaskTool.Register(parentTools, () =>
    new SubagentRunner(client, config, childTools,
        systemPrompt: "You are a focused sub-agent. Return a concise summary."));
```

2. サブエージェントは `messages = []` で開始し、自身のループを実行する。最終テキストだけが親に返る (`AgentCommon/Subagent/SubagentRunner.cs`)。

```csharp
public async Task<string> RunAsync(string description, CancellationToken ct = default)
{
    var messages = new List<Message> { Message.UserText(description) };

    LlmResponse? last = null;
    for (var i = 0; i < _maxIterations; i++)
    {
        last = await _client.CreateMessageAsync(
            _systemPrompt, messages, _tools.AllSpecs().ToList(), ct: ct);
        messages.Add(Message.Assistant(last.Content));

        if (last.StopReason != "tool_use")
            break;

        var results = new List<ToolResultBlock>();
        foreach (var block in last.Content.OfType<ToolUseBlock>())
        {
            var output = _tools.Invoke(block.Name, block.Input);
            results.Add(new ToolResultBlock(block.Id, output));
        }
        messages.Add(Message.UserToolResults(results));
    }

    return ExtractFinalText(last, messages);
}
```

子のメッセージ履歴全体(30回以上のツール呼び出し)は破棄される。親は1段落の要約を通常の `tool_result` として受け取る。

## s03からの変更点

| Component      | Before (s03)     | After (s04)               |
|----------------|------------------|---------------------------|
| Tools          | 5                | 5 (base) + task (parent)  |
| Context        | Single shared    | Parent + child isolation  |
| Subagent       | None             | `SubagentRunner` class    |
| Return value   | N/A              | Summary text only         |

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s06_subagent
```

1. `Use a subtask to find what testing framework this project uses`
2. `Delegate: read all .cs files and summarize what each one does`
3. `Use a task to create a new module, then verify it from here`
