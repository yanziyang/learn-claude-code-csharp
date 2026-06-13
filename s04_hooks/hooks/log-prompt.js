#!/usr/bin/env node
// s04_hooks/hooks/log-prompt.js
//
// UserPromptSubmit hook. Logs the working directory and prompt length to
// stderr. Replaces the in-code C# OnUserPromptSubmit hook in earlier drafts.
//
// Run from appsettings.json:
//   "UserPromptSubmit": [
//     { "hooks": [ { "type": "command", "command": "node hooks/log-prompt.js" } ] }
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

(async () => {
  const raw = await readStdin();
  let event;
  try { event = JSON.parse(raw || "{}"); } catch { event = {}; }
  const prompt = (event.userPrompt || "").slice(0, 60);
  console.error(`[log-prompt] cwd=${process.cwd()} prompt="${prompt}${event.userPrompt && event.userPrompt.length > 60 ? "..." : ""}"`);
  process.exit(0);
})();
