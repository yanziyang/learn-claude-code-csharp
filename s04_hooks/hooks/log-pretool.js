#!/usr/bin/env node
// s04_hooks/hooks/log-pretool.js
//
// PreToolUse hook. Prints a one-line audit of every tool call to stderr.
// Replaces the in-code C# OnPreToolUse logging hook in earlier drafts.
//
// Run from appsettings.json:
//   "PreToolUse": [
//     { "hooks": [ { "type": "command", "command": "node hooks/log-pretool.js" } ] }
//   ]

function readStdin() {
  return new Promise((resolve, reject) => {
    let buf = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => (buf += c));
    process.stdin.on("end", () => resolve(buf));
    process.stdin.on("error", reject);
  });
}

function truncate(s, n) {
  s = String(s);
  return s.length > n ? s.slice(0, n) + "..." : s;
}

(async () => {
  const raw = await readStdin();
  let event;
  try { event = JSON.parse(raw || "{}"); } catch { process.exit(0); }

  const tool = event.toolName || "?";
  const input = event.toolInput || {};
  const preview = Object.entries(input)
    .slice(0, 2)
    .map(([k, v]) => `${k}=${truncate(typeof v === "string" ? v : JSON.stringify(v), 50)}`)
    .join(", ");
  console.error(`[log-pretool] ${tool}(${preview})`);
  process.exit(0);
})();
