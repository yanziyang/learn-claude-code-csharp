// s04_hooks/hooks/summary.ts
//
// Stop hook. Writes a session summary to .memory/session.json and prints
// a one-liner. Demonstrates the TS variant — run with `npx tsx`.
//
// From appsettings.json:
//   "Stop": [
//     { "hooks": [ { "type": "command",
//                    "command": "npx tsx hooks/summary.ts" } ] }
//   ]

import * as fs from "fs";
import * as path from "path";

type Event = { hookEventName: string; sessionStats?: { toolCalls: number } };

function readStdin(): Promise<string> {
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
  let event: Event = { hookEventName: "Stop" };
  try { event = JSON.parse(raw) as Event; } catch { /* tolerate bad payload */ }

  const toolCalls = event.sessionStats?.toolCalls ?? 0;
  const summary = {
    finishedAt: new Date().toISOString(),
    toolCalls,
  };

  const dir = path.join(process.cwd(), ".memory");
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(
    path.join(dir, "session.json"),
    JSON.stringify(summary, null, 2),
    "utf8",
  );

  console.error(`[summary.ts] session wrote ${toolCalls} tool call(s)`);
  process.exit(0);
})();
