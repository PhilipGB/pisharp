import { writeFileSync } from "node:fs";
import { test } from "vitest";
import type { Model, Message } from "../src/types.ts";
import { stream as streamResponses } from "../src/api/openai-responses.ts";
import { stream as streamCompletions } from "../src/api/openai-completions.ts";
import { stream as streamAnthropic } from "../src/api/anthropic-messages.ts";
import { normalizeContext } from "../src/utils/transcript.ts";

type ScenarioPart =
	| { type: "text"; text: string }
	| { type: "image"; mimeType: string; data: string };
type Scenario = { id: string; parts: ScenarioPart[]; modelInput?: ("text" | "image")[] };

test("captures pinned Pi physical image provider requests", async () => {
	const scenarios = JSON.parse(process.env.PISHARP_PROVIDER_CASES!) as Scenario[];
	const responsesSse =
		"data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_wire\"}}\n\n" +
		"data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_wire\",\"output_index\":0,\"content_index\":0,\"delta\":\"ok\"}\n\n" +
		"data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_wire\",\"status\":\"completed\",\"output\":[]}}\n\ndata: [DONE]\n\n";
	const completionsSse =
		"data: {\"id\":\"chat_wire\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":null}]}\n\n" +
		"data: {\"id\":\"chat_wire\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
	const anthropicSse =
		"event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_wire\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-6\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n" +
		"event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
		"event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
		"event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
		"event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":1}}\n\n" +
		"event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
	const result = [];
	for (const entry of [
		{ api: "openai-responses", provider: "openai", modelId: "gpt-5.4", baseUrl: "https://api.openai.com/v1", run: streamResponses, sse: responsesSse },
		{ api: "openai-completions", provider: "openai", modelId: "gpt-4o-mini", baseUrl: "https://api.openai.com/v1", run: streamCompletions, sse: completionsSse },
		{ api: "anthropic-messages", provider: "anthropic", modelId: "claude-sonnet-4-6", baseUrl: "https://api.anthropic.com", run: streamAnthropic, sse: anthropicSse },
	] as const) {
		for (const scenario of scenarios) {
			const user: Message = {
				role: "user",
				content: scenario.parts.map((part) => part.type === "text"
					? { type: "text" as const, text: part.text }
					: { type: "image" as const, mimeType: part.mimeType, data: part.data }),
				timestamp: 0,
			};
			const context = normalizeContext({ messages: [user] });
			const model = {
				id: entry.modelId,
				name: entry.modelId,
				api: entry.api,
				provider: entry.provider,
				baseUrl: entry.baseUrl,
				input: scenario.modelInput ?? ["text", "image"],
				reasoning: false,
				contextWindow: 128000,
				maxTokens: 4096,
				cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
			} as Model<typeof entry.api>;
			const capture = async () => {
				let captured: { path: string; payload: Record<string, unknown> } | undefined;
				const fetchMock = async (url: string | URL | Request, init?: RequestInit) => {
					captured = { path: new URL(String(url)).pathname, payload: JSON.parse(String(init?.body)) };
					return new Response(entry.sse, { status: 200, headers: { "content-type": "text/event-stream" } });
				};
				const answer = await entry.run(model as never, context, {
					apiKey: "fixture-key",
					fetch: fetchMock as typeof globalThis.fetch,
				} as never).result();
				if (answer.stopReason === "error") throw new Error(`${entry.api}/${scenario.id}: ${answer.errorMessage}`);
				if (!captured) throw new Error(`${entry.api}/${scenario.id}: provider request was not captured`);
				return captured;
			};
			const first = await capture();
			const second = await capture();
			if (JSON.stringify(first) !== JSON.stringify(second)) {
				throw new Error(`${entry.api}/${scenario.id}: request changed across calibration runs`);
			}
			result.push({ api: entry.api, model: entry.modelId, scenario: scenario.id, first, second });
		}
	}
	writeFileSync(process.env.PISHARP_PROVIDER_OUTPUT!, JSON.stringify(result));
});
