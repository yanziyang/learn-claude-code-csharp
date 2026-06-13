import * as fs from "fs";
import * as path from "path";
import type {
  AgentVersion,
  VersionDiff,
  DocContent,
  VersionIndex,
  ChapterImage,
} from "../src/types/agent-data";
import { VERSION_META, VERSION_ORDER, LEARNING_PATH } from "../src/lib/constants";

const WEB_DIR = path.resolve(__dirname, "..");
const REPO_ROOT = path.resolve(WEB_DIR, "..");
const LEGACY_AGENTS_DIR = path.join(REPO_ROOT, "agents");
const LEGACY_DOCS_DIR = path.join(REPO_ROOT, "docs");
const AGENT_COMMON_DIR = path.join(REPO_ROOT, "AgentCommon");
const OUT_DIR = path.join(WEB_DIR, "src", "data", "generated");
const PUBLIC_DIR = path.join(WEB_DIR, "public");
const COURSE_ASSETS_DIR = path.join(PUBLIC_DIR, "course-assets");

type Locale = "en" | "zh" | "ja";
type Language = "csharp" | "python";

interface CodeFile {
  absPath: string;
  relativePath: string;
  language: Language;
}

interface ChapterSource {
  id: string;
  dirName: string;
  dirPath: string;
  code: CodeFile | null;
}

function dirToVersionId(dirName: string): string | null {
  const match = dirName.match(/^(s\d{2})_/);
  return match ? match[1] : null;
}

function filenameToVersionId(filename: string): string | null {
  const base = path.basename(filename, ".py");
  if (base === "s_full" || base === "__init__") return null;

  const match = base.match(/^(s\d+[a-c]?)_/);
  return match ? match[1] : null;
}

function findCodeFile(dirPath: string): CodeFile | null {
  const candidates: { file: string; language: Language }[] = [
    { file: "Program.cs", language: "csharp" },
    { file: "code.py", language: "python" },
  ];
  for (const c of candidates) {
    const abs = path.join(dirPath, c.file);
    if (fs.existsSync(abs)) {
      return { absPath: abs, relativePath: c.file, language: c.language };
    }
  }
  return null;
}

function listRootChapters(): ChapterSource[] {
  return fs
    .readdirSync(REPO_ROOT, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name)
    .filter((name) => /^s\d{2}_/.test(name))
    .sort()
    .map((dirName) => {
      const id = dirToVersionId(dirName);
      if (!id) return null;
      const dirPath = path.join(REPO_ROOT, dirName);
      return { id, dirName, dirPath, code: findCodeFile(dirPath) };
    })
    .filter((chapter): chapter is ChapterSource => chapter !== null);
}

function walkCsFiles(dir: string): string[] {
  const out: string[] = [];
  if (!fs.existsSync(dir)) return out;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      out.push(...walkCsFiles(p));
    } else if (entry.isFile() && p.endsWith(".cs")) {
      out.push(p);
    }
  }
  return out;
}

function extractClasses(
  lines: string[],
  language: Language
): { name: string; startLine: number; endLine: number }[] {
  const classes: { name: string; startLine: number; endLine: number }[] = [];
  if (language === "python") {
    const classPattern = /^class\s+(\w+)/;
    for (let i = 0; i < lines.length; i++) {
      const match = lines[i].match(classPattern);
      if (!match) continue;
      const name = match[1];
      const startLine = i + 1;
      let endLine = lines.length;
      for (let j = i + 1; j < lines.length; j++) {
        if (
          lines[j].match(/^class\s/) ||
          lines[j].match(/^def\s/) ||
          (lines[j].match(/^\S/) &&
            lines[j].trim() !== "" &&
            !lines[j].startsWith("#") &&
            !lines[j].startsWith("@"))
        ) {
          endLine = j;
          break;
        }
      }
      classes.push({ name, startLine, endLine });
    }
    return classes;
  }

  // C#: class | record | struct | interface | enum, with optional modifiers.
  const classPattern =
    /^\s*(?:public|internal|private|protected|file|static|sealed|abstract|partial|new|virtual|override|unsafe|readonly|ref|sealed\s+record|\s+)*\b(?:class|record|struct|interface|enum)\s+(\w+)/;
  for (let i = 0; i < lines.length; i++) {
    const match = lines[i].match(classPattern);
    if (!match) continue;
    const name = match[1];
    const startLine = i + 1;
    let endLine = lines.length;
    for (let j = i + 1; j < lines.length; j++) {
      if (classPattern.test(lines[j])) {
        endLine = j;
        break;
      }
    }
    classes.push({ name, startLine, endLine });
  }
  return classes;
}

