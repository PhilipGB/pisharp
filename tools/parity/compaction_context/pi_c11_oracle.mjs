#!/usr/bin/env node
import { mkdir, writeFile } from "node:fs/promises";
import { resolve, join } from "node:path";
import { pathToFileURL } from "node:url";
import { execFileSync } from "node:child_process";

const PIN = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const args = process.argv.slice(2);
const value = name => {
  const index = args.indexOf(name);
  if (index < 0 || !args[index + 1]) throw new Error(`missing ${name}`);
  return args[index + 1];
};
const piRoot = resolve(value("--pi"));
const output = resolve(value("--output"));
const head = execFileSync("git", ["rev-parse", "HEAD"], { cwd: piRoot, encoding: "utf8" }).trim();
if (head !== PIN) throw new Error(`Pi checkout is ${head}; expected ${PIN}`);
const fromPi = relative => import(pathToFileURL(join(piRoot, relative)).href);
const [{ createHarness }, { fauxAssistantMessage }, { createAssistantMessageEventStream },
  { getModel, normalizeContext }, { stream: streamResponses }, { stream: streamCompletions },
  { stream: streamAnthropic }, { convertToLlm }, { toJsonEvent }] = await Promise.all([
  fromPi("packages/coding-agent/test/suite/harness.ts"),
  fromPi("packages/ai/src/providers/faux.ts"),
  fromPi("packages/ai/src/utils/event-stream.ts"),
  fromPi("packages/ai/src/compat.ts"),
  fromPi("packages/ai/src/api/openai-responses.ts"),
  fromPi("packages/ai/src/api/openai-completions.ts"),
  fromPi("packages/ai/src/api/anthropic-messages.ts"),
  fromPi("packages/coding-agent/src/core/messages.ts"),
  fromPi("packages/coding-agent/src/modes/json-event.ts"),
]);

const CUSTOM_INSTRUCTIONS = "preserve release decisions";
const SUMMARY = "compacted release summary";
const CONTINUATION = "continue after compaction";

function messageText(message) {
  if (message.role === "compactionSummary" || message.role === "branchSummary") return message.summary ?? "";
  if (typeof message.content === "string") return message.content;
  return (message.content ?? []).filter(item => item.type === "text").map(item => item.text ?? "").join("");
}

function plainMessage(message) {
  return { role: message.role, text: messageText(message) };
}

function seededMessage(role, text, timestamp) {
  return role === "user"
    ? { role, content: [{ type: "text", text }], timestamp }
    : { ...fauxAssistantMessage(text, { timestamp }), timestamp };
}

function fakeResponse(model, text, withUsage = true) {
  const message = {
    ...fauxAssistantMessage(text),
    api: model.api,
    provider: model.provider,
    model: model.id,
  };
  if (withUsage) message.usage = {
    input: 24, output: 5, cacheRead: 0, cacheWrite: 0, totalTokens: 29,
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
  };
  const stream = createAssistantMessageEventStream();
  queueMicrotask(() => stream.push({ type: "done", reason: "stop", message }));
  return stream;
}

function c8ProviderResponse(api) {
  const body = api === "openai-responses"
    ? [
        `data: ${JSON.stringify({ type: "response.created", response: { id: "resp_c11", object: "response", created_at: 1, model: "fixture-model", status: "in_progress", output: [] } })}`,
        `data: ${JSON.stringify({ type: "response.output_text.delta", item_id: "msg_c11", output_index: 0, content_index: 0, delta: "continued" })}`,
        `data: ${JSON.stringify({ type: "response.completed", response: { id: "resp_c11", object: "response", created_at: 1, model: "fixture-model", status: "completed", output: [], usage: { input_tokens: 1, output_tokens: 1, total_tokens: 2 } } })}`,
        "data: [DONE]",
      ].join("\n\n") + "\n\n"
    : api === "openai-completions"
      ? [
          `data: ${JSON.stringify({ id: "chat_c11", object: "chat.completion.chunk", created: 1, model: "fixture-model", choices: [{ index: 0, delta: { role: "assistant", content: "continued" }, finish_reason: null }] })}`,
          `data: ${JSON.stringify({ id: "chat_c11", object: "chat.completion.chunk", created: 1, model: "fixture-model", choices: [{ index: 0, delta: {}, finish_reason: "stop" }] })}`,
          "data: [DONE]",
        ].join("\n\n") + "\n\n"
      : [
          `event: message_start\ndata: ${JSON.stringify({ type: "message_start", message: { id: "msg_c11", type: "message", role: "assistant", model: "fixture-model", content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 1, output_tokens: 0 } } })}`,
          `event: content_block_start\ndata: ${JSON.stringify({ type: "content_block_start", index: 0, content_block: { type: "text", text: "" } })}`,
          `event: content_block_delta\ndata: ${JSON.stringify({ type: "content_block_delta", index: 0, delta: { type: "text_delta", text: "continued" } })}`,
          `event: content_block_stop\ndata: ${JSON.stringify({ type: "content_block_stop", index: 0 })}`,
          `event: message_delta\ndata: ${JSON.stringify({ type: "message_delta", delta: { stop_reason: "end_turn", stop_sequence: null }, usage: { output_tokens: 1 } })}`,
          `event: message_stop\ndata: ${JSON.stringify({ type: "message_stop" })}`,
        ].join("\n\n") + "\n\n";
  return new Response(body, { status: 200, headers: { "content-type": "text/event-stream" } });
}

