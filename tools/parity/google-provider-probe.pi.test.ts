import { writeFileSync } from "node:fs";
import { test } from "vitest";
import { convertMessages } from "../src/api/google-shared.ts";
import type { Context, Model } from "../src/types.ts";
import { normalizeContext } from "../src/utils/transcript.ts";

const cases = [
	{ id: "genai-gemini-2.5", api: "google-generative-ai", provider: "google", model: "gemini-2.5-flash" },
	{ id: "genai-gemini-3.5", api: "google-generative-ai", provider: "google", model: "gemini-3.5-flash" },
	{ id: "genai-gemma-4", api: "google-generative-ai", provider: "google", model: "gemma-4-26b-a4b-it" },
	{ id: "vertex-gemini-2.5", api: "google-vertex", provider: "google-vertex", model: "gemini-2.5-flash" },
	{ id: "vertex-gemini-3.5", api: "google-vertex", provider: "google-vertex", model: "gemini-3.5-flash" },
	{ id: "vertex-gemma-4", api: "google-vertex", provider: "google-vertex", model: "gemma-4-26b-a4b-it" },
] as const;

const image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/bX8AAAAASUVORK5CYII=";

test("captures pinned Google and Vertex transcript image/signature/tool routing", () => {
	const results = cases.map((item) => {
		const model: Model<"google-generative-ai" | "google-vertex"> = {
			id: item.model,
			name: item.model,
			api: item.api,
			provider: item.provider,
			baseUrl: "https://example.test/v1beta",
			reasoning: true,
			input: ["text", "image"],
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
			contextWindow: 128_000,
			maxTokens: 8192,
		};
		const context: Context = normalizeContext({
			messages: [
				{ role: "user", content: [{ type: "text", text: "inspect" }, { type: "image", data: image, mimeType: "image/png" }], timestamp: 0 },
				{
					role: "assistant",
					content: [
						{ type: "thinking", thinking: "checking", thinkingSignature: "QUJDRA==" },
						{ type: "text", text: "", textSignature: "QUJDRA==" },
						{ type: "toolCall", id: "call/one", name: "read", arguments: { path: "image.png" }, thoughtSignature: "QUJDRA==" },
					],
					api: item.api,
					provider: item.provider,
					model: item.model,
					usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
					stopReason: "toolUse",
					timestamp: 1,
				},
				{
					role: "toolResult",
					toolCallId: "call/one",
					toolName: "read",
					content: [{ type: "text", text: "image read" }, { type: "image", data: image, mimeType: "image/png" }],
					isError: false,
					timestamp: 2,
				},
			],
		});
		return { id: item.id, contents: convertMessages(model, context) };
	});
	writeFileSync(process.env.PISHARP_GOOGLE_PROVIDER_OUTPUT!, JSON.stringify(results));
});
