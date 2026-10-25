#!/usr/bin/env node

import { execFileSync, spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { isDeepStrictEqual } from "node:util";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const SCENARIOS = ["before-tool-invocation", "during-tool-after-side-effect", "after-tool-result"];
const singleObservation = process.argv[2] === "--observe";
const piArg = process.argv[singleObservation ? 3 : 2];
const scenarioArg = singleObservation ? process.argv[4] : undefined;
const outputArg = process.argv[singleObservation ? 5 : 3];
if (!piArg || !outputArg || (singleObservation && !SCENARIOS.includes(scenarioArg))) {
	console.error("Usage: node session-crash-recovery-pi-oracle.mjs <pinned-pi> <output.json>");
	process.exit(2);
}

const pi = resolve(piArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);

if (!singleObservation) {
	const observations = {};
	const tempFiles = [];
	let samePiCalibration = true;
	try {
		for (const scenario of SCENARIOS) {
			const paths = [1, 2].map((run) => join(tmpdir(), `pi-crash-${scenario}-${process.pid}-${run}.json`));
			tempFiles.push(...paths);
			for (const observationPath of paths)
				execFileSync(process.execPath, [fileURLToPath(import.meta.url), "--observe", pi, scenario, observationPath], { stdio: "inherit" });
			const runs = paths.map((path) => JSON.parse(readFileSync(path, "utf8")));
			const stable = isDeepStrictEqual(runs[0].result, runs[1].result);
			samePiCalibration &&= stable;
			observations[scenario] = { samePiCalibration: stable, result: runs[0].result };
		}
		const report = {
			schema: 1,
			caseId: "pinned-pi-process-crash-during-tool-lifecycle",
			pinnedPiSha: actualPiSha,
			independentRunsPerScenario: 2,
			samePiCalibration,
			source: "packages/coding-agent/src/core/agent-session.ts:tool-call persistence and execution; packages/coding-agent/src/core/session-manager.ts:SessionManager.open/buildSessionContext",
			observations,
			limitations: [
				"Each boundary uses two separate Pi worker processes and a third process to reopen the same JSONL session; the parent sends SIGKILL only after a deterministic filesystem marker.",
				"The before-invocation boundary blocks in Pi's tool_call hook after the assistant toolCall is persisted; the during-tool boundary kills after a side effect and before a toolResult; the after-result boundary kills when the next provider request begins, after the toolResult was persisted.",
				"This establishes ordinary Pi JSONL crash behavior and automatic replay behavior. It does not claim Pi has PiSharp's durable intent/outcome journal or unknown-outcome classification.",
				"Generated model catalogs were absent from the disposable exact-pin checkout; empty catalog modules were used only to resolve imports. Workers register and use a faux provider, and no static catalog behavior is asserted."
			]
		};
		writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
		if (!samePiCalibration) throw new Error("Pinned Pi crash/reopen outcomes differed between independent runs");
		console.log(`Wrote three-boundary, two-run pinned Pi crash evidence to ${outputPath}`);
	} finally {
		for (const path of tempFiles) rmSync(path, { force: true });
	}
	process.exit(0);
}

const scenario = scenarioArg;
const providerSource = readFileSync(join(pi, "packages/ai/src/models.generated.ts"), "utf8");
const providerIds = Array.from(providerSource.matchAll(/from "\.\/providers\/([^"/]+)\.models\.ts";/g), (match) => match[1]);
const providerData = join(pi, "packages/ai/src/providers/data");
mkdirSync(providerData, { recursive: true });
for (const provider of providerIds) {
	const dataPath = join(providerData, `${provider}.json`);
	if (!existsSync(dataPath)) writeFileSync(dataPath, "{}\n");
}
const manifestPath = join(providerData, ".manifest.json");
if (!existsSync(manifestPath)) writeFileSync(manifestPath, '{"generatedAt":"2026-10-24T00:00:00.000Z"}\n');

const root = mkdtempSync(join(tmpdir(), "pi-tool-crash-oracle-"));
const agentDir = join(root, "agent");
mkdirSync(agentDir, { recursive: true });
const sessionPath = join(root, "session.jsonl");
const boundaryPath = join(root, "boundary-reached");
const sideEffectPath = join(root, "side-effects");
const replayPath = join(root, "replay-side-effects");
const restartContextPath = join(root, "restart-provider-context.json");
const crashContextPath = join(root, "crash-provider-context.json");
const workerPath = join(pi, "packages/coding-agent/test", `.session-crash-oracle-${process.pid}.mjs`);
const resolver = pathToFileURL(join(pi, "packages/coding-agent/src/experimental/source-resolver.ts")).href;
writeFileSync(sessionPath, JSON.stringify({
	type: "session",
	version: 3,
	id: "pinned-pi-crash-oracle",
	timestamp: "2026-10-24T00:00:00.000Z",
	cwd: root,
}) + "\n");

const workerSource = String.raw`
import { appendFileSync, writeFileSync } from "node:fs";
import { registerFauxProvider, fauxAssistantMessage, fauxToolCall } from "@earendil-works/pi-ai/compat";
import { Type } from "typebox";
import { AuthStorage } from "../src/core/auth-storage.ts";
import { createAgentSessionFromServices, createAgentSessionRuntime, createAgentSessionServices } from "../src/core/agent-session-runtime.ts";
import { SessionManager } from "../src/core/session-manager.ts";

const [phase, scenario, sessionPath, cwd, agentDir, boundaryPath, sideEffectPath, replayPath, restartContextPath, crashContextPath] = process.argv.slice(2);
const faux = registerFauxProvider({ models: [{ id: "crash-oracle-model", reasoning: true }] });
const blockUntilKilled = () => {
	setInterval(() => {}, 1000);
	return new Promise(() => {});
};
const toolCallResponse = fauxAssistantMessage(fauxToolCall("checkpoint", {}), { stopReason: "toolUse" });
if (phase === "crash") {
	if (scenario === "after-tool-result") {
		faux.setResponses([toolCallResponse, (context) => {
			writeFileSync(crashContextPath, JSON.stringify(context.messages, null, 2));
			writeFileSync(boundaryPath, "next-provider-request");
			return blockUntilKilled();
		}]);
	} else {
		faux.setResponses([toolCallResponse]);
	}
} else {
	faux.setResponses([(context) => {
		writeFileSync(restartContextPath, JSON.stringify(context.messages, null, 2));
		return fauxAssistantMessage("continued after process restart");
	}]);
}

const authStorage = AuthStorage.inMemory();
await authStorage.modify(faux.getModel().provider, async () => ({ type: "api_key", key: "fixture-only" }));
const sessionManager = SessionManager.open(sessionPath, cwd, agentDir);
const createRuntime = async ({ cwd: runtimeCwd, sessionManager: runtimeSessionManager, sessionStartEvent }) => {
	const services = await createAgentSessionServices({
		agentDir,
		authStorage,
		model: faux.getModel(),
		cwd: runtimeCwd,
		resourceLoaderOptions: {
			extensionFactories: [
				(pi) => {
					pi.registerProvider(faux.getModel().provider, {
						baseUrl: faux.getModel().baseUrl,
						apiKey: "fixture-only",
						api: faux.api,
						models: faux.models.map((model) => ({
							id: model.id,
							name: model.name,
							api: model.api,
							reasoning: model.reasoning,
							input: model.input,
							cost: model.cost,
							contextWindow: model.contextWindow,
							maxTokens: model.maxTokens,
						})),
					});
					pi.registerTool({
						name: "checkpoint",
						label: "Checkpoint",
						description: "Writes one side effect and returns a checkpoint result.",
						parameters: Type.Object({}),
						execute: async () => {
							appendFileSync(phase === "crash" ? sideEffectPath : replayPath, "x");
						if (phase === "crash" && scenario === "during-tool-after-side-effect") {
							writeFileSync(boundaryPath, "tool-side-effect");
							return blockUntilKilled();
						}
						return { content: [{ type: "text", text: "checkpoint result" }] };
						},
					});
					pi.on("tool_call", async () => {
						if (phase === "crash" && scenario === "before-tool-invocation") {
							writeFileSync(boundaryPath, "tool-call-hook");
							return blockUntilKilled();
						}
					});
				},
			],
			noSkills: true,
			noPromptTemplates: true,
			noThemes: true,
		},
	});
	const created = await createAgentSessionFromServices({
		services,
		sessionManager: runtimeSessionManager,
		sessionStartEvent,
		model: faux.getModel(),
		thinkingLevel: "off",
	});
	return { ...created, services, diagnostics: services.diagnostics };
};

const runtime = await createAgentSessionRuntime(createRuntime, { cwd, agentDir, sessionManager });
await runtime.session.bindExtensions({});
await runtime.session.prompt(phase === "crash" ? "trigger one checkpoint tool" : "continue after restart");
await runtime.dispose();
faux.unregister();
`;
writeFileSync(workerPath, workerSource);

function launchWorker(phase) {
	const child = spawn(process.execPath, ["--import", resolver, workerPath, phase, scenario, sessionPath, root, agentDir,
		boundaryPath, sideEffectPath, replayPath, restartContextPath, crashContextPath], {
		cwd: join(pi, "packages/coding-agent"),
		env: { ...process.env, PI_CODING_AGENT_DIR: agentDir, PI_OFFLINE: "1" },
		stdio: ["ignore", "pipe", "pipe"],
	});
	const stdout = [];
	const stderr = [];
	child.stdout.on("data", (chunk) => stdout.push(chunk));
	child.stderr.on("data", (chunk) => stderr.push(chunk));
	const exited = new Promise((resolveExit, reject) => {
		child.once("error", reject);
		child.once("exit", (code, signal) => resolveExit({ code, signal,
			stdout: Buffer.concat(stdout).toString("utf8"), stderr: Buffer.concat(stderr).toString("utf8") }));
	});
	return { child, exited };
}

async function waitForFile(path, childState, description) {
	const deadline = Date.now() + 30000;
	while (Date.now() < deadline) {
		if (existsSync(path)) return;
		if (childState.child.exitCode !== null || childState.child.signalCode !== null) {
			const result = await childState.exited;
			throw new Error(`${description} worker exited early (${result.code}/${result.signal}): ${result.stderr}\n${result.stdout}`);
		}
		await new Promise((resolveWait) => setTimeout(resolveWait, 20));
	}
	childState.child.kill("SIGKILL");
	const result = await childState.exited;
	throw new Error(`Timed out waiting for ${description}: ${result.stderr}\n${result.stdout}`);
}

try {
	const first = launchWorker("crash");
	await waitForFile(boundaryPath, first, scenario);
	const crashSnapshotText = readFileSync(sessionPath, "utf8");
	const crashEntries = crashSnapshotText.split(/\r?\n/).filter(Boolean).slice(1).map((line) => JSON.parse(line));
	const toolCallEntry = crashEntries.find((entry) => entry.type === "message" && entry.message?.role === "assistant" &&
		entry.message.content?.some((content) => content.type === "toolCall" && content.name === "checkpoint"));
	const toolResultCountBeforeRestart = crashEntries.filter((entry) => entry.type === "message" &&
		entry.message?.role === "toolResult").length;
	first.child.kill("SIGKILL");
	const firstExit = await first.exited;
	if (firstExit.signal !== "SIGKILL") throw new Error(`Expected a SIGKILL after ${scenario}; got ${firstExit.signal ?? firstExit.code}.`);

	const second = launchWorker("restart");
	const restartTimeout = setTimeout(() => second.child.kill("SIGKILL"), 30000);
	const secondExit = await second.exited;
	clearTimeout(restartTimeout);
	if (secondExit.code !== 0) throw new Error(`Restart worker failed (${secondExit.code}/${secondExit.signal}): ${secondExit.stderr}\n${secondExit.stdout}`);
	const context = JSON.parse(readFileSync(restartContextPath, "utf8"));
	const sideEffectCount = existsSync(sideEffectPath) ? readFileSync(sideEffectPath, "utf8").length : 0;
	const replayed = existsSync(replayPath);
	const toolCallIds = new Set(context.flatMap((message) => message.role === "assistant"
		? (message.content ?? []).filter((content) => content.type === "toolCall").map((content) => content.id) : []));
	const providerContext = context.map((message) => {
		if (message.role === "system") return { role: "system" };
		if (message.role === "user") return { role: "user", text: Array.isArray(message.content)
			? message.content.filter((part) => part.type === "text").map((part) => part.text).join("") : message.content };
		if (message.role === "assistant") return {
			role: "assistant",
			stopReason: message.stopReason,
			toolCalls: (message.content ?? []).filter((content) => content.type === "toolCall")
				.map((content) => ({ name: content.name })),
		};
		if (message.role === "toolResult") return {
			role: "toolResult",
			toolName: message.toolName,
			callIdCorrelated: toolCallIds.has(message.toolCallId),
			isError: message.isError,
			text: (message.content ?? []).filter((part) => part.type === "text").map((part) => part.text).join(""),
		};
		return { role: message.role };
	});
	const report = {
		result: {
			scenario,
			crashBoundary: existsSync(boundaryPath) ? readFileSync(boundaryPath, "utf8") : null,
			crashProcessExit: { code: firstExit.code, signal: firstExit.signal },
			restartProcessExit: { code: secondExit.code, signal: secondExit.signal },
			crashSnapshot: {
				assistantToolCallPersistedBeforeKill: Boolean(toolCallEntry),
				toolResultCount: toolResultCountBeforeRestart,
				entryTypes: crashEntries.map((entry) => entry.type),
			},
			afterRestart: {
				sideEffectCount,
				automaticToolReplayObserved: replayed,
				providerContext,
				continued: true,
			},
		},
	};
	writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
	const expectsResult = scenario === "after-tool-result";
	if (!report.result.crashSnapshot.assistantToolCallPersistedBeforeKill ||
		report.result.crashSnapshot.toolResultCount !== (expectsResult ? 1 : 0) ||
		sideEffectCount !== (scenario === "before-tool-invocation" ? 0 : 1) || replayed || !report.result.afterRestart.continued)
		throw new Error(`Pinned Pi ${scenario} recovery did not preserve the expected checkpoint/no-replay behavior`);
	if (expectsResult && !providerContext.some((message) => message.role === "toolResult" && message.text === "checkpoint result"))
		throw new Error("Pinned Pi did not restore the completed tool result in provider context");
	console.log(`Wrote pinned Pi ${scenario} process-crash observation to ${outputPath}`);
} finally {
	try { rmSync(workerPath, { force: true }); } catch {}
	rmSync(root, { recursive: true, force: true });
}