function extractFunctions(
  lines: string[],
  language: Language
): { name: string; signature: string; startLine: number }[] {
  if (language === "python") {
    const functions: { name: string; signature: string; startLine: number }[] = [];
    const funcPattern = /^def\s+(\w+)\((.*?)\)/;
    for (let i = 0; i < lines.length; i++) {
      const match = lines[i].match(funcPattern);
      if (!match) continue;
      functions.push({
        name: match[1],
        signature: `def ${match[1]}(${match[2]})`,
        startLine: i + 1,
      });
    }
    return functions;
  }

  // C#: scan lines for method-shaped declarations, with multi-line signature support.
  const functions: { name: string; signature: string; startLine: number }[] = [];
  const skip = new Set([
    "if", "else", "for", "foreach", "while", "do", "switch", "try", "catch", "finally",
    "using", "return", "throw", "yield", "new", "var", "namespace", "class", "struct",
    "interface", "enum", "record", "public", "private", "internal", "protected", "static",
    "sealed", "abstract", "partial", "override", "virtual", "async", "extern", "unsafe",
    "readonly", "volatile", "fixed", "required", "file", "out", "ref", "in", "params",
    "this", "typeof", "sizeof", "nameof", "is", "as", "default", "true", "false", "null",
    "where", "get", "set", "init", "add", "remove", "case", "break", "continue",
  ]);
  // Lines that look like a method declaration must not start with these.
  const statementPrefixes = [
    "var ", "let ", "const ", "new ", "await ", "return ", "throw ", "yield ",
    "= ", "+=", "-=", "*=", "/=", "?: ", "?.", "?.(",
  ];

  let i = 0;
  while (i < lines.length) {
    const trimmed = lines[i].trim();
    if (
      !trimmed ||
      trimmed.startsWith("//") ||
      trimmed.startsWith("/*") ||
      trimmed.startsWith("*")
    ) {
      i++;
      continue;
    }

    // Reject statement-style lines that merely contain a lambda.
    if (statementPrefixes.some((p) => trimmed.startsWith(p))) {
      i++;
      continue;
    }

    const openParen = trimmed.indexOf("(");
    if (openParen < 0) {
      i++;
      continue;
    }

    // Accumulate lines until the closing paren is found.
    let combined = trimmed;
    let j = i;
    if (trimmed.indexOf(")", openParen) < 0) {
      while (j + 1 < lines.length && combined.indexOf(")", openParen) < 0 && j - i < 6) {
        j++;
        combined += " " + lines[j].trim();
      }
    }

    const closeParen = combined.indexOf(")", openParen);
    if (closeParen < 0) {
      i = Math.max(i + 1, j + 1);
      continue;
    }

    const afterParen = combined.substring(closeParen + 1).trimStart();
    let looksLikeMethod =
      afterParen.startsWith("{") || /^=>/.test(afterParen) || /^\s*where\b/.test(afterParen);
    // Multi-line signatures: `)` is the last thing on the line, the next
    // non-blank line starts with `{` or `=>`.
    if (!looksLikeMethod && afterParen === "" && j + 1 < lines.length) {
      let k = j + 1;
      while (k < lines.length && lines[k].trim() === "") k++;
      if (k < lines.length) {
        const next = lines[k].trim();
        if (next.startsWith("{") || next.startsWith("=>") || /^\s*where\b/.test(next)) {
          looksLikeMethod = true;
        }
      }
    }
    if (!looksLikeMethod) {
      i = Math.max(i + 1, j + 1);
      continue;
    }

    const before = combined.substring(0, openParen).trim();
    // Skip when the name is preceded by `new ` (constructor call, e.g. `new Thread(...)`).
    if (/\bnew\s+\w*$/.test(before)) {
      i = Math.max(i + 1, j + 1);
      continue;
    }
    // Strip trailing generic type arguments.
    const beforeNoGeneric = before.replace(/<[^>]+>$/, "").trim();
    const tokens = beforeNoGeneric.split(/\s+/);
    const name = tokens[tokens.length - 1];
    if (!name || !/^\w+$/.test(name)) {
      i = Math.max(i + 1, j + 1);
      continue;
    }
    if (skip.has(name)) {
      i = Math.max(i + 1, j + 1);
      continue;
    }

    const signature = combined
      .split(/\s*\{/)[0]
      .split(/\s*=>/)[0]
      .trim();

    functions.push({ name, signature, startLine: i + 1 });
    i = Math.max(i + 1, j + 1);
  }
  return functions;
}

function extractTools(source: string, language: Language, classToolMap: Map<string, string[]>): string[] {
  const tools = new Set<string>();
  if (language === "python") {
    const toolPattern = /"name"\s*:\s*"([\w-]+)"/g;
    let m: RegExpExecArray | null;
    while ((m = toolPattern.exec(source)) !== null) {
      tools.add(m[1]);
    }
    return Array.from(tools);
  }

  // C#: collect tools from inline `tools.Register("name", ...)` calls
  // and from static `XxxTool.Register(tools, ...)` calls resolved via classToolMap.
  const inlinePattern = /tools\.Register\(\s*"([\w-]+)"/g;
  let m: RegExpExecArray | null;
  while ((m = inlinePattern.exec(source)) !== null) {
    tools.add(m[1]);
  }

  const staticPattern = /(\w+Tools?)\.Register\(\s*\w+/g;
  while ((m = staticPattern.exec(source)) !== null) {
    const className = m[1];
    const names = classToolMap.get(className);
    if (names) {
      for (const n of names) tools.add(n);
    }
  }

  return Array.from(tools);
}

function countLoc(lines: string[], language: Language): number {
  let inBlockComment = false;
  return lines.filter((line) => {
    const trimmed = line.trim();
    if (trimmed === "") return false;

    if (language === "python") {
      return !trimmed.startsWith("#");
    }

    // C#
    if (inBlockComment) {
      if (trimmed.includes("*/")) inBlockComment = false;
      return false;
    }
    if (trimmed.startsWith("/*")) {
      if (!trimmed.endsWith("*/") && !trimmed.includes("*/")) inBlockComment = true;
      return false;
    }
    if (trimmed.startsWith("//")) return false;
    if (trimmed.startsWith("*")) return false;
    return true;
  }).length;
}

function detectLocale(relPath: string): Locale {
  if (relPath.startsWith("zh/") || relPath.startsWith("zh\\")) return "zh";
  if (relPath.startsWith("ja/") || relPath.startsWith("ja\\")) return "ja";
  return "en";
}

function extractDocVersion(filename: string): string | null {
  const match = filename.match(/^(s\d+[a-c]?)-/);
  return match ? match[1] : null;
}

function titleFromMarkdown(content: string, fallback: string): string {
  const titleMatch = content.match(/^#\s+(.+)$/m);
  return titleMatch ? titleMatch[1] : fallback;
}

function cleanCourseAssets() {
  fs.rmSync(COURSE_ASSETS_DIR, { recursive: true, force: true });
  fs.mkdirSync(COURSE_ASSETS_DIR, { recursive: true });
}

function copyChapterAssets(chapter: ChapterSource): ChapterImage[] {
  const imagesDir = path.join(chapter.dirPath, "images");
  if (!fs.existsSync(imagesDir)) return [];

  const outDir = path.join(COURSE_ASSETS_DIR, chapter.dirName);
  fs.mkdirSync(outDir, { recursive: true });
  fs.cpSync(imagesDir, outDir, { recursive: true });

  return fs
    .readdirSync(imagesDir)
    .filter((filename) => filename.endsWith(".svg"))
    .filter((filename) => !filename.includes(".en.") && !filename.includes(".ja."))
    .sort()
    .map((filename) => ({
      src: `/course-assets/${chapter.dirName}/${filename}`,
      alt: filename.replace(/\.svg$/, "").replace(/-/g, " "),
    }));
}

function localeReadmeName(locale: Locale): string {
  if (locale === "zh") return "README.md";
  return `README.${locale}.md`;
}

function rewriteChapterMarkdown(
  content: string,
  chapter: ChapterSource,
  locale: Locale
): string {
  let next = content;

  next = next.replace(
    /^\[中文\]\(README\.md\)\s*.\s*\[English\]\(README\.en\.md\)\s*.\s*\[日本語\]\(README\.ja\.md\)\n\n?/m,
    ""
  );

  next = next.replace(
    /(!\[[^\]]*\]\()images\/([^)]+)(\))/g,
    `$1/course-assets/${chapter.dirName}/$2$3`
  );

  next = next.replace(
    /\]\(\.\.\/(s\d{2}_[^)\/]+)\/?\)/g,
    (_match, dirName) => {
      const id = dirToVersionId(dirName);
      return id ? `](/${locale}/${id})` : `](../${dirName}/)`;
    }
  );

  next = next.replace(
    /\]\(\.\/(s\d{2}_[^)\/]+)\/?\)/g,
    (_match, dirName) => {
      const id = dirToVersionId(dirName);
      return id ? `](/${locale}/${id})` : `](./${dirName}/)`;
    }
  );

  return next;
}

