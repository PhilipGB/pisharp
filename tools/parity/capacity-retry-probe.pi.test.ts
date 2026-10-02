import { writeFileSync } from "node:fs";
import { fauxAssistantMessage } from "@earendil-works/pi-ai";
import { it } from "vitest";
import { createHarness, getMessageText } from "./harness.ts";

it("captures current Pi capacity retries through AgentSession", async () => {
    const results = [];
    for (const scenario of JSON.parse(process.env.PISHARP_CAPACITY_SCENARIOS!)) {
        const harness = await createHarness({ tools: [], settings: { retry: {
            enabled: scenario.enabled, maxRetries: scenario.retries,
            baseDelayMs: scenario.cancel ? 100 : 0, maxAgentDelayMs: scenario.cancel ? 100 : 0,
        } } });
        try {
            const events = [];
            const contexts = [];
            harness.session.subscribe((event) => {
                if (event.type === "auto_retry_start") {
                    events.push({ type: event.type, attempt: event.attempt, maxAttempts: event.maxAttempts,
                        delayMs: event.delayMs, errorMessage: event.errorMessage });
                    if (scenario.cancel) queueMicrotask(() => harness.session.abortRetry());
                }
                if (event.type === "auto_retry_end") events.push({ type: event.type, attempt: event.attempt,
                    success: event.success, ...(event.finalError ? { finalError: event.finalError } : {}) });
            });
            const response = (context) => {
                contexts.push(context.messages.filter((message) => message.role === "user" || message.role === "assistant")
                    .map((message) => ({ role: message.role, text: getMessageText(message) })));
                return contexts.length <= scenario.errors
                    ? fauxAssistantMessage("", { stopReason: "error", errorMessage: scenario.message })
                    : fauxAssistantMessage("done");
            };
            harness.setResponses(Array.from({ length: scenario.errors + 1 }, () => response));
            await harness.session.prompt("go");
            results.push({ id: scenario.id, requests: harness.faux.state.callCount, events, contexts,
                completed: harness.session.messages.some((message) => message.role === "assistant" && getMessageText(message) === "done"),
                isRetrying: harness.session.isRetrying });
        } finally { harness.cleanup(); }
    }
    writeFileSync(process.env.PISHARP_CAPACITY_OUTPUT!, JSON.stringify(results));
});
