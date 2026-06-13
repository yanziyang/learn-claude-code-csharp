# s02: Tool Use (工具使用)

`s01 > [ s02 ] s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"加一个工具, 只加一个 handler"* -- 循环不用动, 新工具注册进 dispatch map 就行。
>
> **Harness 层**: 工具分发 -- 扩展模型能触达的边界。

## 问题

只有 `bash` 时, 所有操作都走 shell。`cat` 截断不可预测, `sed` 遇到特殊字符就崩, 每次 bash 调用都是不受约束的安全面。专用工具 (`read_file`, `write_file`) 可以在工具层面做路径沙箱。

关键洞察: 加工具不需要改循环。

## 解决方案

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

## 工作原理

1. 每个工具有一个处理函数。路径沙箱防止逃逸工作区 (`AgentCommon/Util/PathGuard.cs`)。

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

2. 每个工具向中央 `ToolRegistry` 注册 名称 / JSON schema / handler。

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

3. 循环中按名称查找处理函数。循环体本身与 s01 完全一致。

```csharp
foreach (var block in response.Content.OfType<ToolUseBlock>())
{
    // 钩子 → 权限网关 → 分发 的顺序
    var output = tools.Invoke(block.Name, block.Input);
    results.Add(new ToolResultBlock(block.Id, output));
}
```

加工具 = `tools.Register(...)` 调用一次。循环永远不变。

## 相对 s01 的变更

| 组件           | 之前 (s01)         | 之后 (s02)                     |
|----------------|--------------------|--------------------------------|
| Tools          | 1 (仅 bash)        | 4 (bash, read, write, edit)    |
| Dispatch       | 硬编码 bash 调用   | `ToolRegistry.Invoke`         |
| 路径安全       | 无                 | `PathGuard.SafePath()`         |
| Agent loop     | 不变               | 不变                           |

## 试一试

```sh
cd learn-claude-code-csharp
dotnet run --project s02_tool_use
```

试试这些 prompt (英文 prompt 对 LLM 效果更好, 也可以用中文):

1. `Read the file README.md`
2. `Create a file called greet.cs with a Greet(string name) method`
3. `Edit greet.cs to add an XML doc comment to the method`
4. `Read greet.cs to verify the edit worked`
