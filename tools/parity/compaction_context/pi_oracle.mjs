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
const scenario = args.includes("--scenario") ? value("--scenario") : "length-recovery";
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
  const contextWindow = ["context-boundary-length", "silent-usage-overflow"].includes(scenario) ? 10_000
    : scenario === "unknown-model-window" ? 0 : scenario === "stale-old-model-overflow" ? 1_000 : 1_000_000;
  const models = scenario === "stale-old-model-overflow"
    ? [{ id: "old-model", contextWindow, maxTokens: 100 }, { id: "new-model", contextWindow: 1_000_000, maxTokens: 100 }]
    : [{ id: "faux-1", contextWindow, maxTokens: 100 }];
  const harness = await createHarness({
    models,
    settings: { compaction: { enabled: true,
      reserveTokens: ["context-boundary-length", "silent-usage-overflow", "stale-old-model-overflow"].includes(scenario)
        ? 0 : 10_000,
      keepRecentTokens: 1 } },
  });
  const providerRequests = [];
  let modelSwitchApplied = false;
  try {
    const model = harness.getModel();
    const now = Date.parse("2100-01-01T00:00:00.000Z");
    // Recovery checks compare response timestamps with the compaction boundary.
    // Keep fixture responses after that boundary even when both operations share a millisecond.
    const responseTimestampBase = now + 86_400_000;
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
      const overflowError = requestNumber === 1 &&
        ["pre-content-overflow", "partial-content-overflow"].includes(scenario);
      const text = requestNumber === 1
        ? scenario === "partial-content-overflow" ? "partial output"
          : scenario === "silent-usage-overflow" || scenario === "unknown-model-window" ? "completed response"
            : overflowError ? "" : "partial output"
        : requestNumber === 2 || requestNumber === 3 ? "compacted history summary" : "continued response";
      const stopReason = overflowError ? "error" :
        requestNumber === 1 && scenario === "stale-old-model-overflow" ? "length" :
        requestNumber === 1 && scenario === "silent-usage-overflow" ? "stop" :
          requestNumber === 1 && scenario === "unknown-model-window" ? "stop" :
            requestNumber === 1 || (scenario === "second-length-stop" && requestNumber === 4) ? "length" : "stop";
      const inputTokens = requestNumber === 1 && scenario === "context-boundary-length" ? contextWindow
        : requestNumber === 1 && scenario === "stale-old-model-overflow" ? contextWindow
        : requestNumber === 1 && scenario === "silent-usage-overflow" ? contextWindow + 1
          : requestNumber === 1 && scenario === "unknown-model-window" ? 20_000
            : requestNumber === 1 ? 80 : 60;
      const outputTokens = requestNumber === 1
        ? (["context-boundary-length", "stale-old-model-overflow"].includes(scenario) || overflowError ? 0 : 16)
        : 12;
      const message = {
        ...fauxAssistantMessage(text, {
          stopReason,
          ...(overflowError ? { errorMessage: "context_length_exceeded: prompt exceeds context window" } : {}),
          timestamp: responseTimestampBase + requestNumber,
        }),
        api: requestModel.api,
        provider: requestModel.provider,
        model: requestModel.id,
        usage: { input: inputTokens, output: outputTokens, cacheRead: 0, cacheWrite: 0,
          totalTokens: inputTokens + outputTokens,
          cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
      };
      queueMicrotask(async () => {
        if (overflowError) {
          stream.push({ type: "error", reason: "error", error: message });
          stream.end(message);
        } else if (scenario === "stale-old-model-overflow" && requestNumber === 1) {
          await harness.session.setModel(harness.getModel("new-model"));
          modelSwitchApplied = true;
          stream.push({ type: "done", reason: stopReason, message });
        } else stream.push({ type: "done", reason: stopReason, message });
      });
      return stream;
    };
    await harness.session.prompt("continue");

    return {
      scenario,
      modelSwitchApplied,
      selectedModel: harness.session.model.id,
      providerRequests,
      events: harness.events.map((event) => ({
        type: event.type,
        reason: event.reason ?? null,
        willRetry: event.willRetry ?? null,
        errorMessage: event.errorMessage ?? null,
        summary: event.result?.summary ?? null,
        tokensBefore: event.result?.tokensBefore ?? null,
        usage: event.type === "message_end" && event.message?.role === "assistant" &&
          event.message.stopReason !== "error"
          ? { inputTokens: event.message.usage.input, outputTokens: event.message.usage.output }
          : null,
      })),
      sessionEntries: harness.sessionManager.getEntries().map((entry) => entry.type === "message"
        ? { type: "message", message: plainMessage(entry.message), stopReason: entry.message.stopReason ?? null }
        : entry.type === "compaction"
          ? { type: "compaction", summary: entry.summary, tokensBefore: entry.tokensBefore }
          : { type: entry.type }),
      activeMessages: harness.session.agent.state.messages.map(plainMessage),
      compactionCount: harness.sessionManager.getEntries().filter((entry) => entry.type === "compaction").length,
      selectedModel: harness.session.model.id,
    };
  } finally {
    harness.cleanup();
  }
}

const first = await runOnce();
const second = await runOnce();
const firstBytes = JSON.stringify(first);
const secondBytes = JSON.stringify(second);
await mkdir(output, { recursive: true });
await writeFile(join(output, "pi-run-1.json"), `${JSON.stringify(first, null, 2)}\n`);
await writeFile(join(output, "pi-run-2.json"), `${JSON.stringify(second, null, 2)}\n`);
if (firstBytes !== secondBytes) {
  const differences = [];
  const collectDifferences = (left, right, path = "") => {
    if (differences.length >= 12 || JSON.stringify(left) === JSON.stringify(right)) return;
    if (Array.isArray(left) && Array.isArray(right)) {
      if (left.length !== right.length) differences.push(`${path || "/"}: lengths ${left.length} vs ${right.length}`);
      for (let index = 0; index < Math.min(left.length, right.length); index++) {
        collectDifferences(left[index], right[index], `${path}/${index}`);
      }
    } else if (left && right && typeof left === "object" && typeof right === "object") {
      for (const key of new Set([...Object.keys(left), ...Object.keys(right)])) {
        collectDifferences(left[key], right[key], `${path}/${key}`);
      }
    } else {
      differences.push(`${path || "/"}: ${JSON.stringify(left)} vs ${JSON.stringify(right)}`);
    }
  };
  collectDifferences(first, second);
  throw new Error(`pinned Pi oracle was not stable across two executions: ${differences.join("; ")}`);
}
const sha256 = createHash("sha256").update(firstBytes).digest("hex");
await writeFile(join(output, "pi-oracle.json"), `${JSON.stringify({
  piSha: PIN,
  scenario: first.scenario,
  stable: true,
  normalizedSha256: sha256,
  runs: ["pi-run-1.json", "pi-run-2.json"],
}, null, 2)}\n`);
console.log(`Stable pinned-Pi observation: ${first.scenario}; SHA-256 ${sha256}`);
