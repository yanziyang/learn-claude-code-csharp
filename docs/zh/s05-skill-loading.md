# s05: Skills (Skill 加载)

`s01 > s02 > s03 > s04 > [ s05 ] s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"用到什么知识, 临时加载什么知识"* -- 通过 tool_result 注入, 不塞 system prompt。
>
> **Harness 层**: 按需知识 -- 模型开口要时才给的领域专长。

## 问题

你希望 Agent 遵循特定领域的工作流: git 约定、测试模式、代码审查清单。全塞进系统提示太浪费 -- 10 个 Skill, 每个 2000 token, 就是 20,000 token, 大部分跟当前任务毫无关系。

## 解决方案

```
System prompt (Layer 1 -- always present):
+--------------------------------------+
| You are a coding agent.              |
| Skills available:                    |
|   - git: Git workflow helpers        |  ~100 tokens/skill
|   - test: Testing best practices     |
+--------------------------------------+

When model calls load_skill("git"):
+--------------------------------------+
| tool_result (Layer 2 -- on demand):  |
| <skill name="git">                   |
|   Full git workflow instructions...  |  ~2000 tokens
|   Step 1: ...                        |
| </skill>                             |
+--------------------------------------+
```

第一层: 系统提示中放 Skill 名称 (低成本)。第二层: tool_result 中按需放完整内容。

## 工作原理

1. 每个 Skill 是一个目录, 包含 `SKILL.md` 文件和 YAML frontmatter。

```
skills/
  pdf/
    SKILL.md       # ---\nname: pdf\ndescription: Process PDF files\n---\n...
  code-review/
    SKILL.md       # ---\nname: code-review\ndescription: Review code\n---\n...
```

2. `SkillRegistry` 递归扫描 `SKILL.md` 文件, 解析 frontmatter 并用目录名作为 Skill 标识 (`AgentCommon/Skills/SkillRegistry.cs`)。

```csharp
public static SkillRegistry LoadFromDir(string skillsDir)
{
    var reg = new SkillRegistry();
    if (!Directory.Exists(skillsDir)) return reg;

    foreach (var dir in Directory.EnumerateDirectories(skillsDir))
    {
        var manifest = Path.Combine(dir, "SKILL.md");
        if (!File.Exists(manifest)) continue;

        var raw = File.ReadAllText(manifest);
        var (name, desc) = ParseFrontmatter(raw);
        if (string.IsNullOrEmpty(name)) name = Path.GetFileName(dir);
        reg._byName[name] = new SkillManifest
        {
            Name = name,
            Description = desc,
            FullContent = raw,
        };
    }
    return reg;
}
```

3. 第一层写入系统提示。第二层不过是注册表中的又一个工具。

```csharp
var skills = SkillRegistry.LoadFromDir(skillsDir);

var system = $"You are a coding agent at {workDir}.\n" +
             $"Skills available:\n{skills.Catalog()}";

SkillTools.Register(tools, skills);
// → 注册 "load_skill" 工具, 返回 SkillManifest.FullContent
```

模型知道有哪些 Skill (便宜), 需要时再加载完整内容 (贵)。

## 相对 s04 的变更

| 组件           | 之前 (s04)       | 之后 (s05)                     |
|----------------|------------------|--------------------------------|
| Tools          | 5 (基础 + task)  | 5 (基础 + load_skill)          |
| 系统提示       | 静态字符串       | + Skill 描述列表               |
| 知识库         | 无               | skills/*/SKILL.md files        |
| 注入方式       | 无               | 两层 (系统提示 + result)       |

## 试一试

```sh
cd learn-claude-code-csharp
dotnet run --project s07_skill_loading
```

试试这些 prompt (英文 prompt 对 LLM 效果更好, 也可以用中文):

1. `What skills are available?`
2. `Load the agent-builder skill and follow its instructions`
3. `I need to do a code review -- load the relevant skill first`
4. `Build an MCP server using the mcp-builder skill`
