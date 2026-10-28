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
const [{ createHarness }, { fauxAssistantMessage }, { createAssistantMessageEventStream },
  { prepareCompaction, DEFAULT_COMPACTION_SETTINGS, estimateContextTokens, estimateProjectedContextTokens,
    estimateTokens, shouldCompact }, { buildSessionProjection }, { SettingsManager }, { SETTINGS_DEFAULTS }] = await Promise.all([
  importFromPi("packages/coding-agent/test/suite/harness.ts"),
  importFromPi("packages/ai/src/providers/faux.ts"),
  importFromPi("packages/ai/src/utils/event-stream.ts"),
  importFromPi("packages/coding-agent/src/core/compaction/compaction.ts"),
  importFromPi("packages/coding-agent/src/core/session-manager.ts"),
  importFromPi("packages/coding-agent/src/core/settings-manager.ts"),
  importFromPi("packages/coding-agent/src/core/settings-defaults.ts"),
]);

function c3CompactText(text) {
  return text.length <= 256 ? text : `${text.slice(0, 24)}<${text.length} chars>${text.slice(-24)}`;
}

function c3Snapshot(message) {
  const content = Array.isArray(message.content) ? message.content : [];
  const role = message.role === "toolResult" ? "toolResult" : message.role;
  let text;
  if (message.role === "compactionSummary") text = message.summary;
  else if (typeof message.content === "string") text = message.content;
  else text = content.filter(item => item.type === "text").map(item => item.text).join("");
  return {
    role,
    text: c3CompactText(text ?? ""),
    callIds: content.filter(item => item.type === "toolCall").map(item => item.id),
    resultCallIds: message.role === "toolResult" ? [message.toolCallId] : [],
  };
}

function createC3Fixture() {
  const entries = [];
  const byId = new Map();
  let id = 0;
  let parentId = null;
  const append = fields => {
    const entry = { id: `c3-${String(id++).padStart(4, "0")}`, parentId,
      timestamp: "2026-10-28T00:00:00.000Z", ...fields };
    entries.push(entry);
    byId.set(entry.id, entry);
    parentId = entry.id;
    return entry.id;
  };
  const message = value => append({ type: "message", message: value });
  const user = text => message({ role: "user", content: text, timestamp: 1 });
  const assistant = (text, calls = []) => message({
    role: "assistant",
    content: [...(text ? [{ type: "text", text }] : []), ...calls.map(call => ({
      type: "toolCall", id: call.id, name: call.name, arguments: call.arguments,
    }))],
    stopReason: calls.length ? "toolUse" : "stop",
    timestamp: 1,
    api: "anthropic-messages",
    provider: "anthropic",
    model: "fixture",
  });
  const toolResult = (toolCallId, text) => message({
    role: "toolResult", toolCallId, toolName: "read",
    content: [{ type: "text", text }], isError: false, timestamp: 1,
  });
  const compact = (summary, firstKeptEntryId) => append({
    type: "compaction", summary, firstKeptEntryId, tokensBefore: 100,
  });
  const omit = targetId => append({ type: "context_edit", targetId, replacement: null });
  const observe = keepRecentTokens => {
    const plan = prepareCompaction(entries, { ...DEFAULT_COMPACTION_SETTINGS, keepRecentTokens });
    const boundary = plan ? byId.get(plan.firstKeptEntryId) : undefined;
    const contextMessages = buildSessionProjection(entries).messages.map(c3Snapshot);
    const metadata = boundary?.type === "message" ? c3Snapshot(boundary.message) : undefined;
    return {
      planExists: plan !== undefined,
      boundaryRole: metadata?.role ?? null,
      boundaryText: metadata?.text ?? null,
      boundaryIndex: boundary ? entries.indexOf(boundary) : null,
      boundaryCallIds: metadata?.callIds ?? [],
      boundaryResultCallIds: metadata?.resultCallIds ?? [],
      isSplitTurn: plan?.isSplitTurn ?? null,
      previousSummary: plan?.previousSummary ?? null,
      messagesToSummarize: plan?.messagesToSummarize.map(c3Snapshot) ?? [],
      turnPrefixMessages: plan?.turnPrefixMessages.map(c3Snapshot) ?? [],
      contextMessages,
    };
  };
  return { entries, byId, user, assistant, toolResult, compact, omit, observe };
}

