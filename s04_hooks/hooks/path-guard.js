#!/usr/bin/env node
// s04_hooks/hooks/path-guard.js
//
// PreToolUse hook. Blocks write_file / edit_file targets that escape the
// working directory. Replaces the path-guard branch of the in-code C# hook.
//
// Run from appsettings.json:
//   "PreToolUse": [
//     { "matcher": "write_file|edit_file",
//       "hooks": [ { "type": "command", "command": "node hooks/path-guard.js" } ] }
//   ]

const path = require("path");

function readStdin() {
  return new Promise((resolve, reject) => {
    let buf = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => (buf += c));
    process.stdin.on("end", () => resolve(buf));
    process.stdin.on("error", reject);
  });
}

function isInside(parent, child) {
  const rel = path.relative(parent, child);
  return rel && !rel.startsWith("..") && !path.isAbsolute(rel);
}

(async () => {
  const raw = await readStdin();
  let event;
  try { event = JSON.parse(raw || "{}"); } catch { process.exit(0); }

  const target = event.toolInput && event.toolInput.path;
  if (typeof target !== "string" || target.length === 0) process.exit(0);

  const cwd = process.cwd();
  const resolved = path.resolve(cwd, target);
  if (resolved !== cwd && !isInside(cwd, resolved)) {
    console.error(`[path-guard] ${event.toolName} -> ${target} is outside ${cwd}`);
    process.exit(2);
  }
  process.exit(0);
})();
