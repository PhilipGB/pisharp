import { readFileSync } from "node:fs";
import { render } from "./dist/index.js";

const source = readFileSync(0, "utf-8");

try {
  const art = render(source);
  process.stdout.write(JSON.stringify(art ?? null) + "\n");
} catch (error) {
  process.stdout.write(JSON.stringify(null) + "\n");
}