function runC3() {
  const cases = {};
  const add = (caseId, fixture, keepRecentTokens) => { cases[caseId] = fixture.observe(keepRecentTokens); };
  let fixture = createC3Fixture();
  fixture.user("older question"); fixture.assistant("older answer");
  fixture.user("U".repeat(80)); fixture.assistant("A".repeat(80));
  add("C3.user-boundary-selection", fixture, 30);

  fixture = createC3Fixture();
  fixture.user("older question"); fixture.assistant("older answer");
  fixture.user("current request"); fixture.assistant("current response");
  add("C3.assistant-boundary-split-turn", fixture, 1);

  fixture = createC3Fixture();
  fixture.user("older question"); fixture.assistant("older answer"); fixture.user("current request");
  fixture.assistant("", [{ id: "call-1", name: "read", arguments: { path: "small.txt" } }]);
  fixture.toolResult("call-1", "short result");
  add("C3.tool-call-result-kept-as-one-group", fixture, 1);

  fixture = createC3Fixture();
  fixture.user("older question"); fixture.assistant("older answer"); fixture.user("current request");
  fixture.assistant("", [{ id: "cycle-1", name: "read", arguments: {} }]);
  fixture.toolResult("cycle-1", "first result");
  fixture.assistant("continue after first tool cycle");
  fixture.assistant("", [{ id: "cycle-2", name: "read", arguments: {} }]);
  fixture.toolResult("cycle-2", "second result");
  add("C3.sequential-tool-cycles", fixture, 1);

  fixture = createC3Fixture();
  fixture.user("older question"); fixture.assistant("older answer"); fixture.user("current request");
  fixture.assistant("", [
    { id: "parallel-1", name: "read", arguments: {} },
    { id: "parallel-2", name: "read", arguments: {} },
  ]);
  fixture.toolResult("parallel-1", "first result"); fixture.toolResult("parallel-2", "second result");
  add("C3.parallel-tool-batch-boundary", fixture, 1);

  fixture = createC3Fixture();
  fixture.user("old history"); fixture.assistant("old answer"); fixture.user("read the large file");
  fixture.assistant("", [{ id: "call-1", name: "read", arguments: { path: "big.txt" } }]);
  fixture.toolResult("call-1", "x".repeat(8000));
  add("C3.oversized-trailing-result-falls-back-before-call", fixture, 1000);

  fixture = createC3Fixture();
  fixture.user("old history"); fixture.assistant("old answer"); fixture.user("current request");
  fixture.assistant("A".repeat(80)); fixture.toolResult("unmatched-result", "R".repeat(2000));
  add("C3.never-cut-at-tool-result", fixture, 100);

  fixture = createC3Fixture();
  fixture.user("old history"); fixture.assistant("old answer"); fixture.user("current request");
  fixture.assistant("", [{ id: "incomplete-call", name: "read", arguments: { path: "pending.txt" } }]);
  add("C3.incomplete-assistant-and-tool-call-retained", fixture, 1);

  fixture = createC3Fixture();
  fixture.user("current request");
  fixture.assistant("", [{ id: "missing-result", name: "read", arguments: { path: "pending.txt" } }]);
  add("C3.missing-tool-result-provider-projection", fixture, 1);

  fixture = createC3Fixture();
  fixture.user("older question"); fixture.assistant("older answer"); fixture.user("current request");
  fixture.assistant("", [{ id: "duplicate-result", name: "read", arguments: {} }]);
  fixture.toolResult("duplicate-result", "first result");
  fixture.toolResult("duplicate-result", "duplicate result");
  fixture.user("new request");
  add("C3.unmatched-or-duplicate-tool-result-handling", fixture, 1);

  fixture = createC3Fixture();
  fixture.user("U".repeat(80)); fixture.assistant("A".repeat(80));
  const secondUser = fixture.user("V".repeat(80)); fixture.assistant("B".repeat(80));
  const firstPlan = prepareCompaction(fixture.entries, { ...DEFAULT_COMPACTION_SETTINGS, keepRecentTokens: 30 });
  if (!firstPlan) throw new Error("C3 initial compaction plan missing");
  fixture.compact("First summary", secondUser);
  fixture.user("U".repeat(120)); fixture.assistant("A".repeat(120));
  add("C3.repeated-compaction-updates-prior-summary", fixture, 40);

  fixture = createC3Fixture();
  fixture.user("U".repeat(80)); fixture.assistant("A".repeat(80));
  const retainedUser = fixture.user("V".repeat(80)); fixture.assistant("B".repeat(80));
  const firstCompactionPlan = prepareCompaction(fixture.entries, { ...DEFAULT_COMPACTION_SETTINGS, keepRecentTokens: 30 });
  if (!firstCompactionPlan) throw new Error("C3 first compaction plan missing");
  fixture.compact("First summary", retainedUser);
  fixture.user("U".repeat(120)); fixture.assistant("A".repeat(120));
  const secondCompactionPlan = prepareCompaction(fixture.entries, { ...DEFAULT_COMPACTION_SETTINGS, keepRecentTokens: 40 });
  if (!secondCompactionPlan) throw new Error("C3 second compaction plan missing");
  fixture.compact("Second summary", secondCompactionPlan.firstKeptEntryId);
  const context = buildSessionProjection(fixture.entries).messages.map(c3Snapshot);
  cases["C3.latest-compaction-controls-context"] = {
    compactionCount: fixture.entries.filter(entry => entry.type === "compaction").length,
    contextMessages: context,
    latestSummaryIsSecond: context.some(message => message.role === "compactionSummary" && message.text === "Second summary"),
    previousSummaryOmitted: context.every(message => message.text !== "First summary"),
    retainedCurrentAnswer: context.some(message => message.text === "A".repeat(120)),
  };

  fixture = createC3Fixture();
  fixture.user("older question"); fixture.assistant("older answer"); fixture.user("U".repeat(100));
  const failedAssistant = fixture.assistant("", [{ id: "failed-call", name: "read", arguments: { path: "current.txt" } }]);
  const failedResultEntry = fixture.toolResult("failed-call", "R".repeat(100));
  fixture.omit(failedAssistant); fixture.omit(failedResultEntry);
  add("C3.recovery-omission-suffix-boundary", fixture, 15);

  const empty = createC3Fixture().observe(1);
  fixture = createC3Fixture(); fixture.user("only one user message");
  const oneUser = fixture.observe(1);
  cases["C3.empty-or-insufficient-history-is-no-op"] = {
    emptyPlanExists: empty.planExists,
    oneUserPlanExists: oneUser.planExists,
  };
  return { scenario: "c3", cases };
}

