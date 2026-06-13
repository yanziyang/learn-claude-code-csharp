# s01: The Agent Loop

`[ s01 ] s02 > s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"One loop & Bash is all you need"* -- 1つのツール + 1つのループ = エージェント。
>
> **Harness 層**: ループ -- モデルと現実世界を繋ぐ最初の接点。

## 問題

言語モデルはコードについて推論できるが、現実世界に触れられない。ファイルを読めず、テストを実行できず、エラーを確認できない。ループがなければ、ツール呼び出しのたびにユーザーが手動で結果をコピーペーストする必要がある。つまりユーザー自身がループになる。

## 解決策

```
+--------+      +-------+      +---------+
|  User  | ---> |  LLM  | ---> |  Tool   |
| prompt |      |       |      | execute |
+--------+      +---+---+      +----+----+
                    ^                |
                    |   tool_result  |
                    +----------------+
                    (loop until stop_reason != "tool_use")
```

1つの終了条件がフロー全体を制御する。モデルがツール呼び出しを止めるまでループが回り続ける。

## 仕組み

1. ユーザーのプロンプトが最初のメッセージになる。

```csharp
messages.Add(Message.UserText(query));
```

2. メッセージとツール定義をLLMに送信する。

```csharp
var response = await client.CreateMessageAsync(
    system, messages, tools.AllSpecs().ToList(), maxTokens: 8000);
```

3. アシスタントのレスポンスを追加し、`stop_reason`を確認する。ツールが呼ばれなければ終了。

```csharp
messages.Add(Message.Assistant(response.Content));
if (response.StopReason != "tool_use")
    return response;
```

4. 各ツール呼び出しを実行し、結果を収集してuserメッセージとして追加。ステップ2に戻る。

```csharp
var results = new List<ToolResultBlock>();
foreach (var block in response.Content.OfType<ToolUseBlock>())
{
    var output = tools.Invoke(block.Name, block.Input);
    results.Add(new ToolResultBlock(block.Id, output));
}
messages.Add(Message.UserToolResults(results));
```

1つの関数にまとめると (`AgentCommon/Agent/AgentHarness.cs`):

```csharp
public async Task<LlmResponse> RunAsync(
    List<Message> messages, int? maxTokensOverride = null, CancellationToken ct = default)
{
    Hooks.FireBeforeLlmCall(messages);
    Compactor.PrepareBeforeLlm(messages);

    var systemPrompt = SystemPromptProvider?.Invoke() ?? "";
    var response = await Client.CreateMessageAsync(
        systemPrompt, messages, Tools.AllSpecs().ToList(),
        maxTokensOverride, modelOverride: null, ct);
    messages.Add(Message.Assistant(response.Content));

    if (response.StopReason != "tool_use")
        return response;

    var results = new List<ToolResultBlock>();
    foreach (var block in response.Content.OfType<ToolUseBlock>())
    {
        var output = Tools.Invoke(block.Name, block.Input);
        results.Add(new ToolResultBlock(block.Id, output));
    }
    messages.Add(Message.UserToolResults(results));
    return response;
}
```

これでエージェント全体がこのループに収まる。本コースの残りはすべてこのループの上に積み重なる -- ループ自体は変わらない。

## 変更点

| Component     | Before     | After                          |
|---------------|------------|--------------------------------|
| Agent loop    | (none)     | `while stop_reason == "tool_use"` |
| Tools         | (none)     | `bash` (one tool)              |
| Messages      | (none)     | Accumulating list              |
| Control flow  | (none)     | `StopReason != "tool_use"`     |

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s01_agent_loop
```

1. `Create a file called hello.cs that prints "Hello, World!"`
2. `List all C# files in this directory`
3. `What is the current git branch?`
4. `Create a directory called test_output and write 3 files in it`
