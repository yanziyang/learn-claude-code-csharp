#!/usr/bin/env node
// s04_hooks/hooks/audit-log.js
//
// PostToolUse hook. Appends one JSON line per tool call to .memory/audit.log
// so every tool invocation leaves a tamper-evident trail.
//
// Run from appsettings.json:
//   "PostToolUse": [
//     { "hooks": [ { "type": "command", "command": "node hooks/audit-log.js" } ] }
//   ]

const fs = require("fs");
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

(async () => {
  const raw = await readStdin();
  let event;
  try {
    event = JSON.parse(raw || "{}");
  } catch {
    process.exit(0);
  }

  const line = JSON.stringify({
    ts: new Date().toISOString(),
    hook: "PostToolUse",
    tool: event.toolName,
    input: event.toolInput,
    outputLen: (event.toolOutput || "").length,
  });

  const dir = path.join(process.cwd(), ".memory");
  fs.mkdirSync(dir, { recursive: true });
  fs.appendFileSync(path.join(dir, "audit.log"), line + "\n", "utf8");
  process.exit(0);
})();
