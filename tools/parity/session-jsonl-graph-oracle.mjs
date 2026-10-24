#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { isDeepStrictEqual } from "node:util";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piArg, casesArg, outputArg] = process.argv.slice(2);
if (!piArg || !casesArg || !outputArg) {
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts session-jsonl-graph-oracle.mjs <pinned-pi> <cases.json> <output.json>");
	process.exit(2);
}

const pi = resolve(piArg);
const inputPath = resolve(casesArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);
const input = JSON.parse(readFileSync(inputPath, "utf8"));
if (input.pinnedPiSha !== PINNED_PI_SHA) throw new Error(`Case pin mismatch: ${input.pinnedPiSha}`);
const { SessionManager } = await import(join(pi, "packages/coding-agent/src/core/session-manager.ts"));

function observe(item) {
	const root = mkdtempSync(join(tmpdir(), "pi-jsonl-graph-oracle-"));
	const path = join(root, `${item.id}.jsonl`);
	const header = { type: "session", version: 3, id: `graph-${item.id}`, timestamp: "2026-10-24T00:00:00.000Z", cwd: root };
	const entries = item.entries.map((entry) => ({
		type: "message",
		id: entry.id,
		parentId: entry.parentId,
		timestamp: "2026-10-24T00:00:01.000Z",
		message: { role: "user", content: entry.content, timestamp: 1792800000000 },
	}));
	const content = `${[header, ...entries].map((entry) => JSON.stringify(entry)).join("\n")}\n`;
	try {
		writeFileSync(path, content);
		const session = SessionManager.open(path, root, root);
		const initialEntries = session.getEntries();
		const result = {
			id: item.id,
			entryCount: initialEntries.length,
			indexedEntryCount: session.getEntryCount(),
			physicalEntries: initialEntries.map((entry) => ({ id: entry.id, parentId: entry.parentId })),
			leafId: session.getLeafId(),
			branch: item.traversal === "skip" ? null : session.getBranch().map((entry) => ({
				id: entry.id,
				parentId: entry.parentId,
				content: entry.type === "message" ? entry.message.content : null,
			})),
			traversal: item.traversal === "skip" ? "skipped-to-avoid-unbounded-cycle-walk" : "completed",
		};
		if (item.append) {
			session.appendMessage({ role: "user", content: "continued", timestamp: 1792800000001 });
			const appended = session.getEntries().at(-1);
			result.appendedParentId = appended.parentId;
		}
		return result;
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
}

const results = input.cases.map((item) => {
	const first = observe(item);
	const second = observe(item);
	if (!isDeepStrictEqual(first, second)) throw new Error(`Pinned graph output differed between runs for ${item.id}`);
	return { ...first, independentRuns: 2, samePiCalibration: true };
});
const report = {
	schema: 1,
	caseId: "jsonl-session-graph-and-selected-leaf",
	pinnedPiSha: actualPiSha,
	inputSha256: createHash("sha256").update(readFileSync(inputPath)).digest("hex"),
	source: "packages/coding-agent/src/core/session-manager.ts:loadEntriesFromFile, SessionManager._buildIndex, SessionManager.getBranch",
	results,
};
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
console.log(`Wrote ${results.length} pinned graph captures for ${actualPiSha} to ${outputPath}`);
