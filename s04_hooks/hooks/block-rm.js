#!/usr/bin/env node
// s04_hooks/hooks/block-rm.js
//
// PreToolUse hook. Blocks destructive bash commands. Reads the event JSON
// from stdin (CC-compatible protocol), decides, exits 0 (allow) or 2 (block).
//
// Run from appsettings.json:
//   "PreToolUse": [
//     { "matcher": "bash",
//       "hooks": [ { "type": "command", "command": "node hooks/block-rm.js" } ] }
//   ]

const DENY = [
  /\brm\s+-rf?\s+\//,        // rm -rf /
  /\bsudo\b/,                // any sudo
  /\bshutdown\b|\breboot\b/, // system control
  /\bmkfs\b/,                // filesystem format
  /\bdd\s+if=/,              // raw disk write
];

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
  let event;
  try {
    event = JSON.parse((await readStdin()) || "{}");
  } catch (e) {
    console.error("block-rm: bad JSON on stdin:", e.message);
    process.exit(2);
  }

  if (event.toolName !== "bash") process.exit(0);

  const cmd = (event.toolInput && event.toolInput.command) || "";
  for (const pat of DENY) {
    if (pat.test(cmd)) {
      console.error(`block-rm: denied by pattern ${pat}`);
      process.exit(2);
    }
  }
  process.exit(0);
})();
