#!/usr/bin/env node
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { resolve, join } from "node:path";
import { pathToFileURL } from "node:url";
import { execFileSync } from "node:child_process";

const PIN = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const args = process.argv.slice(2);
const value = (name) => {
  const index = args.indexOf(name);
  if (index < 0 || !args[index + 1]) throw new Error(`missing ${name}`);
  return args[index + 1];
};
const piRoot = resolve(value("--pi"));
const output = resolve(value("--output"));
const head = execFileSync("git", ["rev-parse", "HEAD"], { cwd: piRoot, encoding: "utf8" }).trim();
if (head !== PIN) throw new Error(`Pi checkout is ${head}; expected ${PIN}`);

const importFromPi = (relative) => import(pathToFileURL(join(piRoot, relative)).href);
const [{ createHarness }, { fauxAssistantMessage }, { createAssistantMessageEventStream }] = await Promise.all([
  importFromPi("packages/coding-agent/test/suite/harness.ts"),
  importFromPi("packages/ai/src/providers/faux.ts"),
  importFromPi("packages/ai/src/utils/event-stream.ts"),
]);

function plainMessage(message) {
  const content = message.content;
  const text = typeof content === "string" ? content
    : Array.isArray(content) ? content.filter((item) => item.type === "text").map((item) => item.text).join("")
      : "";
  return { role: message.role, text };
}

async function runOnce() {
  const harness = await createHarness({
    models: [{ id: "faux-1", contextWindow: 1_000_000, maxTokens: 100 }],
    settings: { compaction: { enabled: true, reserveTokens: 10_000, keepRecentTokens: 1 } },
  });
  const providerRequests = [];
  try {
    const model = harness.getModel();
    const now = Date.now();
    for (const [role, text, offset] of [
      ["user", "first", -4_000],
      ["assistant", "answer one", -3_000],
      ["user", "second", -2_000],
      ["assistant", "answer two", -1_000],
    ]) {
      if (role === "user") {
        harness.sessionManager.appendMessage({ role, content: [{ type: "text", text }], timestamp: now + offset });
      } else {
        harness.sessionManager.appendMessage({
          ...fauxAssistantMessage(text, { timestamp: now + offset }),
          api: model.api,
          provider: model.provider,
          model: model.id,
          usage: { input: 60, output: 10, cacheRead: 0, cacheWrite: 0, totalTokens: 70,
            cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
        });
      }
    }
    harness.session.agent.state.messages = harness.sessionManager.buildSessionContext().messages;
    harness.session.agent.streamFunction = (requestModel, context, options) => {
      const requestNumber = providerRequests.length + 1;
      providerRequests.push({
        model: requestModel.id,
        messages: context.messages.map(plainMessage),
        systemPrompt: context.systemPrompt,
        maxTokens: options?.maxTokens ?? null,
      });
      const stream = createAssistantMessageEventStream();
      const text = requestNumber === 1 ? "partial output"
        : requestNumber === 2 || requestNumber === 3 ? "compacted history summary"
          : "continued response";
      const stopReason = requestNumber === 1 ? "length" : "stop";
      const message = {
        ...fauxAssistantMessage(text, { stopReason }),
        api: requestModel.api,
        provider: requestModel.provider,
        model: requestModel.id,
        usage: { input: 40, output: requestNumber === 1 ? 16 : 8, cacheRead: 0, cacheWrite: 0,
          totalTokens: requestNumber === 1 ? 56 : 48,
          cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
      };
      queueMicrotask(() => stream.push({ type: "done", reason: stopReason, message }));
      return stream;
    };
    await harness.session.prompt("continue");

    return {
      scenario: "C5.recoverable-length-stop-retries-once",
      providerRequests,
      events: harness.events.map((event) => ({
        type: event.type,
        reason: event.reason ?? null,
        willRetry: event.willRetry ?? null,
        errorMessage: event.errorMessage ?? null,
        summary: event.result?.summary ?? null,
        tokensBefore: event.result?.tokensBefore ?? null,
      })),
      sessionEntries: harness.sessionManager.getEntries().map((entry) => entry.type === "message"
        ? { type: "message", message: plainMessage(entry.message), stopReason: entry.message.stopReason ?? null }
        : entry.type === "compaction"
          ? { type: "compaction", summary: entry.summary, tokensBefore: entry.tokensBefore }
          : { type: entry.type }),
      activeMessages: harness.session.agent.state.messages.map(plainMessage),
      compactionCount: harness.sessionManager.getEntries().filter((entry) => entry.type === "compaction").length,
    };
  } finally {
    harness.cleanup();
  }
}

const first = await runOnce();
const second = await runOnce();
const firstBytes = JSON.stringify(first);
const secondBytes = JSON.stringify(second);
if (firstBytes !== secondBytes) throw new Error("pinned Pi oracle was not stable across two executions");
await mkdir(output, { recursive: true });
await writeFile(join(output, "pi-run-1.json"), `${JSON.stringify(first, null, 2)}\n`);
await writeFile(join(output, "pi-run-2.json"), `${JSON.stringify(second, null, 2)}\n`);
const sha256 = createHash("sha256").update(firstBytes).digest("hex");
await writeFile(join(output, "pi-oracle.json"), `${JSON.stringify({
  piSha: PIN,
  scenario: first.scenario,
  stable: true,
  normalizedSha256: sha256,
  runs: ["pi-run-1.json", "pi-run-2.json"],
}, null, 2)}\n`);
console.log(`Stable pinned-Pi observation: ${first.scenario}; SHA-256 ${sha256}`);
