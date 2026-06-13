# AGENTS.md

A 20-lesson harness engineering tutorial in .NET 10 / C#. The `s01_*` through `s20_*` folders are runnable chapters; `AgentCommon/` is the shared library every chapter links.

## Verification commands (run from repo root)

- Build everything: `dotnet build`
- Run a chapter: `dotnet run --project sNN_<topic>` (e.g. `dotnet run --project s01_agent_loop`)
- Run the endpoint chapter: `dotnet run --project s20_comprehensive`
- Web app (legacy docs renderer): `cd web && npm install && npm run dev` (binds :3000)

There is no `dotnet test` project and no linter/formatter is configured. `dotnet build` is the only static check for C#.

## CI reality (important)

CI in `.github/workflows/` does **not** build the .NET solution. `ci.yml` builds/typechecks the Next.js `web/` app. `test.yml` builds the web app only. .NET changes are not gated by CI; verify locally with `dotnet build` and a smoke run.

## Tutorial tracks

- **Current 20-lesson track**: root-level `s01_*` ... `s20_*` (each has `Program.cs`, `README*.md`).
- **Legacy docs**: `docs/{en,ja,zh}/` was the older 12-lesson track. Content has been ported to C# in a previous cleanup, but the `web/` app still renders these files.

The current 20-lesson track is canonical. The previous Python mirror (`code.py` per chapter, `agents/`, `tests/`, `requirements.txt`, `.env.example`) has been removed — the repo is C#-only.

## .NET solution layout

`LearnClaudeCode.slnx` has 21 projects: `AgentCommon/AgentCommon.csproj` plus one `sNN_*/sNN_*.csproj` per chapter. Every chapter `<ProjectReference>`'s `AgentCommon`. All target `net10.0`.

`Directory.Build.props` (root) sets `<LangVersion>latest</LangVersion>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>`. It also copies any per-chapter `appsettings.json` to the build output.

## API key / LLM config

The default engine is **DeepSeek** via its Anthropic-compatible endpoint:
- `baseUrl`: `https://api.deepseek.com/anthropic`
- `modelId`: `deepseek-v4-flash`
- `maxTokens`: 8000

`AgentCommon/Config/AgentConfig.cs` is the schema. It also reads `maxTokensEscalation` and `fallbackModel`.

Key resolution order (`AgentConfig.ResolveApiKey`):
1. `apiKey` in `appsettings.json` (anything other than the literal `PUT-YOUR-KEY-HERE`).
2. `DEEPSEEK_API_KEY` env var.
3. `ANTHROPIC_API_KEY` env var.

Per-chapter config: copy `sNN_*/appsettings.example.json` to `sNN_*/appsettings.json` and edit. `appsettings.json` is gitignored at every `s*/` level (and at the root) — never commit it. The root `appsettings.example.json` is a tracked template.

`AgentConfigLoader` walks up to 8 parent directories from `AppContext.BaseDirectory` looking for `appsettings.json`, so it works whether you run from the chapter folder or the repo root.

`AgentCommon/Llm/DeepSeekClient.cs` is named after the default engine but speaks the Anthropic Messages API — change `baseUrl`/`modelId` to use any Anthropic-compatible provider.

## Runtime artifacts (auto-created, gitignored)

When agents run, they create these dotfile directories next to the working directory: `.memory/`, `.task_outputs/`, `.tasks/`, `.teams/`, `.mailboxes/`, `.worktrees/`, `.transcripts/`, plus `.scheduled_tasks.json`. All are in `.gitignore`; safe to delete.

## Web app quirks

`web/package.json` defines `predev` and `prebuild` hooks that run `npm run extract` (a `tsx` script that materializes content from `docs/`). The `next dev` / `next build` steps depend on this, so do not bypass it. `web/package.json` and `web/package-lock.json` are the tracked lockfile; an accidental root `package-lock.json` is gitignored.

## Entry points (high-signal files)

- Shared agent loop: `AgentCommon/Agent/AgentHarness.cs:144` (`RunUntilDoneAsync`) — every `Program.cs` calls into it.
- Tool registration contract: `AgentCommon/Tools/ToolRegistry.cs` and `AgentCommon/Defaults/*.cs` (BashTool, FileTools, TodoTools, TaskTool, TeamTools).
- LLM client: `AgentCommon/Llm/DeepSeekClient.cs`.
- Config schema + loader: `AgentCommon/Config/AgentConfig.cs`, `AgentCommon/Config/AgentConfigLoader.cs`.
- Smallest chapter (good reading order entry): `s01_agent_loop/Program.cs`.
- Largest chapter (sees every mechanism wired): `s20_comprehensive/Program.cs`.

## "skills/" folder is tutorial content, not agent config

`skills/agent-builder/`, `skills/code-review/`, `skills/mcp-builder/`, `skills/pdf/` are Markdown assets consumed by chapter s07 (`s07_skill_loading`). The previous Python scripts/references inside `agent-builder/` have been removed; only `SKILL.md` and `references/agent-philosophy.md` remain. They are not OpenCode/Cursor skill files.