async function captureProviderRequest(api, messages) {
  const provider = api === "anthropic-messages" ? "anthropic" : "openai";
  const base = getModel(provider, provider === "anthropic" ? "claude-sonnet-4-6" : "gpt-4o-mini");
  if (!base) throw new Error(`missing pinned Pi provider fixture for ${provider}`);
  const model = {
    ...base,
    api,
    id: "fixture-model",
    baseUrl: api === "anthropic-messages" ? "http://127.0.0.1" : "http://127.0.0.1/v1",
  };
  let captured;
  const fetch = async (input, init = {}) => {
    const request = input instanceof Request ? input.clone() : new Request(input, init);
    const body = typeof init.body === "string" ? init.body : await request.text();
    captured = { path: new URL(request.url).pathname, request: JSON.parse(body) };
    return c8ProviderResponse(api);
  };
  const stream = (api === "openai-responses" ? streamResponses
    : api === "openai-completions" ? streamCompletions : streamAnthropic)(
    model, normalizeContext({ messages: convertToLlm(messages) }), { apiKey: "fixture-key", fetch });
  await stream.result();
  if (!captured) throw new Error(`pinned Pi did not issue the ${api} request`);
  const serialized = JSON.stringify(captured.request);
  return {
    api,
    path: captured.path,
    containsSummary: serialized.includes(SUMMARY),
    containsRetainedHistory: serialized.includes("RETAINED ANSWER"),
    containsContinuation: serialized.includes(CONTINUATION),
    containsOlderRawHistory: serialized.includes("OLDER ANSWER"),
  };
}

async function runManualCompaction() {
  const harness = await createHarness({ settings: { compaction: { keepRecentTokens: 1 } } });
  try {
    const manager = harness.sessionManager;
    const now = Date.parse("2100-01-01T00:00:00.000Z");
    manager.appendMessage(seededMessage("user", "OLDER QUESTION", now));
    manager.appendMessage(seededMessage("assistant", "OLDER ANSWER", now + 1));
    manager.appendMessage(seededMessage("user", "RETAINED QUESTION", now + 2));
    manager.appendMessage(seededMessage("assistant", "RETAINED ANSWER", now + 3));
    harness.session.agent.state.messages = manager.buildSessionContext().messages;
    const summaryRequests = [];
    const events = [];
    let summaryResponseCount = 0;
    harness.session.subscribe(event => events.push(toJsonEvent(event)));
    harness.session.agent.streamFunction = (model, context) => {
      summaryRequests.push({
        messages: context.messages.map(plainMessage),
        customInstructions: context.customInstructions ?? null,
      });
      const text = ++summaryResponseCount === 1 ? SUMMARY : "continued response";
      return fakeResponse(model, text);
    };
    const result = await harness.session.compact(CUSTOM_INSTRUCTIONS);
    const context = harness.session.messages;
    const activeEntries = manager.getBranch();
    const apiMessages = [
      ...context,
      { role: "user", content: [{ type: "text", text: CONTINUATION }] },
    ];
    const providerRequests = [];
    for (const api of ["openai-responses", "openai-completions", "anthropic-messages"])
      providerRequests.push(await captureProviderRequest(api, apiMessages));
    const stats = harness.session.getSessionStats();
    const resultFields = Object.keys(result).sort();
    const compactEvents = events.filter(event => event.type === "compaction_start" || event.type === "compaction_end");
    const contextTexts = context.map(plainMessage).map(message => `${message.role}: ${message.text}`);
    return {
      summaryRequest: summaryRequests[0],
      result,
      resultFields,
      compactEvents,
      context: contextTexts,
      providerRequests,
      usageTotalTokens: stats.tokens.total,
      isCompacting: harness.session.isCompacting,
      canonicalMessageCount: activeEntries.filter(entry => entry.type === "message").length,
      canonicalRetainsOldHistory: activeEntries.some(entry => entry.type === "message" &&
        messageText(entry.message).includes("OLDER ANSWER")),
      activeMessages: context.map(plainMessage),
      outboundHistoryValid: convertToLlm(apiMessages).every(message =>
        message.role !== "toolResult" || typeof message.toolCallId === "string"),
    };
  } finally {
    harness.cleanup();
  }
}

