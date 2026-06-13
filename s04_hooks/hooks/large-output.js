#!/usr/bin/env node
// s04_hooks/hooks/large-output.js
//
// PostToolUse hook. Warns to stderr when a tool's output is suspiciously large.
// Replaces the in-code C# OnPostToolUse large_output_hook in earlier drafts.
//
// Run from appsettings.json:
//   "PostToolUse": [
//     { "hooks": [ { "type": "command", "command": "node hooks/large-output.js" } ] }
//   ]

const THRESHOLD = 100_000;

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
  try { event = JSON.parse(raw || "{}"); } catch { process.exit(0); }

  const tool = event.toolName || "?";
  const output = event.toolOutput || "";
  if (output.length > THRESHOLD) {
    console.error(`[large-output] ${tool}: ${output.length} chars (>${THRESHOLD})`);
  }
  process.exit(0);
})();
