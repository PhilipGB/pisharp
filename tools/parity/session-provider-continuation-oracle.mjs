#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { createServer } from "node:http";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { isDeepStrictEqual } from "node:util";
import { pathToFileURL } from "node:url";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piArg, casesArg, outputArg] = process.argv.slice(2);
if (!piArg || !casesArg || !outputArg) {
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts session-provider-continuation-oracle.mjs <pinned-pi> <cases.json> <output.json>");
	process.exit(2);
}

const pi = resolve(piArg);
const casesPath = resolve(casesArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);
const input = JSON.parse(readFileSync(casesPath, "utf8"));
if (input.pinnedPiSha !== PINNED_PI_SHA) throw new Error(`Case pin mismatch: ${input.pinnedPiSha}`);

const [{ buildSessionContext }, transcript] = await Promise.all([
	import(pathToFileURL(resolve(pi, "packages/coding-agent/src/core/session-manager.ts")).href),
	import(pathToFileURL(resolve(pi, "packages/ai/src/utils/transcript.ts")).href),
]);
const entries = input.sessionRecords.slice(1);
const sessionContext = buildSessionContext(JSON.parse(JSON.stringify(entries)));
const messages = transcript.normalizeContext({
	messages: [...sessionContext.messages, { role: "user", content: input.continuationPrompt, timestamp: input.promptTimestamp }],
});

async function captureApi(item) {
	const capture = {};
	const server = createServer(async (request, response) => {
		const chunks = [];
		for await (const chunk of request) chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
		capture.method = request.method;
		const url = new URL(request.url ?? "/", `http://${request.headers.host}`);
		capture.path = url.pathname;
		capture.headers = Object.fromEntries(Object.entries(request.headers)
			.filter(([key]) => ["authorization", "x-api-key", "anthropic-version", "content-type"].includes(key.toLowerCase()))
			.map(([key, value]) => [key.toLowerCase(), ["authorization", "x-api-key"].includes(key.toLowerCase()) ? "<fixture-key>" : value]));
		capture.body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
		response.writeHead(200, { "content-type": "text/event-stream" });
		response.end(item.api === "anthropic-messages"
			? "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_fixture\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"fixture\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n" +
			  "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
			  "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
			  "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
			  "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":1}}\n\n" +
			  "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
			: "data: [DONE]\n\n");
	});
	await new Promise((resolveListen) => server.listen(0, "127.0.0.1", resolveListen));
	const address = server.address();
	const provider = item.api === "anthropic-messages" ? "anthropic" : "openai";
	const model = {
		id: item.modelId,
		name: item.modelId,
		provider,
		api: item.api,
		baseUrl: `http://127.0.0.1:${address.port}${item.api === "anthropic-messages" ? "" : "/v1"}`,
		input: ["text"],
		cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
		contextWindow: 32768,
		maxTokens: 4096,
		reasoning: false,
	};
	const apiModule = await import(pathToFileURL(resolve(pi, `packages/ai/src/api/${item.api}.ts`)).href);
	try {
		for await (const _event of apiModule.streamSimple(model, messages, { apiKey: "fixture-only-key", maxTokens: 4096 })) { /* drain fixture response */ }
	} catch (error) {
		// The captured request remains useful if the local terminal stream is incomplete.
		capture.streamError = error instanceof Error ? error.stack ?? `${error.name}: ${error.message}` : String(error);
	} finally {
		await new Promise((resolveClose, reject) => server.close((error) => error ? reject(error) : resolveClose()));
	}
	if (!capture.body) throw new Error(`Pinned Pi emitted no request for ${item.api}: ${capture.streamError ?? "no stream error"}`);
	return { api: item.api, modelId: item.modelId, capture };
}

const results = [];
for (const item of input.cases) {
	const first = await captureApi(item);
	const second = await captureApi(item);
	if (!isDeepStrictEqual(first.capture, second.capture)) throw new Error(`Pinned Pi captures differed for ${item.api}`);
	results.push({ ...first, independentRuns: 2, samePiCalibration: true });
}
const report = {
	schema: 1,
	caseId: "completed-tool-history-provider-continuation",
	pinnedPiSha: actualPiSha,
	inputSha256: createHash("sha256").update(readFileSync(casesPath)).digest("hex"),
	source: [
		"packages/coding-agent/src/core/session-manager.ts:buildSessionContext",
		"packages/ai/src/api/openai-responses.ts",
		"packages/ai/src/api/openai-completions.ts",
		"packages/ai/src/api/anthropic-messages.ts",
	],
	savedContextRoles: sessionContext.messages.map((message) => message.role),
	continuationPrompt: input.continuationPrompt,
	samePiCalibration: results.every((item) => item.samePiCalibration),
	results,
};
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
console.log(`Wrote ${results.length} pinned provider continuation captures for ${actualPiSha} to ${outputPath}`);
