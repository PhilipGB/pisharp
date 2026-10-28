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
  const text = message.role === "compactionSummary" || message.role === "branchSummary" ? message.summary
    : typeof content === "string" ? content
      : Array.isArray(content) ? content.filter((item) => item.type === "text").map((item) => item.text).join("")
        : "";
  return { role: message.role, text };
}

async function runErrorTurnUsageOnce() {
  const runFailedTurn = async ({ validUsageTokens, staleCompaction = false, errorUsageTokens = 19_000 }) => {
    const harness = await createHarness({
      models: [{ id: "faux-1", contextWindow: 20_000, maxTokens: 100 }],
      settings: { retry: { enabled: false }, compaction: { enabled: true, reserveTokens: 10_000, keepRecentTokens: 1 } },
    });
    const now = Date.now();
    const model = harness.getModel();
    const providerRequests = [];
    let requestCount = 0;
    try {
      if (staleCompaction) harness.settingsManager.applyOverrides({ compaction: { enabled: false } });
      for (const [role, text, offset, usageTokens] of [
        ["user", "first history", -4_000, null], ["assistant", "answer one", -3_000, 60],
        ["user", "second history", -2_000, null], ["assistant", "answer two", -1_000, 60],
      ]) {
        if (role === "user") {
          harness.sessionManager.appendMessage({ role, content: [{ type: "text", text }], timestamp: now + offset });
        } else {
          harness.sessionManager.appendMessage({
            ...fauxAssistantMessage(text, { timestamp: now + offset }),
            api: model.api,
            provider: model.provider,
            model: model.id,
            usage: { input: usageTokens, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: usageTokens,
              cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
          });
        }
      }
      harness.session.agent.state.messages = harness.sessionManager.buildSessionContext().messages;
      harness.session.agent.streamFunction = (requestModel, context, options) => {
        const isSummaryRequest = context.messages.some((message) => message.role === "system" &&
          plainMessage(message).text.startsWith("You are a context summarization assistant"));
        providerRequests.push({ model: requestModel.id, isSummaryRequest,
          messages: context.messages.map(plainMessage), maxTokens: options?.maxTokens ?? null });
        const stream = createAssistantMessageEventStream();
        queueMicrotask(() => {
          if (isSummaryRequest) {
            stream.push({ type: "done", reason: "stop", message: {
              ...fauxAssistantMessage("compacted history summary", { timestamp: now + 86_400_000 + requestCount }),
              api: requestModel.api,
              provider: requestModel.provider,
              model: requestModel.id,
              usage: { input: 60, output: 12, cacheRead: 0, cacheWrite: 0, totalTokens: 72,
                cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
            } });
            return;
          }
          requestCount++;
          if (requestCount === 1) {
            stream.push({ type: "done", reason: "stop", message: {
              ...fauxAssistantMessage("completed response", {
                timestamp: staleCompaction ? now - 10_000 : now + 1_000,
              }),
              api: requestModel.api,
              provider: requestModel.provider,
              model: requestModel.id,
              usage: { input: validUsageTokens, output: 0, cacheRead: 0, cacheWrite: 0,
                totalTokens: validUsageTokens,
                cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
            } });
            return;
          }
          if (!staleCompaction) harness.settingsManager.applyOverrides({ compaction: { enabled: true } });
          const error = {
            ...fauxAssistantMessage("", { stopReason: "error", errorMessage: "529 overloaded",
              timestamp: now + 2_000 }),
            api: requestModel.api,
            provider: requestModel.provider,
            model: requestModel.id,
            usage: { input: errorUsageTokens, output: 0, cacheRead: 0, cacheWrite: 0,
              totalTokens: errorUsageTokens,
              cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
          };
          stream.push({ type: "error", reason: "error", error });
          stream.end(error);
        });
        return stream;
      };

      await harness.session.prompt("successful prior turn");
      if (staleCompaction) {
        harness.settingsManager.applyOverrides({ compaction: { enabled: false } });
        await harness.session.compact();
        harness.settingsManager.applyOverrides({ compaction: { enabled: true } });
      } else harness.settingsManager.applyOverrides({ compaction: { enabled: false } });
      const compactionCountBeforeError = harness.sessionManager.getEntries()
        .filter(entry => entry.type === "compaction").length;
      try {
        await harness.session.prompt("x".repeat(320));
      } catch {
        // The provider error is an input to Pi's post-response compaction decision.
      }
      const thresholdEvents = harness.events.filter((event) => event.type === "compaction_end" &&
        event.reason === "threshold").map((event) => ({ reason: event.reason, willRetry: event.willRetry ?? false }));
      return {
        providerAttemptCount: requestCount,
        summaryRequestCount: providerRequests.filter(request => request.isSummaryRequest).length,
        thresholdCompactionEvents: thresholdEvents,
        compactionCountBeforeError,
        compactionCount: harness.sessionManager.getEntries().filter(entry => entry.type === "compaction").length,
        providerRequests,
        events: harness.events.map((event) => ({
          type: event.type,
          reason: event.reason ?? null,
          willRetry: event.willRetry ?? null,
          errorMessage: event.errorMessage ?? null,
          messageError: event.message?.errorMessage ?? null,
        })),
        activeMessages: harness.sessionManager.buildSessionContext().messages.map(plainMessage),
      };
    } finally {
      harness.cleanup();
    }
  };

  return {
    scenario: "error-turn-usage",
    errorTurnObservations: {
      lastValidUsageTriggersThreshold: await runFailedTurn({ validUsageTokens: 9_936 }),
      failedResponseUsageDoesNotReplaceLastValid: await runFailedTurn({ validUsageTokens: 1_000 }),
      keptPreCompactionUsageIsIgnored: await runFailedTurn({ validUsageTokens: 19_000, staleCompaction: true }),
    },
  };
}

async function runOnce() {
  if (scenario === "error-turn-usage") return runErrorTurnUsageOnce();
  const contextWindow = scenario === "queued-steering" ? 12_000
    : ["threshold-after-turn", "threshold-equal", "threshold-over", "disabled-setting"].includes(scenario) ? 10_000
    : scenario === "pre-prompt-compaction" ? 1_800
      : ["context-boundary-length", "silent-usage-overflow"].includes(scenario) ? 10_000
        : scenario === "physical-route-change" ? 50_000
          : scenario === "unknown-model-window" ? 0 : scenario === "stale-old-model-overflow" ? 1_000 : 1_000_000;
  const models = scenario === "stale-old-model-overflow"
    ? [{ id: "old-model", contextWindow, maxTokens: 100 }, { id: "new-model", contextWindow: 1_000_000, maxTokens: 100 }]
    : scenario === "physical-route-change"
      ? [{ id: "large", contextWindow: 50_000, maxTokens: 100 }, { id: "small", contextWindow: 10_000, maxTokens: 100 }]
      : [{ id: "faux-1", contextWindow, maxTokens: 100 }];
  let markManualCompactionStarted = () => {};
  const manualCompactionStarted = new Promise((resolve) => { markManualCompactionStarted = resolve; });
  const routeDecisions = [];
  const extensionFactories = scenario === "manual-cancel" ? [
    (pi) => {
      pi.on("session_before_compact", async (event) => await new Promise((resolve) => {
        event.signal.addEventListener("abort", () => resolve({ cancel: true }), { once: true });
        markManualCompactionStarted();
      }));
    },
  ] : scenario === "manual-queue-order" ? [
    (pi) => {
      pi.on("session_before_compact", async (event) => ({
        compaction: {
          summary: "manual queue summary",
          firstKeptEntryId: event.preparation.firstKeptEntryId,
          tokensBefore: event.preparation.tokensBefore,
          details: {},
        },
      }));
    },
  ] : scenario === "queued-steering" ? [
    (pi) => {
      pi.on("session_before_compact", async (event) => {
        pi.sendUserMessage("change direction", { deliverAs: "steer" });
        return { compaction: {
          summary: "compacted history summary",
          firstKeptEntryId: event.preparation.firstKeptEntryId,
          tokensBefore: event.preparation.tokensBefore,
          details: {},
        } };
      });
    },
  ] : scenario === "physical-route-change" ? [
    (pi) => {
      let userRouteCount = 0;
      pi.registerVirtualModel({
        provider: "router",
        id: "auto",
        name: "Auto",
        contextWindow: 50_000,
        maxTokens: 100,
        route(request, ctx) {
          const modelId = request.reason === "direct" ? "large" : userRouteCount++ === 0 ? "large" : "small";
          routeDecisions.push({ reason: request.reason, model: modelId });
          return { model: ctx.modelRegistry.find("faux", modelId), thinkingLevel: "off" };
        },
      });
    },
  ] : undefined;
  const harness = await createHarness({
    models,
    extensionFactories,
    settings: { compaction: { enabled: scenario !== "disabled-setting",
      reserveTokens: scenario === "queued-steering" ? 2_000
        : scenario === "physical-route-change" ? 1_000
        : ["threshold-after-turn", "threshold-equal", "threshold-over"].includes(scenario) ? 100
        : scenario === "pre-prompt-compaction" ? 300
          : ["context-boundary-length", "silent-usage-overflow", "stale-old-model-overflow", "disabled-setting"].includes(scenario)
            ? 0 : 10_000,
      keepRecentTokens: 1 } },
  });
  const providerRequests = [];
  let modelSwitchApplied = false;
  try {
    const model = harness.getModel();
    if (scenario === "physical-route-change") {
      const virtualModel = harness.session.modelRuntime.getModel("router", "auto");
      if (!virtualModel) throw new Error("physical route virtual model was not registered");
      await harness.session.setModel(virtualModel);
    }
    const now = Date.parse("2100-01-01T00:00:00.000Z");
    if (scenario.startsWith("manual-")) {
      harness.settingsManager.applyOverrides({ compaction: { keepRecentTokens: 1 } });
      if (["manual-eligible", "manual-failure", "manual-cancel", "manual-split-turn", "manual-queue-order"].includes(scenario)) {
        for (const [role, text, offset] of [
          ["user", "first", -4_000], ["assistant", "answer one", -3_000],
          ["user", "second", -2_000], ["assistant", "answer two", -1_000],
        ]) {
          if (role === "user") {
            harness.sessionManager.appendMessage({ role, content: [{ type: "text", text }], timestamp: now + offset });
          } else {
            const assistant = {
              ...fauxAssistantMessage(text, { timestamp: now + offset }),
              api: model.api,
              provider: model.provider,
              model: model.id,
              usage: { input: 10, output: 2, cacheRead: 0, cacheWrite: 0, totalTokens: 12,
                cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
            };
            harness.sessionManager.appendMessage(assistant);
          }
        }
        if (scenario === "manual-split-turn") {
          harness.sessionManager.appendMessage({ role: "user", content: [{ type: "text", text: "current request" }],
            timestamp: now - 500 });
          harness.sessionManager.appendMessage({
            ...fauxAssistantMessage("partial answer", { timestamp: now - 250, stopReason: "aborted" }),
            api: model.api,
            provider: model.provider,
            model: model.id,
            usage: { input: 10, output: 2, cacheRead: 0, cacheWrite: 0, totalTokens: 12,
              cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
          });
        }
      } else {
        harness.sessionManager.appendMessage({ role: "user", content: [{ type: "text", text: "only turn" }],
          timestamp: now - 1_000 });
      }
      harness.session.agent.state.messages = harness.sessionManager.buildSessionContext().messages;
      const summaryRequests = [];
      const events = [];
      let summaryResponseCount = 0;
      let queuedPrompt;
      const entriesBefore = harness.sessionManager.getEntries().length;
      harness.session.subscribe((event) => events.push(event));
      if (scenario === "manual-queue-order") {
        harness.session.subscribe((event) => {
          if (event.type === "compaction_end" && event.reason === "manual" && event.result) {
            queuedPrompt = harness.session.prompt("queued after compaction");
          }
        });
      }
      harness.session.agent.streamFunction = (summaryModel, context, options) => {
        if (scenario === "manual-queue-order") {
          providerRequests.push({ messages: context.messages.map(plainMessage) });
          const stream = createAssistantMessageEventStream();
          queueMicrotask(() => stream.push({ type: "done", reason: "stop", message: {
            ...fauxAssistantMessage("queued response"),
            api: summaryModel.api,
            provider: summaryModel.provider,
            model: summaryModel.id,
          } }));
          return stream;
        }
        summaryRequests.push({
          messages: context.messages.map(plainMessage),
          maxTokens: options?.maxTokens ?? null,
          customInstructions: context.customInstructions ?? null,
        });
        summaryResponseCount++;
        if (scenario === "manual-failure") throw new Error("summary provider failed");
        const stream = createAssistantMessageEventStream();
        const summaryText = scenario === "manual-split-turn"
          ? summaryResponseCount === 1 ? "history summary" : "turn prefix summary"
          : "compacted history summary";
        queueMicrotask(() => {
          stream.push({ type: "done", reason: "stop", message: {
            ...fauxAssistantMessage(summaryText),
            api: summaryModel.api,
            provider: summaryModel.provider,
            model: summaryModel.id,
            usage: { input: 10, output: 20, cacheRead: 30, cacheWrite: 40, totalTokens: 100,
              cost: { input: 0.1, output: 0.2, cacheRead: 0.3, cacheWrite: 0.4, total: 1 } },
          } });
        });
        return stream;
      };
      let result = null;
      let error = null;
      try {
        if (scenario === "manual-cancel") {
          const compactPromise = harness.session.compact();
          await manualCompactionStarted;
          harness.session.abortCompaction();
          result = await compactPromise;
        } else {
          result = await harness.session.compact(scenario === "manual-eligible" ? "prioritize files" : undefined);
          if (scenario === "manual-queue-order") {
            if (!queuedPrompt) throw new Error("compaction_end did not start the queued prompt");
            await queuedPrompt;
          }
        }
      } catch (caught) {
        error = String(caught?.message ?? caught);
      }
      const entries = harness.sessionManager.getEntries();
      const firstKeptEntryIndex = result
        ? entries.findIndex((entry) => entry.id === result.firstKeptEntryId) : null;
      return {
        scenario,
        compactionSucceeded: result !== null,
        canonicalStateUnchanged: result === null && entries.length === entriesBefore,
        error,
        result: result ? {
          summary: result.summary,
          firstKeptEntryIndex,
          tokensBefore: result.tokensBefore,
          estimatedTokensAfter: result.estimatedTokensAfter,
          usage: result.usage ?? null,
          details: result.details ?? null,
        } : null,
        summaryRequests,
        compactionCount: entries.filter((entry) => entry.type === "compaction").length,
        entries: entries.map((entry) => entry.type === "compaction"
          ? { type: entry.type, summary: entry.summary, tokensBefore: entry.tokensBefore,
              usage: entry.usage ?? null, details: entry.details ?? null }
          : { type: entry.type, message: entry.type === "message" ? plainMessage(entry.message) : null }),
        providerRequests,
        contextMessages: harness.session.messages.map(plainMessage),
        activeMessages: harness.sessionManager.buildSessionContext().messages.map(plainMessage),
        events: events.map((event) => ({ type: event.type, reason: event.reason ?? null,
          willRetry: event.willRetry ?? null, aborted: event.aborted ?? false,
          error: event.errorMessage ?? event.error ?? null })),
      };
    }
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
        const textValue = scenario === "pre-prompt-compaction" && text === "first" ? "x".repeat(12_000) : text;
        harness.sessionManager.appendMessage({ role, content: [{ type: "text", text: textValue }], timestamp: now + offset });
      } else {
        const assistant = {
          ...fauxAssistantMessage(text, { timestamp: now + offset }),
          api: model.api,
          provider: model.provider,
          model: model.id,
          usage: { input: 60, output: 10, cacheRead: 0, cacheWrite: 0, totalTokens: 70,
            cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
        };
        if (scenario === "pre-prompt-compaction") {
          assistant.usage = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0,
            cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } };
        }
        harness.sessionManager.appendMessage(assistant);
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
      const isSummaryRequest = context.messages.some((message) => message.role === "system" &&
        plainMessage(message).text.startsWith("You are a context summarization assistant"));
      const overflowError = requestNumber === 1 &&
        ["pre-content-overflow", "partial-content-overflow"].includes(scenario);
      const text = scenario === "physical-route-change"
        ? isSummaryRequest ? "compacted history summary" : "completed response"
        : requestNumber === 1
        ? scenario === "partial-content-overflow" ? "partial output"
          : ["silent-usage-overflow", "unknown-model-window", "threshold-after-turn", "threshold-equal", "queued-steering",
            "threshold-over", "disabled-setting", "pre-prompt-compaction"].includes(scenario) ? "completed response"
            : overflowError ? "" : "partial output"
        : requestNumber === 2 || requestNumber === 3 ? "compacted history summary" : "continued response";
      const scenarioText = scenario === "pre-prompt-compaction"
        ? isSummaryRequest ? "compacted history summary" : "completed response"
        : text;
      const stopReason = overflowError ? "error" :
        scenario === "physical-route-change" ? "stop" :
        requestNumber === 1 && scenario === "stale-old-model-overflow" ? "length" :
        requestNumber === 1 && ["silent-usage-overflow", "unknown-model-window", "threshold-after-turn", "queued-steering",
          "threshold-equal", "threshold-over", "disabled-setting", "pre-prompt-compaction"].includes(scenario) ? "stop" :
            requestNumber === 1 || (scenario === "second-length-stop" && requestNumber === 4) ? "length" : "stop";
      const inputTokens = scenario === "physical-route-change"
        ? isSummaryRequest ? 60 : requestModel.id === "large" ? 9_500 : 100
        : requestNumber === 1 && scenario === "context-boundary-length" ? contextWindow
        : requestNumber === 1 && scenario === "stale-old-model-overflow" ? contextWindow
        : requestNumber === 1 && scenario === "threshold-after-turn" ? 9_950
        : requestNumber === 1 && scenario === "queued-steering" ? 10_050
        : requestNumber === 1 && scenario === "threshold-equal" ? 9_884
        : requestNumber === 1 && scenario === "threshold-over" ? 9_885
        : requestNumber === 1 && scenario === "disabled-setting" ? 2_000
        : requestNumber === 1 && scenario === "silent-usage-overflow" ? contextWindow + 1
          : requestNumber === 1 && scenario === "unknown-model-window" ? 20_000
            : requestNumber === 1 ? 80 : 60;
      const outputTokens = requestNumber === 1
        ? (["context-boundary-length", "stale-old-model-overflow"].includes(scenario) || overflowError ? 0 : 16)
        : 12;
      const message = {
          ...fauxAssistantMessage(scenarioText, {
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
    if (scenario === "physical-route-change") {
      await harness.session.prompt("first on large route");
      await harness.session.prompt("next on small route");
    } else await harness.session.prompt("continue");

    return {
      scenario,
      modelSwitchApplied,
      selectedModel: harness.session.model.id,
      routeDecisions,
      providerRequests,
      events: harness.events.map((event) => ({
        type: event.type,
        reason: event.reason ?? null,
        willRetry: event.willRetry ?? null,
        errorMessage: event.errorMessage ?? null,
        messageError: event.message?.errorMessage ?? null,
        summary: event.result?.summary ?? null,
        tokensBefore: event.result?.tokensBefore ?? null,
        usage: event.type === "message_end" && event.message?.role === "assistant" &&
          event.message.stopReason !== "error"
          ? { inputTokens: event.message.usage.input, outputTokens: event.message.usage.output }
          : null,
      })),
      sessionEntries: harness.sessionManager.getEntries().map((entry) => entry.type === "message"
        ? { type: "message", message: plainMessage(entry.message), stopReason: entry.message.stopReason ?? null,
            errorMessage: entry.message.errorMessage ?? null }
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
