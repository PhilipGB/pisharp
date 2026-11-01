import { createInterface } from "node:readline";
import { render } from "./dist/index.js";

const input = createInterface({ input: process.stdin, terminal: false });

for await (const line of input) {
  if (line.length === 0) continue;
  let id = null;
  let source = "";
  try {
    const request = JSON.parse(line);
    id = request.id ?? null;
    source = typeof request.source === "string" ? request.source : "";
  } catch {
    continue;
  }
  let art = null;
  try {
    art = render(source);
  } catch {
    art = null;
  }
  try {
    process.stdout.write(JSON.stringify({ id, art: art ?? null }) + "\n");
  } catch {
    break;
  }
}
