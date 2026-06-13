# s05: Skills

`s01 > s02 > s03 > s04 > [ s05 ] s06 | s07 > s08 > s09 > s10 > s11 > s12`

> *"必要な知識を、必要な時に読み込む"* -- system prompt ではなく tool_result で注入。
>
> **Harness 層**: オンデマンド知識 -- モデルが求めた時だけ渡すドメイン専門性。

## 問題

エージェントにドメイン固有のワークフローを遵守させたい: gitの規約、テストパターン、コードレビューチェックリスト。すべてをシステムプロンプトに入れると、使われないスキルにトークンを浪費する。10スキル x 2000トークン = 20,000トークン、ほとんどが任意のタスクに無関係だ。

## 解決策

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

第1層: スキル*名*をシステムプロンプトに(低コスト)。第2層: スキル*本体*を tool_result に(オンデマンド)。

## 仕組み

1. 各スキルは `SKILL.md` ファイルを含むディレクトリとして配置される。

```
skills/
  pdf/
    SKILL.md       # ---\nname: pdf\ndescription: Process PDF files\n---\n...
  code-review/
    SKILL.md       # ---\nname: code-review\ndescription: Review code\n---\n...
```

2. `SkillRegistry` が `SKILL.md` を再帰的に探索し、frontmatter をパースしてディレクトリ名をスキル識別子として使用する (`AgentCommon/Skills/SkillRegistry.cs`)。

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

3. 第1層はシステムプロンプトに配置。第2層は通常のツールハンドラ。

```csharp
var skills = SkillRegistry.LoadFromDir(skillsDir);

var system = $"You are a coding agent at {workDir}.\n" +
             $"Skills available:\n{skills.Catalog()}";

SkillTools.Register(tools, skills);
// → "load_skill" ツールを登録し、SkillManifest.FullContent を返す
```

モデルはどのスキルが存在するかを知り(低コスト)、関連する時にだけ読み込む(高コスト)。

## s04からの変更点

| Component      | Before (s04)     | After (s05)                |
|----------------|------------------|----------------------------|
| Tools          | 5 (base + task)  | 5 (base + load_skill)      |
| System prompt  | Static string    | + skill descriptions       |
| Knowledge      | None             | skills/*/SKILL.md files    |
| Injection      | None             | Two-layer (system + result)|

## 試してみる

```sh
cd learn-claude-code-csharp
dotnet run --project s07_skill_loading
```

1. `What skills are available?`
2. `Load the agent-builder skill and follow its instructions`
3. `I need to do a code review -- load the relevant skill first`
4. `Build an MCP server using the mcp-builder skill`