function buildClassToolMap(): Map<string, string[]> {
  // Walk AgentCommon/*.cs, track class scope, and capture every
  // `tools.Register("name", ...)` call found inside each class body.
  const map = new Map<string, string[]>();
  const classPattern =
    /^\s*(?:public|internal|private|protected|file|static|sealed|abstract|partial|new|virtual|override|unsafe|readonly|ref|\s+)*\bclass\s+(\w+)/;
  const toolRegisterPattern = /tools\.Register\(\s*"([\w-]+)"/g;

  for (const file of walkCsFiles(AGENT_COMMON_DIR)) {
    const source = fs.readFileSync(file, "utf-8");
    const lines = source.split("\n");
    let currentClass: string | null = null;
    let depth = 0;
    let classDepth = -1;
    let justEntered = false;

    for (const line of lines) {
      if (currentClass === null) {
        const m = line.match(classPattern);
        if (m) {
          currentClass = m[1];
          if (!map.has(currentClass)) map.set(currentClass, []);
          classDepth = -1;
          justEntered = true;
        }
      } else {
        const re = new RegExp(toolRegisterPattern.source, "g");
        let mm: RegExpExecArray | null;
        while ((mm = re.exec(line)) !== null) {
          const list = map.get(currentClass)!;
          if (!list.includes(mm[1])) list.push(mm[1]);
        }
      }

      for (const ch of line) {
        if (ch === "{") depth++;
        else if (ch === "}") depth--;
      }

      if (justEntered) {
        justEntered = false;
        classDepth = depth;
      } else if (currentClass !== null && classDepth >= 0 && depth < classDepth) {
        currentClass = null;
        classDepth = -1;
      }
    }
  }
  return map;
}

function buildRootVersions(
  chapters: ChapterSource[],
  classToolMap: Map<string, string[]>
): AgentVersion[] {
  return chapters.map((chapter) => {
    const meta = VERSION_META[chapter.id];
    if (!chapter.code) {
      return {
        id: chapter.id,
        filename: `${chapter.dirName}/README.md`,
        title: meta?.title ?? chapter.id,
        subtitle: meta?.subtitle ?? "",
        loc: 0,
        tools: [],
        newTools: [],
        coreAddition: meta?.coreAddition ?? "",
        keyInsight: meta?.keyInsight ?? "",
        classes: [],
        functions: [],
        layer: meta?.layer ?? "tools",
        source: "",
        images: copyChapterAssets(chapter),
      };
    }

    const source = fs.readFileSync(chapter.code.absPath, "utf-8");
    const lines = source.split("\n");
    return {
      id: chapter.id,
      filename: `${chapter.dirName}/${chapter.code.relativePath}`,
      title: meta?.title ?? chapter.id,
      subtitle: meta?.subtitle ?? "",
      loc: countLoc(lines, chapter.code.language),
      tools: extractTools(source, chapter.code.language, classToolMap),
      newTools: [] as string[],
      coreAddition: meta?.coreAddition ?? "",
      keyInsight: meta?.keyInsight ?? "",
      classes: extractClasses(lines, chapter.code.language),
      functions: extractFunctions(lines, chapter.code.language),
      layer: meta?.layer ?? "tools",
      source,
      images: copyChapterAssets(chapter),
    };
  });
}

function buildLegacyVersions(): AgentVersion[] {
  if (!fs.existsSync(LEGACY_AGENTS_DIR)) return [];

  const agentFiles = fs
    .readdirSync(LEGACY_AGENTS_DIR)
    .filter((filename) => filename.startsWith("s") && filename.endsWith(".py"));

  const versions = agentFiles
    .map((filename) => {
      const id = filenameToVersionId(filename);
      if (!id) return null;

      const filePath = path.join(LEGACY_AGENTS_DIR, filename);
      const source = fs.readFileSync(filePath, "utf-8");
      const lines = source.split("\n");
      const meta = VERSION_META[id];

      return {
        id,
        filename,
        title: meta?.title ?? id,
        subtitle: meta?.subtitle ?? "",
        loc: countLoc(lines, "python"),
        tools: extractTools(source, "python", new Map()),
        newTools: [] as string[],
        coreAddition: meta?.coreAddition ?? "",
        keyInsight: meta?.keyInsight ?? "",
        classes: extractClasses(lines, "python"),
        functions: extractFunctions(lines, "python"),
        layer: meta?.layer ?? "tools",
        source,
        images: [] as ChapterImage[],
      };
    })
    .filter((version): version is AgentVersion => version !== null);

  return versions;
}

function buildRootDocs(chapters: ChapterSource[]): DocContent[] {
  const docs: DocContent[] = [];
  const locales: Locale[] = ["en", "zh", "ja"];

  for (const chapter of chapters) {
    for (const locale of locales) {
      const filename = localeReadmeName(locale);
      const filePath = path.join(chapter.dirPath, filename);
      if (!fs.existsSync(filePath)) continue;

      const raw = fs.readFileSync(filePath, "utf-8");
      const content = rewriteChapterMarkdown(raw, chapter, locale);
      docs.push({
        version: chapter.id,
        locale,
        title: titleFromMarkdown(content, filename),
        content,
      });
    }
  }

  return docs;
}

function buildLegacyDocs(): DocContent[] {
  const docs: DocContent[] = [];
  if (!fs.existsSync(LEGACY_DOCS_DIR)) return docs;

  const localeDirs: Locale[] = ["en", "zh", "ja"];
  for (const locale of localeDirs) {
    const localeDir = path.join(LEGACY_DOCS_DIR, locale);
    if (!fs.existsSync(localeDir)) continue;

    const docFiles = fs.readdirSync(localeDir).filter((f) => f.endsWith(".md"));
    for (const filename of docFiles) {
      const version = extractDocVersion(filename);
      if (!version) continue;

      const relPath = path.join(locale, filename);
      const filePath = path.join(LEGACY_DOCS_DIR, relPath);
      const content = fs.readFileSync(filePath, "utf-8");
      docs.push({
        version,
        locale: detectLocale(relPath),
        title: titleFromMarkdown(content, filename),
        content,
      });
    }
  }

  return docs;
}

function computeNewTools(versions: AgentVersion[]) {
  for (let i = 0; i < versions.length; i++) {
    const prev = i > 0 ? new Set(versions[i - 1].tools) : new Set<string>();
    versions[i].newTools = versions[i].tools.filter((tool) => !prev.has(tool));
  }
}

function buildDiffs(versions: AgentVersion[]): VersionDiff[] {
  const diffs: VersionDiff[] = [];
  const versionMap = new Map(versions.map((version) => [version.id, version]));

  for (let i = 1; i < LEARNING_PATH.length; i++) {
    const fromId = LEARNING_PATH[i - 1];
    const toId = LEARNING_PATH[i];
    const fromVer = versionMap.get(fromId);
    const toVer = versionMap.get(toId);
    if (!fromVer || !toVer) continue;

    const fromClassNames = new Set(fromVer.classes.map((cls) => cls.name));
    const fromFuncNames = new Set(fromVer.functions.map((fn) => fn.name));
    const fromToolNames = new Set(fromVer.tools);

    diffs.push({
      from: fromId,
      to: toId,
      newClasses: toVer.classes
        .map((cls) => cls.name)
        .filter((name) => !fromClassNames.has(name)),
      newFunctions: toVer.functions
        .map((fn) => fn.name)
        .filter((name) => !fromFuncNames.has(name)),
      newTools: toVer.tools.filter((tool) => !fromToolNames.has(tool)),
      locDelta: toVer.loc - fromVer.loc,
    });
  }

  return diffs;
}

function sortVersions(versions: AgentVersion[]) {
  const orderMap = new Map(VERSION_ORDER.map((id, index) => [id, index]));
  versions.sort(
    (a, b) => (orderMap.get(a.id as any) ?? 99) - (orderMap.get(b.id as any) ?? 99)
  );
}

function main() {
  console.log("Extracting course content...");
  console.log(`  Repo root: ${REPO_ROOT}`);

  cleanCourseAssets();

  const rootChapters = listRootChapters();
  const useRootTrack = rootChapters.length > 0;

  console.log(
    useRootTrack
      ? `  Source: root chapter folders (${rootChapters.length})`
      : "  Source: legacy agents/docs folders"
  );

  const classToolMap = useRootTrack ? buildClassToolMap() : new Map<string, string[]>();
  console.log(
    useRootTrack
      ? `  Built class→tool map with ${classToolMap.size} entries`
      : ""
  );

  const versions = useRootTrack
    ? buildRootVersions(rootChapters, classToolMap)
    : buildLegacyVersions();
  const docs = useRootTrack ? buildRootDocs(rootChapters) : buildLegacyDocs();

  sortVersions(versions);
  computeNewTools(versions);
  const diffs = buildDiffs(versions);

  fs.mkdirSync(OUT_DIR, { recursive: true });

  const index: VersionIndex = { versions, diffs };
  fs.writeFileSync(path.join(OUT_DIR, "versions.json"), JSON.stringify(index, null, 2));
  fs.writeFileSync(path.join(OUT_DIR, "docs.json"), JSON.stringify(docs, null, 2));

  console.log("\nExtraction complete:");
  console.log(`  ${versions.length} versions`);
  console.log(`  ${diffs.length} diffs`);
  console.log(`  ${docs.length} docs`);
  for (const version of versions) {
    console.log(
      `    ${version.id}: ${version.loc} LOC, ${version.tools.length} tools, ${version.classes.length} classes, ${version.functions.length} functions`
    );
  }
}

main();
