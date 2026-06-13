# s02: Tool Use

`s01 > [ s02 ] s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"ツールを足すなら、ハンドラーを1つ足すだけ"* -- ループは変わらない。新ツールは dispatch map に登録するだけ。
>
> **Harness 層**: ツール分配 -- モデルが届く範囲を広げる。

## 問題

`bash`だけでは、エージェントは何でもシェル経由で行う。`cat`は予測不能に切り詰め、`sed`は特殊文字で壊れ、すべてのbash呼び出しが制約のないセキュリティ面になる。`read_file`や`write_file`のような専用ツールなら、ツールレベルでパスのサンドボックス化を強制できる。

重要な点: ツールを追加してもループの変更は不要。

## 解決策

```
+--------+      +-------+      +------------------+
|  User  | ---> |  LLM  | ---> | Tool Dispatch    |
| prompt |      |       |      | {                |
+--------+      +---+---+      |   bash: run_bash |
                    ^           |   read: run_read |
                    |           |   write: run_wr  |
                    +-----------+   edit: run_edit |
                    tool_result | }                |
                                +------------------+

The dispatch map is a dict: {tool_name: handler_function}.
One lookup replaces any if/elif chain.
```

## 仕組み

1. 各ツールにハンドラ関数を定義する。パスのサンドボックス化でワークスペース外への脱出を防ぐ (`AgentCommon/Util/PathGuard.cs`)。

```csharp
public static string SafePath(string workDir, string p)
{
    var root = Path.GetFullPath(workDir);
    var candidate = Path.IsPathRooted(p) ? p : Path.Combine(root, p);
    var resolved = Path.GetFullPath(candidate);

    var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Path escapes workspace: {p}");
    }
    return resolved;
}
```

2. 各ツールは中央の `ToolRegistry` に名前・JSONスキーマ・ハンドラを登録する。

```csharp
var tools = new ToolRegistry();
BashTool.Register(tools, workDir);

tools.Register("read_file", "Read file contents.",
    SchemaBuilder.Object("Read file contents.",
        new Dictionary<string, (string, string, bool)>
        {
            ["path"]  = ("string",  "Path to the file (relative to workspace)", true),
            ["limit"] = ("integer", "Optional maximum number of lines to read", false),
        }),
    input =>
    {
        var pathStr = input.GetProperty("path").GetString() ?? "";
        var resolved = PathGuard.SafePath(workDir, pathStr);
        var lines = File.ReadAllText(resolved).Split('\n');
        return string.Join("\n", lines);
    });
```

3. ループ内で名前によりハンドラをルックアップする。ループ本体はs01から不変。

```csharp
foreach (var block in response.Content.OfType<ToolUseBlock>())
{
    // フック → 権限ゲート → ディスパッチの順。
    var output = tools.Invoke(block.Name, block.Input);
    results.Add(new ToolResultBlock(block.Id, output));
}
```

ツール追加 = `tools.Register(...)` を1つ追加。ループは決して変わらない。

## s01からの変更点

| Component      | Before (s01)       | After (s02)                |
|----------------|--------------------|----------------------------|
| Tools          | 1 (bash only)      | 4 (bash, read, write, edit)|
| Dispatch       | Hardcoded bash call | `ToolRegistry.Invoke`    |
| Path safety    | None               | `PathGuard.SafePath()`     |
| Agent loop     | Unchanged          | Unchanged                  |

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s02_tool_use
```

1. `Read the file README.md`
2. `Create a file called greet.cs with a Greet(string name) method`
3. `Edit greet.cs to add an XML doc comment to the method`
4. `Read greet.cs to verify the edit worked`
