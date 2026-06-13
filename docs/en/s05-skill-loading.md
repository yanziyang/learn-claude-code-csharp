# s05: Skills

`s01 > s02 > s03 > s04 > [ s05 ] s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"Load knowledge when you need it, not upfront"* -- inject via tool_result, not the system prompt.
>
> **Harness layer**: On-demand knowledge -- domain expertise, loaded when the model asks.

## Problem

You want the agent to follow domain-specific workflows: git conventions, testing patterns, code review checklists. Putting everything in the system prompt wastes tokens on unused skills. 10 skills at 2000 tokens each = 20,000 tokens, most of which are irrelevant to any given task.

## Solution

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

Layer 1: skill *names* in system prompt (cheap). Layer 2: full *body* via tool_result (on demand).

## How It Works

1. Each skill is a directory containing a `SKILL.md` with YAML frontmatter.

```
skills/
  pdf/
    SKILL.md       # ---\nname: pdf\ndescription: Process PDF files\n---\n...
  code-review/
    SKILL.md       # ---\nname: code-review\ndescription: Review code\n---\n...
```

2. `SkillRegistry` scans for `SKILL.md` files, parses the frontmatter, and uses the directory name as the skill identifier (`AgentCommon/Skills/SkillRegistry.cs`).

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

3. Layer 1 goes into the system prompt. Layer 2 is just another tool handler.

```csharp
var skills = SkillRegistry.LoadFromDir(skillsDir);

var system = $"You are a coding agent at {workDir}.\n" +
             $"Skills available:\n{skills.Catalog()}";

SkillTools.Register(tools, skills);
// → registers a "load_skill" tool that returns SkillManifest.FullContent
```

The model learns what skills exist (cheap) and loads them when relevant (expensive).

## What Changed From s04

| Component      | Before (s04)     | After (s05)                |
|----------------|------------------|----------------------------|
| Tools          | 5 (base + task)  | 5 (base + load_skill)      |
| System prompt  | Static string    | + skill descriptions       |
| Knowledge      | None             | skills/*/SKILL.md files    |
| Injection      | None             | Two-layer (system + result)|

## Try It

```sh
cd learn-claude-code-csharp
dotnet run --project s07_skill_loading
```

1. `What skills are available?`
2. `Load the agent-builder skill and follow its instructions`
3. `I need to do a code review -- load the relevant skill first`
4. `Build an MCP server using the mcp-builder skill`
