#!/usr/bin/env node
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { mkdtemp } from "node:fs/promises";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";
import { pathToFileURL, fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";
import { spawn } from "node:child_process";

const PIN = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const args = process.argv.slice(2);
const value = (name) => {
	const index = args.indexOf(name);
	if (index < 0 || !args[index + 1]) throw new Error(`missing ${name}`);
	return args[index + 1];
};
const piRoot = resolve(value("--pi"));
const head = execFileSync("git", ["rev-parse", "HEAD"], { cwd: piRoot, encoding: "utf8" }).trim();
if (head !== PIN) throw new Error(`Pi checkout is ${head}; expected ${PIN}`);
const fromPi = (relative) => import(pathToFileURL(resolve(piRoot, relative)).href);
const [{ createHarness }, { userMsg, assistantMsg }, { SessionManager, loadEntriesFromFile }] = await Promise.all([
	fromPi("packages/coding-agent/test/suite/harness.ts"),
	fromPi("packages/coding-agent/test/utilities.ts"),
	fromPi("packages/coding-agent/src/core/session-manager.ts"),
]);

async function seed(harness) {
	const manager = harness.sessionManager;
	manager.appendMessage(userMsg("first question"));
	manager.appendMessage(assistantMsg("first answer"));
	manager.appendMessage(userMsg("retained question"));
	manager.appendMessage(assistantMsg("retained answer"));
	harness.session.agent.state.messages = manager.buildSessionContext().messages;
}

async function observePostSummaryPersistenceFailure() {
	const root = await mkdtemp(join(tmpdir(), "pi-c13-save-failure-"));
	const manager = SessionManager.create(root, join(root, "sessions"));
	let harness;
	try {
		harness = await createHarness({
			sessionManager: manager,
			settings: { compaction: { keepRecentTokens: 1 } },
			extensionFactories: [
				(pi) => pi.on("session_before_compact", (event) => ({
					compaction: {
						summary: "committed before failure",
						firstKeptEntryId: event.preparation.firstKeptEntryId,
						tokensBefore: event.preparation.tokensBefore,
					},
				})),
			],
		});
		await seed(harness);
		const append = manager.appendCompaction.bind(manager);
		manager.appendCompaction = (...parameters) => {
			append(...parameters);
			throw new Error("injected lost acknowledgement after durable append");
		};
		let failureObserved = false;
		try {
			await harness.session.compact();
		} catch (error) {
			failureObserved = error.message.includes("injected lost acknowledgement");
		}
		manager.appendCompaction = append;
		const sessionFile = manager.getSessionFile();
		if (!sessionFile) throw new Error("Pi did not create a session file for the persistence probe");
		const memoryEntries = manager.getEntries().filter((entry) => entry.type === "compaction");
		const reloaded = SessionManager.open(sessionFile);
		const durableEntries = loadEntriesFromFile(sessionFile).filter((entry) => entry.type === "compaction");
		return {
			failureObserved,
			oneDurableCompactionEntry: memoryEntries.length === 1 && durableEntries.length === 1,
			reloadShowsSameEntry: reloaded.getEntries().filter((entry) => entry.type === "compaction")[0]?.id ===
				durableEntries[0]?.id,
			compactEventPublished: harness.eventsOfType("session_compact").length > 0,
		};
	} finally {
		harness?.cleanup();
		await rm(root, { recursive: true, force: true });
	}
}

async function waitForFile(path, child, output, error) {
	const deadline = Date.now() + 20000;
	while (Date.now() < deadline) {
		try {
			return JSON.parse(await readFile(path, "utf8"));
		} catch (exception) {
			if (exception.code !== "ENOENT") throw exception;
		}
		if (child.exitCode !== null || child.signalCode !== null)
			throw new Error(`Pi publication child exited before marker: ${output.join("")} ${error.join("")}`);
		await new Promise((resolve) => setTimeout(resolve, 20));
	}
	child.kill("SIGKILL");
	throw new Error(`timed out waiting for Pi publication marker: ${output.join("")} ${error.join("")}`);
}

async function observeProcessKillDuringPublication() {
	const root = await mkdtemp(join(tmpdir(), "pi-c13-publication-kill-"));
	const marker = join(root, "ready.json");
	const output = [];
	const error = [];
	const child = spawn(process.execPath,
		[fileURLToPath(import.meta.url), "--pi", piRoot, "--publication-child", "--root", root],
		{ cwd: piRoot, stdio: ["ignore", "pipe", "pipe"] });
	child.stdout.on("data", (chunk) => output.push(chunk.toString()));
	child.stderr.on("data", (chunk) => error.push(chunk.toString()));
	try {
		const ready = await waitForFile(marker, child, output, error);
		child.kill("SIGKILL");
		const [code, signal] = await new Promise((resolveExit) => child.once("exit", (exitCode, exitSignal) =>
			resolveExit([exitCode, exitSignal])));
		const reloaded = SessionManager.open(ready.sessionFile);
		const entries = reloaded.getEntries();
		const compactions = entries.filter((entry) => entry.type === "compaction");
		const context = JSON.stringify(reloaded.buildSessionContext().messages);
		return {
			killedBeforeCompactionReturned: code === null && signal === "SIGKILL",
			singleCompactionEntry: compactions.length === 1,
			reloadContainsSummary: compactions.length === 1 && context.includes("killed at publication boundary"),
		};
	} finally {
		if (child.exitCode === null && child.signalCode === null) child.kill("SIGKILL");
		await rm(root, { recursive: true, force: true });
	}
}

if (args.includes("--publication-child")) {
	const root = resolve(value("--root"));
	const sessionManager = SessionManager.create(root, join(root, "sessions"));
	let harness;
	harness = await createHarness({
		sessionManager,
		settings: { compaction: { keepRecentTokens: 1 } },
		extensionFactories: [
			(pi) => {
				pi.on("session_before_compact", (event) => ({
					compaction: {
						summary: "killed at publication boundary",
						firstKeptEntryId: event.preparation.firstKeptEntryId,
						tokensBefore: event.preparation.tokensBefore,
					},
				}));
				pi.on("session_compact", async () => {
					const sessionFile = harness.sessionManager.getSessionFile();
					if (!sessionFile) throw new Error("Pi compact hook has no durable session file");
					await writeFile(join(root, "ready.json"), JSON.stringify({ sessionFile }), "utf8");
					await new Promise(() => {});
				});
			},
		],
	});
	await seed(harness);
	await harness.session.compact();
	throw new Error("Pi compaction unexpectedly returned before the publication process was killed");
}

const outputPath = resolve(value("--output"));
await mkdir(resolve(outputPath, ".."), { recursive: true });
const persistenceFailure = await observePostSummaryPersistenceFailure();
if (!persistenceFailure.failureObserved || !persistenceFailure.oneDurableCompactionEntry ||
	!persistenceFailure.reloadShowsSameEntry || persistenceFailure.compactEventPublished)
	throw new Error(`Pi post-summary persistence boundary was not consistent: ${JSON.stringify(persistenceFailure)}`);
const processKill = await observeProcessKillDuringPublication();
if (!processKill.killedBeforeCompactionReturned || !processKill.singleCompactionEntry || !processKill.reloadContainsSummary)
	throw new Error(`Pi process-kill publication probe failed: ${JSON.stringify(processKill)}`);
const result = {
	cases: {
		"C13.post-summary-prepublish-failure-keeps-one-state": persistenceFailure,
		"C13.persistence-failure-rolls-back-or-recovers-consistently": persistenceFailure,
		"C13.process-kill-during-publication-reloads-valid-state": processKill,
	},
};
await writeFile(outputPath, `${JSON.stringify(result, null, 2)}\n`, "utf8");
console.log(`Pinned Pi C13 failure and publication observations written to ${outputPath}`);
