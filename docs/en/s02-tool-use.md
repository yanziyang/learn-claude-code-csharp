# s02: Tool Use

`s01 > [ s02 ] s03 > s04 > s05 > s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"Adding a tool means adding one handler"* -- the loop stays the same; new tools register into the dispatch map.
>
> **Harness layer**: Tool dispatch -- expanding what the model can reach.

## Problem

With only `bash`, the agent shells out for everything. `cat` truncates unpredictably, `sed` fails on special characters, and every bash call is an unconstrained security surface. Dedicated tools like `read_file` and `write_file` let you enforce path sandboxing at the tool level.

The key insight: adding tools does not require changing the loop.

## Solution

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

## How It Works

1. Each tool gets a handler function. Path sandboxing prevents workspace escape (`AgentCommon/Util/PathGuard.cs`).

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

2. Each tool registers a name, JSON schema, and handler against the central `ToolRegistry`.

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

3. In the loop, look up the handler by name. The loop body itself is unchanged from s01.

```csharp
foreach (var block in response.Content.OfType<ToolUseBlock>())
{
    // Hooks first; permission gate second; dispatch last.
    var output = tools.Invoke(block.Name, block.Input);
    results.Add(new ToolResultBlock(block.Id, output));
}
```

Add a tool = add a `tools.Register(...)` call. The loop never changes.

## What Changed From s01

| Component      | Before (s01)       | After (s02)                |
|----------------|--------------------|----------------------------|
| Tools          | 1 (bash only)      | 4 (bash, read, write, edit)|
| Dispatch       | Hardcoded bash call | `ToolRegistry.Invoke`    |
| Path safety    | None               | `PathGuard.SafePath()`     |
| Agent loop     | Unchanged          | Unchanged                  |

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s02_tool_use
```

1. `Read the file README.md`
2. `Create a file called greet.cs with a Greet(string name) method`
3. `Edit greet.cs to add an XML doc comment to the method`
4. `Read greet.cs to verify the edit worked`
