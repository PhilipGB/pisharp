import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fauxAssistantMessage } from "@earendil-works/pi-ai/compat";
import type { Model, TranscriptContext } from "../../ai/src/types.ts";
import { stream as streamResponses } from "../../ai/src/api/openai-responses.ts";
import { stream as streamCompletions } from "../../ai/src/api/openai-completions.ts";
import { stream as streamAnthropic } from "../../ai/src/api/anthropic-messages.ts";
import { afterEach, expect, test } from "vitest";
import { SessionManager } from "../src/core/session-manager.ts";
import { createHarness, type Harness } from "./suite/harness.ts";

const tempRoots: string[] = [];
const harnesses: Harness[] = [];

afterEach(() => {
	while (harnesses.length) harnesses.pop()?.cleanup();
	while (tempRoots.length) {
		const root = tempRoots.pop();
		if (root) rmSync(root, { recursive: true, force: true });
	}
});

test("captures provider-wire continuation after reopening image session", async () => {
	const root = mkdtempSync(join(tmpdir(), "pi-multimodal-session-wire-"));
	tempRoots.push(root);
	const inactiveImage = process.env.PISHARP_SESSION_WIRE_INACTIVE_IMAGE!;
	const activeImage = process.env.PISHARP_SESSION_WIRE_ACTIVE_IMAGE!;
	const firstManager = SessionManager.create(root, join(root, "sessions"));
	const first = await createHarness({ sessionManager: firstManager });
	harnesses.push(first);
	first.setResponses([
		fauxAssistantMessage("base received"),
		fauxAssistantMessage("inactive received"),
		fauxAssistantMessage("active received"),
	]);
	await first.session.prompt("base");
	const baseAssistant = firstManager.getEntries().find(
		(entry) => entry.type === "message" && entry.message.role === "assistant",
	);
	if (!baseAssistant) throw new Error("Base turn has no assistant message to anchor the alternate branch");
	await first.session.prompt("inactive image", {
		images: [{ type: "image", data: inactiveImage, mimeType: "image/png" }],
	});
	const branchResult = await first.session.navigateTree(baseAssistant.id, { summarize: false });
	if (branchResult.cancelled) throw new Error("Could not select the active branch anchor");
	await first.session.prompt("inspect λ", {
		images: [{ type: "image", data: activeImage, mimeType: "image/png" }],
	});
	const persistedBranchImages = firstManager.getEntries().flatMap((entry) => {
		if (entry.type !== "message" || entry.message.role !== "user" || typeof entry.message.content === "string")
			return [];
		return entry.message.content.filter((part) => part.type === "image").map((part) => part.data);
	});
	expect(persistedBranchImages).toEqual([inactiveImage, activeImage]);
	const sessionFile = firstManager.getSessionFile();
	if (!sessionFile) throw new Error("Session manager did not create a durable session");
	first.cleanup();
	harnesses.pop();

	const reopened = await createHarness({ sessionManager: SessionManager.open(sessionFile) });
	harnesses.push(reopened);
	let continuationContext: TranscriptContext | undefined;
	reopened.setResponses([
		(context) => {
			continuationContext = context;
			return fauxAssistantMessage("continued");
		},
	]);
	await reopened.session.prompt("continue");
	if (!continuationContext) throw new Error("Reopened session did not produce provider context");

	const captures: Array<{ api: string; model: string; path: string; payload: Record<string, unknown> }> = [];
	for (const entry of [
		{ api: "openai-responses", provider: "openai", modelId: "gpt-5.4", baseUrl: "https://api.openai.com/v1", run: streamResponses, sse: responsesSse },
		{ api: "openai-completions", provider: "openai", modelId: "gpt-4o-mini", baseUrl: "https://api.openai.com/v1", run: streamCompletions, sse: completionsSse },
		{ api: "anthropic-messages", provider: "anthropic", modelId: "claude-sonnet-4-6", baseUrl: "https://api.anthropic.com", run: streamAnthropic, sse: anthropicSse },
	] as const) {
		let captured: { path: string; payload: Record<string, unknown> } | undefined;
		const fetchMock = async (url: string | URL | Request, init?: RequestInit) => {
			captured = { path: new URL(String(url)).pathname, payload: JSON.parse(String(init?.body)) };
			return new Response(entry.sse, { status: 200, headers: { "content-type": "text/event-stream" } });
		};
		const model = {
			id: entry.modelId,
			name: entry.modelId,
			api: entry.api,
			provider: entry.provider,
			baseUrl: entry.baseUrl,
			input: ["text", "image"],
			reasoning: false,
			contextWindow: 128000,
			maxTokens: 4096,
			cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
		} as Model<typeof entry.api>;
		const answer = await entry.run(model as never, continuationContext, {
			apiKey: "fixture-key",
			fetch: fetchMock as typeof globalThis.fetch,
		} as never).result();
		if (answer.stopReason === "error") throw new Error(`${entry.api}: ${answer.errorMessage}`);
		if (!captured) throw new Error(`${entry.api}: continuation provider request was not captured`);
		captures.push({ api: entry.api, model: entry.modelId, ...captured });
	}
	writeFileSync(process.env.PISHARP_SESSION_WIRE_OUTPUT!, JSON.stringify(captures));
	expect(captures).toHaveLength(3);
});

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