async function runJsonCompaction() {
  const harness = await createHarness({
    models: [{ id: "faux-1", contextWindow: 1800, maxTokens: 100 }],
    settings: { compaction: { reserveTokens: 300, keepRecentTokens: 1 } },
  });
  try {
    const manager = harness.sessionManager;
    const now = Date.parse("2100-01-01T00:00:00.000Z");
    const large = "x".repeat(8_000);
    manager.appendMessage(seededMessage("user", "OLDER QUESTION " + large, now));
    manager.appendMessage(seededMessage("assistant", "OLDER ANSWER " + large, now + 1));
    manager.appendMessage(seededMessage("user", "RETAINED QUESTION", now + 2));
    manager.appendMessage(seededMessage("assistant", "RETAINED ANSWER", now + 3));
    harness.session.agent.state.messages = manager.buildSessionContext().messages;
    const jsonEvents = [];
    const summaryRequests = [];
    harness.session.subscribe(event => jsonEvents.push(toJsonEvent(event)));
    harness.session.agent.streamFunction = (model, context) => {
      const summaryRequest = context.messages.some(message => message.role === "system" &&
        message.content.startsWith("You are a context summarization assistant"));
      if (summaryRequest) summaryRequests.push(context.messages.map(plainMessage));
      return fakeResponse(model, summaryRequest
        ? summaryRequests.length === 1 ? "json compacted summary" : "json continued response"
        : "json continued response");
    };
    await harness.session.prompt(CONTINUATION);
    const start = jsonEvents.find(event => event.type === "compaction_start");
    const end = jsonEvents.find(event => event.type === "compaction_end");
    return {
      startType: start?.type ?? null,
      startReason: start?.reason ?? null,
      endType: end?.type ?? null,
      endReason: end?.reason ?? null,
      resultSummary: end?.result?.summary ?? null,
      firstKeptEntryPresent: typeof end?.result?.firstKeptEntryId === "string",
      aborted: end?.aborted ?? null,
      willRetry: end?.willRetry ?? null,
      summaryRequestCount: summaryRequests.length,
    };
  } finally {
    harness.cleanup();
  }
}

async function runOnce() {
  const manual = await runManualCompaction();
  const jsonMode = await runJsonCompaction();
  const resultFields = manual.resultFields;
  const result = manual.result;
  const compactEvents = manual.compactEvents;
  const context = manual.context;
  const providers = manual.providerRequests;
  const providerHistory = {
    providerContextContainsSummary: context.some(message => message.includes(SUMMARY)),
    providerContextContainsRetainedHistory: context.some(message => message.includes("RETAINED ANSWER")),
    providerContextDropsOldHistory: context.every(message => !message.includes("OLDER ANSWER")),
    canonicalHistoryRetainsOldEntries: manual.canonicalRetainsOldHistory,
    outboundHistoryValid: manual.outboundHistoryValid,
    canonicalMessageCount: manual.canonicalMessageCount,
  };
  return {
    cases: {
      "C11.cli-manual-compact-input-and-result": {
        command: `/compact ${CUSTOM_INSTRUCTIONS}`,
        customInstructionsForwarded: JSON.stringify(manual.summaryRequest).includes(CUSTOM_INSTRUCTIONS),
        succeeded: result.summary.includes(SUMMARY),
        summary: result.summary,
      },
      "C11.tui-compact-command-and-progress-status": {
        commandAccepted: result.summary.includes(SUMMARY),
        progressStarted: compactEvents[0]?.type === "compaction_start",
        progressCleared: compactEvents[1]?.type === "compaction_end",
        manualReason: compactEvents[0]?.reason ?? null,
      },
      "C11.rpc-compact-request-and-result-shape": {
        id: "compact-c11",
        command: "compact",
        success: result.summary.includes(SUMMARY),
        resultFields,
        usageTotalTokens: result.usage?.totalTokens ?? null,
      },
      "C11.rpc-event-order-and-correlation": {
        eventOrder: compactEvents.map(event => event.type),
        responseId: "compact-c11",
        startReason: compactEvents[0]?.reason ?? null,
        endReason: compactEvents[1]?.reason ?? null,
      },
      "C11.rpc-state-context-usage-after-compaction": {
        isCompacting: harnessStateIsCompacting(manual),
        summaryInContext: context.some(message => message.includes(SUMMARY)),
        contextMessageCount: context.length,
        usageTotalTokens: manual.usageTotalTokens,
      },
      "C11.json-mode-compaction-output-contract": jsonMode,
      "C11.responses-wire-request-after-compaction": providers[0],
      "C11.chat-completions-wire-request-after-compaction": providers[1],
      "C11.anthropic-messages-wire-request-after-compaction": providers[2],
      "C11.provider-history-is-valid-and-summary-not-canonical-projection": providerHistory,
    },
  };
}

function harnessStateIsCompacting(manual) {
  return manual.isCompacting;
}

const first = await runOnce();
const second = await runOnce();
if (JSON.stringify(first) !== JSON.stringify(second))
  throw new Error("pinned Pi C11 integration observation changed between executions");
await mkdir(resolve(output, ".."), { recursive: true });
await writeFile(output, `${JSON.stringify(first, null, 2)}\n`, "utf8");
console.log(`Stable pinned-Pi C11 observation written to ${output}`);
