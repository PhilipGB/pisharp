#!/usr/bin/env node

import { execFileSync, spawn } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { isDeepStrictEqual } from "node:util";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piArg, outputArg] = process.argv.slice(2);
if (!piArg || !outputArg) {
	console.error("Usage: node session-rpc-projection-pi-oracle.mjs <pinned-pi> <output.json>");
	process.exit(2);
}
const pi = resolve(piArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);

function fixture(cwd) {
	const header = { type: "session", version: 3, id: "rpc-session-projection", timestamp: "2025-01-01T00:00:00.000Z", cwd };
	const entries = [
		{ type: "message", id: "root", parentId: null, timestamp: "2025-01-01T00:00:01.000Z", message: { role: "user", content: "root prompt", timestamp: 1735689601000 } },
		{ type: "message", id: "later", parentId: "root", timestamp: "2025-01-01T00:00:03.000Z", message: { role: "assistant", content: "later branch", timestamp: 1735689603000 } },
		{ type: "message", id: "selected", parentId: "root", timestamp: "2025-01-01T00:00:02.000Z", message: { role: "user", content: "selected branch", timestamp: 1735689602000 } },
		{ type: "label", id: "label-one", parentId: "selected", timestamp: "2025-01-01T00:00:04.000Z", targetId: "selected", label: "checkpoint" },
		{ type: "label", id: "label-two", parentId: "label-one", timestamp: "2025-01-01T00:00:05.000Z", targetId: "selected", label: "latest label" },
		{ type: "thinking_level_change", id: "thinking-off", parentId: "label-two", timestamp: "2025-01-01T00:00:06.000Z", thinkingLevel: "off" },
	];
	return { header, entries, jsonl: [header, ...entries].map((entry) => JSON.stringify(entry)).join("\n") + "\n" };
}

function messageText(content) {
	if (typeof content === "string") return content;
	if (!Array.isArray(content)) return "";
	return content.filter((part) => part.type === "text").map((part) => part.text).join("");
}

function projectTree(nodes) {
	return nodes.map((node) => ({
		id: node.entry.id,
		type: node.entry.type,
		label: node.label ?? null,
		labelTimestamp: node.labelTimestamp ?? null,
		children: projectTree(node.children ?? []),
	}));
}

function normalizeResponse(record) {
	const data = record.data;
	switch (record.command) {
		case "get_state":
			return {
				modelId: data.model?.id,
				provider: data.model?.provider,
				thinkingLevel: data.thinkingLevel,
				isStreaming: data.isStreaming,
				isCompacting: data.isCompacting,
				sessionId: data.sessionId,
				messageCount: data.messageCount,
				pendingMessageCount: data.pendingMessageCount,
			};
		case "get_messages":
			return data.messages.map((message) => ({ role: message.role, text: messageText(message.content) }));
		case "get_entries":
			return {
				leafId: data.leafId,
				entries: data.entries.map((entry) => ({
					id: entry.id,
					parentId: entry.parentId ?? null,
					type: entry.type,
					...(entry.message ? { role: entry.message.role, text: messageText(entry.message.content) } : {}),
					...(entry.type === "label" ? { targetId: entry.targetId, label: entry.label } : {}),
					...(entry.type === "thinking_level_change" ? { thinkingLevel: entry.thinkingLevel } : {}),
				})),
			};
		case "get_tree":
			return { leafId: data.leafId, tree: projectTree(data.tree) };
		case "get_fork_messages":
			return { messages: data.messages.map((message) => ({ entryId: message.entryId, text: message.text })) };
		default:
			throw new Error(`Unexpected Pi RPC response command: ${record.command}`);
	}
}

async function observe() {
	const root = mkdtempSync(join(tmpdir(), "pi-rpc-projection-"));
	const agentDir = join(root, "agent");
	const sessionPath = join(root, "session.jsonl");
	const session = fixture(root);
	writeFileSync(sessionPath, session.jsonl);
	const resolver = pathToFileURL(join(pi, "packages/coding-agent/src/experimental/source-resolver.ts")).href;
	const cli = join(pi, "packages/coding-agent/src/cli.ts");
	const child = spawn(process.execPath, ["--import", resolver, cli, "--mode", "rpc", "--session", sessionPath], {
		cwd: pi,
		env: { ...process.env, PI_CODING_AGENT_DIR: agentDir, PI_OFFLINE: "1" },
		stdio: ["pipe", "pipe", "pipe"],
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
	const commands = ["get_state", "get_messages", "get_entries", "get_tree", "get_fork_messages"];
	child.stdin.end(commands.map((command, index) => JSON.stringify({ id: `rpc-${index}`, type: command })).join("\n") + "\n");
	const timeout = setTimeout(() => child.kill("SIGKILL"), 15000);
	try {
		const exit = await exited;
		if (exit.code !== 0) throw new Error(`Pinned Pi RPC exited ${exit.code}/${exit.signal}: ${exit.stderr}\n${exit.stdout}`);
		if (exit.stderr) throw new Error(`Pinned Pi RPC wrote stderr: ${exit.stderr}`);
		const records = exit.stdout.split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line));
		const responses = new Map(records.filter((record) => record.type === "response").map((record) => [record.command, record]));
		const normalized = {};
		for (const command of commands) {
			const response = responses.get(command);
			if (!response?.success) throw new Error(`Pinned Pi RPC ${command} failed: ${JSON.stringify(response)}`);
			normalized[command] = normalizeResponse(response);
		}
		return normalized;
	} finally {
		clearTimeout(timeout);
		rmSync(root, { recursive: true, force: true });
	}
}

const first = await observe();
const second = await observe();
const samePiCalibration = isDeepStrictEqual(first, second);
const report = {
	schema: 1,
	caseId: "pinned-pi-rpc-session-state-entry-tree-and-fork-projection",
	pinnedPiSha: actualPiSha,
	source: "packages/coding-agent/src/modes/rpc/rpc-mode.ts:get_state/get_messages/get_entries/get_tree/get_fork_messages; packages/coding-agent/src/core/session-manager.ts:getEntries/getTree/buildSessionContext",
	independentRuns: 2,
	samePiCalibration,
	fixture: {
		sessionId: "rpc-session-projection",
		entryIds: ["root", "later", "selected", "label-one", "label-two", "thinking-off"],
		selectedLeaf: "thinking-off",
		branchLabel: "latest label",
	},
	result: first,
	limitations: ["The CLI is queried without provider credentials; get_state therefore reports Pi's unknown model fallback. The capture verifies persisted session state and RPC projections, not provider transport."],
};
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
if (!samePiCalibration) throw new Error("Pinned Pi RPC projections differed between independent runs");
console.log(`Wrote two-run pinned Pi RPC session projection to ${outputPath}`);
