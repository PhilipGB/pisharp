#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { createServer } from "node:http";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piSourceArg, casesArg, outputArg] = process.argv.slice(2);
if (!piSourceArg || !casesArg || !outputArg) {
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts model-thinking-provider-wire-oracle.mjs <pinned-pi> <cases.json> <output.json>");
	process.exit(2);
}

const piSource = resolve(piSourceArg);
const casesPath = resolve(casesArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", piSource, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);
const input = JSON.parse(readFileSync(casesPath, "utf8"));
if (input.pinnedPiSha !== PINNED_PI_SHA) throw new Error(`Case pin mismatch: ${input.pinnedPiSha}`);
const transcript = await import(pathToFileURL(resolve(piSource, "packages/ai/src/utils/transcript.ts")).href);

async function readBody(request) {
	const chunks = [];
	for await (const chunk of request) chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
	return JSON.parse(Buffer.concat(chunks).toString("utf8"));
}

function responseBody(api) {
	if (api.startsWith("google-")) {
		return `data: ${JSON.stringify({ responseId: "oracle-response", candidates: [{ content: { role: "model", parts: [{ text: "ok" }] }, finishReason: "STOP" }] })}\n\n`;
	}
	return "data: [DONE]\n\n";
}

async function runCase(item) {
	const modulePath = resolve(piSource, `packages/ai/src/api/${item.api}.ts`);
	const providerApi = await import(pathToFileURL(modulePath).href);
	const capture = {};
	const server = createServer(async (request, response) => {
		try {
			capture.method = request.method;
			const url = new URL(request.url ?? "/", `http://${request.headers.host}`);
			capture.path = url.pathname;
			capture.query = [...url.searchParams.keys()].sort().reduce((result, key) => {
				result[key] = key.toLowerCase().includes("key") ? "<fixture-key>" : url.searchParams.get(key);
				return result;
			}, {});
			const headers = Object.fromEntries(Object.entries(request.headers)
				.filter(([key]) => ["authorization", "x-api-key", "anthropic-version", "anthropic-beta", "x-goog-api-key", "accept", "content-type"].includes(key.toLowerCase()))
				.map(([key, value]) => [key.toLowerCase(), ["authorization", "x-api-key", "x-goog-api-key"].includes(key.toLowerCase()) ? "<fixture-key>" : value]));
			capture.headers = headers;
			capture.credentialIdentity = "fixture-key";
			capture.body = await readBody(request);
			response.writeHead(200, { "content-type": "text/event-stream" });
			response.end(responseBody(item.api));
		} catch (error) {
			response.writeHead(500, { "content-type": "text/plain" });
			response.end(String(error));
		}
	});
	await new Promise((resolveListen) => server.listen(0, "127.0.0.1", resolveListen));
	const address = server.address();
	const baseUrl = `http://127.0.0.1:${address.port}${item.api.startsWith("openai-") ? "/v1" : item.api === "anthropic-messages" ? "" : "/v1beta"}`;
	const model = {
		id: item.model.id,
		name: item.model.name ?? item.model.id,
		api: item.api,
		provider: item.model.provider,
		baseUrl,
		reasoning: item.model.reasoning,
		input: ["text"],
		cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
		contextWindow: 32768,
		maxTokens: item.model.maxTokens,
		...(item.model.compat ? { compat: item.model.compat } : {}),
		...(item.model.samplingParams ? { samplingParams: item.model.samplingParams } : {}),
		...(item.model.samplingParamsByThinkingLevel ? { samplingParamsByThinkingLevel: item.model.samplingParamsByThinkingLevel } : {}),
	};
	const context = transcript.normalizeContext({
		messages: [
			{ role: "system", content: "fixture system instruction", timestamp: 0 },
			{ role: "user", content: "fixture user request", timestamp: 1 },
		],
		...(item.request.includeTools ? { tools: [{
			name: "lookup",
			description: "Look up a fixture value",
			parameters: { type: "object", properties: { query: { type: "string" } }, required: ["query"], additionalProperties: false },
		}] } : {}),
	});
	const options = {
		apiKey: "fixture-only-secret",
		reasoning: item.request.reasoning === "off" ? undefined : item.request.reasoning,
		maxTokens: item.request.maxTokens,
		temperature: item.request.temperature,
		thinkingBudgets: item.request.thinkingBudgets,
		samplingParams: item.request.samplingParams,
	};
	try {
		for await (const _event of providerApi.streamSimple(model, context, options)) { /* consume the captured local response */ }
	} catch {
		// A completed local request remains valid evidence even if the provider response fixture ends early.
	} finally {
		await new Promise((resolveClose, reject) => server.close((error) => error ? reject(error) : resolveClose()));
	}
	if (!capture.body) throw new Error(`Pi emitted no physical request for ${item.id}`);
	return {
		id: item.id,
		api: item.api,
		pinnedPiSha: actualPiSha,
		platform: `${process.platform}-${process.arch}`,
		oracleImplementation: "direct pinned Pi AI streamSimple provider API against a local HTTP server",
		inputModel: item.model,
		requestOptions: item.request,
		providerOptions: { ...options, apiKey: "<fixture-key>" },
		sourceReferences: item.sourceReferences,
		capture,
	};
}

const cases = [];
for (const item of input.cases) cases.push(await runCase(item));
const report = {
	format: 1,
	pinnedPiSha: actualPiSha,
	oracle: "direct pinned Pi provider API requests captured over local HTTP",
	platform: `${process.platform}-${process.arch}`,
	cases,
};
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
console.log(`Wrote ${cases.length} direct-Pi physical request captures for ${actualPiSha} to ${outputPath}`);