function c4Usage(input, output, cacheRead = 0, cacheWrite = 0, totalTokens = input + output + cacheRead + cacheWrite) {
  return { input, output, cacheRead, cacheWrite, totalTokens,
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } };
}

function c4Assistant(text, usage, stopReason = "stop") {
  return { role: "assistant", content: [{ type: "text", text }], usage, stopReason, timestamp: 1,
    api: "anthropic-messages", provider: "provider", model: "family/model" };
}

function c4Fixture() {
  const entries = [];
  let id = 0;
  let parentId = null;
  const append = fields => {
    const entry = { id: `c4-${String(id++).padStart(4, "0")}`, parentId,
      timestamp: "2026-10-28T00:00:00.000Z", ...fields };
    entries.push(entry);
    parentId = entry.id;
    return entry.id;
  };
  const user = text => append({ type: "message", message: { role: "user", content: text, timestamp: 1 } });
  const assistant = message => append({ type: "message", message });
  const edit = targetId => append({ type: "context_edit", targetId, replacement: null });
  const compact = (summary, firstKeptEntryId) => append({ type: "compaction", summary, firstKeptEntryId, tokensBefore: 100 });
  return { entries, user, assistant, edit, compact };
}

function runC4() {
  const cases = {};
  const defaults = SettingsManager.inMemory().getCompactionSettings();
  cases["C4.model-context-window-controls-trigger"] = {
    triggerTokens: 100_000 - defaults.reserveTokens,
    atThreshold: shouldCompact(83_616, 100_000, defaults),
    oneAboveThreshold: shouldCompact(83_617, 100_000, defaults),
    belowThreshold: shouldCompact(80_000, 100_000, defaults),
  };
  const defaultPolicy = { contextWindowTokens: 100_000, ...defaults };
  cases["C4.reserve-and-keep-recent-defaults"] = {
    ...defaultPolicy,
    defaultReserveTokens: SETTINGS_DEFAULTS.compaction.reserveTokens,
    defaultKeepRecentTokens: SETTINGS_DEFAULTS.compaction.keepRecentTokens,
  };
  const modelKey = "provider/family/model";
  const overrideManager = SettingsManager.inMemory({ compaction: {
    reserveTokens: 8192,
    keepRecentTokens: 10_000,
    modelOverrides: { [modelKey]: { reserveTokens: 400_000 } },
  } });
  const selected = overrideManager.getCompactionSettings({ provider: "provider", id: "family/model" });
  const fallback = overrideManager.getCompactionSettings({ provider: "provider", id: "other" });
  cases["C4.provider-model-override-precedence"] = {
    selectedReserveTokens: selected.reserveTokens,
    selectedKeepRecentTokens: selected.keepRecentTokens,
    fallbackReserveTokens: fallback.reserveTokens,
    fallbackKeepRecentTokens: fallback.keepRecentTokens,
  };
  cases["C4.unknown-limit-is-never-invented"] = {
    contextWindowKnown: false,
    piWouldTriggerFromZeroWindow: shouldCompact(1, 0, defaults),
    reserveTokens: defaults.reserveTokens,
  };

  const anchorMessages = [
    { role: "user", content: "previous request", timestamp: 1 },
    c4Assistant("answer", c4Usage(10, 3)),
    { role: "user", content: "pending", timestamp: 1 },
  ];
  const anchor = estimateContextTokens(anchorMessages);
  cases["C4.provider-usage-anchors-current-context"] = {
    hasValidUsageAnchor: anchor.usageTokens === 13,
    usageTokens: anchor.usageTokens,
    trailingTokens: anchor.trailingTokens,
    totalTokens: anchor.tokens,
  };
  const invalidMessages = [
    { role: "user", content: "request", timestamp: 1 },
    c4Assistant("failed", c4Usage(500, 0), "error"),
    c4Assistant("zero usage", c4Usage(0, 0)),
  ];
  const invalid = estimateContextTokens(invalidMessages);
  cases["C4.aborted-error-and-zero-usage-are-not-anchors"] = {
    hasValidUsageAnchor: invalid.lastUsageIndex !== null,
    usageTokens: null,
    estimateFromVisibleMessages: invalid.tokens,
  };
  const trailingMessages = [
    { role: "user", content: "request", timestamp: 1 },
    c4Assistant("answer", c4Usage(90, 10)),
    { role: "user", content: "pending one", timestamp: 1 },
    { role: "user", content: "pending two with more text", timestamp: 1 },
  ];
  const trailing = estimateContextTokens(trailingMessages);
  cases["C4.messages-after-usage-are-estimated"] = {
    usageTokens: trailing.usageTokens,
    trailingTokens: trailing.trailingTokens,
    totalTokens: trailing.tokens,
  };

  const edited = c4Fixture();
  edited.user("request");
  const editedAssistant = edited.assistant(c4Assistant("answer", c4Usage(10, 3)));
  edited.edit(editedAssistant);
  const editedProjection = buildSessionProjection(edited.entries);
  const editEstimate = estimateProjectedContextTokens(editedProjection, edited.entries);
  const compacted = c4Fixture();
  compacted.user("old request");
  compacted.assistant(c4Assistant("old answer", c4Usage(10, 3)));
  const current = compacted.user("current request");
  compacted.assistant(c4Assistant("current answer", c4Usage(15, 2)));
  compacted.compact("current summary", current);
  const compactedEstimate = estimateProjectedContextTokens(buildSessionProjection(compacted.entries), compacted.entries);
  cases["C4.context-edit-and-compaction-invalidate-stale-usage"] = {
    contextEditInvalidatesUsage: editEstimate.lastUsageIndex === null && editEstimate.usageTokens === 0,
    compactionInvalidatesUsage: compactedEstimate.lastUsageIndex === null && compactedEstimate.usageTokens === 0,
  };

  const cache = estimateContextTokens([
    { role: "user", content: "request", timestamp: 1 },
    c4Assistant("answer", c4Usage(7, 3, 20, 4, 0)),
  ]);
  cases["C4.cache-read-and-write-token-fallback"] = { usageTokens: cache.usageTokens, totalTokens: cache.tokens };

  const prompt = "fixture system prompt characters";
  const tool = { name: "custom", description: "fixture", parameters: {
    type: "object", properties: { path: { type: "string" } },
  } };
  const systemEstimate = estimateContextTokens([
    { role: "system", content: prompt, toolsAdded: [tool] },
    { role: "user", content: "user context", timestamp: 1 },
  ]);
  cases["C4.system-prompt-and-tool-declaration-estimate"] = {
    estimate: systemEstimate.tokens,
    systemPromptCharacters: prompt.length,
    toolCount: 1,
    messageCount: 1,
  };

  const imageAndResult = estimateContextTokens([
    { role: "user", content: [
      { type: "image", data: "", mimeType: "image/png" }, { type: "text", text: "image marker" },
    ], timestamp: 1 },
    { role: "toolResult", toolCallId: "large", toolName: "read",
      content: [{ type: "text", text: "x".repeat(8000) }], isError: false, timestamp: 1 },
  ]);
  cases["C4.image-and-large-tool-result-estimate"] = {
    estimate: imageAndResult.tokens,
    imageCharacters: 4800,
    toolResultCharacters: 8000,
  };
  const unicodeText = "π🦊é".repeat(25);
  const heuristic = estimateTokens({ role: "user", content: unicodeText, timestamp: 1 });
  cases["C4.estimate-method-is-explicitly-heuristic-not-tokenizer"] = {
    estimate: heuristic,
    expectedCharacterHeuristic: Math.ceil(unicodeText.length / 4),
    tokenizerOutput: false,
  };
  return { scenario: "c4", cases };
}

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
  if (scenario === "c3") return runC3();
  if (scenario === "c4") return runC4();
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
