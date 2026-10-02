import { writeFileSync } from "node:fs";
import { test } from "vitest";
import { estimateTokens, findCutPoint } from "../../coding-agent/src/core/compaction/compaction.ts";
import { streamSimple } from "../src/compat.ts";
import type { Context, Model } from "../src/types.ts";

interface Scenario {
    id: string;
    systemSupport: boolean;
    toolSupport: boolean;
    messages: Context["messages"];
    oauth?: boolean;
}

test("captures Anthropic inline tool request bodies", async () => {
    const scenarios = JSON.parse(process.env.PISHARP_ANTHROPIC_SCENARIOS!) as Scenario[];
    const results = [];
    for (const scenario of scenarios) {
        const model: Model<"anthropic-messages"> = {
            id: "claude-opus-5", name: "fixture", api: "anthropic-messages", provider: "anthropic",
            baseUrl: "http://127.0.0.1:9", reasoning: false, input: ["text"],
            cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 100000, maxTokens: 16384,
            compat: { supportsMidConvoSystemMessages: scenario.systemSupport, supportsMidConvoToolChanges: scenario.toolSupport }
        };
        let payload: Record<string, unknown> | undefined;
        const stream = streamSimple(model, { messages: scenario.messages }, {
            apiKey: scenario.oauth ? "sk-ant-oat-fixture" : "fixture-key",
            onPayload: (value) => {
                payload = value as Record<string, unknown>;
                throw new Error("Captured local fixture payload");
            }
        });
        await stream.result();
        if (!payload) throw new Error("No Anthropic payload");
        results.push({ id: scenario.id, payload, betas: payload.betas ?? [] });
    }
    writeFileSync(process.env.PISHARP_ANTHROPIC_OUTPUT!, JSON.stringify(results));
});

test("captures restored tool result compaction boundaries", () => {
    const messages = [
        { role: "user", content: "first", timestamp: 0 },
        { role: "assistant", content: [{ type: "text", text: "reply one" }], timestamp: 1 },
        { role: "user", content: "second", timestamp: 2 },
        { role: "assistant", content: [{ type: "toolCall", id: "tool2", name: "read", arguments: {} }], timestamp: 3 },
        { role: "toolResult", content: [{ type: "text", text: "read output" }], toolCallId: "tool2", toolName: "read", isError: false, timestamp: 4 },
        { role: "user", content: "third", timestamp: 5 }
    ];
    const entries = messages.map((message, i) => ({ type: "message", id: String(i), parentId: i ? String(i - 1) : null, timestamp: "1970-01-01T00:00:00.000Z", message }));
    const result = {
        tokens: messages.map(message => estimateTokens(message as any)),
        cuts: [9, 10].map(budget => {
            const cut = findCutPoint(entries as any, 0, entries.length, budget);
            return { budget, firstKeptIndex: cut.firstKeptEntryIndex, historyCount: cut.isSplitTurn ? cut.turnStartIndex : cut.firstKeptEntryIndex };
        })
    };
    writeFileSync(process.env.PISHARP_COMPACTION_OUTPUT!, JSON.stringify(result));
});
