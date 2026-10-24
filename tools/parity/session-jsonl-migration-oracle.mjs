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
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts session-jsonl-migration-oracle.mjs <pinned-pi> <cases.json> <output.json>");
	process.exit(2);
}

const pi = resolve(piArg);
const inputPath = resolve(casesArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);
const input = JSON.parse(readFileSync(inputPath, "utf8"));
if (input.pinnedPiSha !== PINNED_PI_SHA) throw new Error(`Case pin mismatch: ${input.pinnedPiSha}`);
const { SessionManager } = await import(resolve(pi, "packages/coding-agent/src/core/session-manager.ts"));

function summarize(session) {
	const entries = session.getEntries();
	const idMap = new Map();
	for (let index = 0; index < entries.length; index++) {
		const entry = entries[index];
		if (entry.type !== "session" && typeof entry.id === "string" && !idMap.has(entry.id)) idMap.set(entry.id, `entry-${index}`);
	}
	const normalizeId = (id) => typeof id === "string" ? idMap.get(id) ?? id : id ?? null;
	return {
		headerVersion: session.getHeader()?.version ?? null,
		leafId: normalizeId(session.getLeafId()),
		entries: entries.map((entry) => ({
			type: entry.type,
			id: normalizeId(entry.id),
			parentId: normalizeId(entry.parentId),
			role: entry.type === "message" ? entry.message?.role ?? null : undefined,
			content: entry.type === "message" ? entry.message?.content ?? null : undefined,
			firstKeptEntryId: entry.type === "compaction" ? normalizeId(entry.firstKeptEntryId) : undefined,
			hasFirstKeptEntryIndex: entry.type === "compaction" ? Object.hasOwn(entry, "firstKeptEntryIndex") : undefined,
		})),
	};
}

function observe(item) {
	const root = mkdtempSync(join(tmpdir(), "pi-jsonl-migration-oracle-"));
	const path = join(root, `${item.id}.jsonl`);
	try {
		const input = item.rawJsonl ?? `${item.entries.map((entry) => JSON.stringify(entry)).join("\n")}\n`;
		writeFileSync(path, input.endsWith("\n") ? input : `${input}\n`);
		const before = readFileSync(path);
		const firstSession = SessionManager.open(path, root, root);
		const afterFirstOpen = readFileSync(path);
		const first = summarize(firstSession);
		const secondSession = SessionManager.open(path, root, root);
		const afterSecondOpen = readFileSync(path);
		const second = summarize(secondSession);
		return {
			id: item.id,
			fileChangedByFirstOpen: !before.equals(afterFirstOpen),
			secondOpenStable: isDeepStrictEqual(first, second),
			secondOpenLeftFileUnchanged: afterFirstOpen.equals(afterSecondOpen),
			state: first,
		};
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
}

const results = input.cases.map((item) => {
	const first = observe(item);
	const second = observe(item);
	if (!isDeepStrictEqual(first, second)) throw new Error(`Pinned migration output differed between runs for ${item.id}`);
	return { ...first, independentRuns: 2, samePiCalibration: true };
});
const report = {
	schema: 1,
	caseId: "jsonl-v1-v3-file-migration-and-reopen",
	pinnedPiSha: actualPiSha,
	inputSha256: createHash("sha256").update(readFileSync(inputPath)).digest("hex"),
	source: "packages/coding-agent/src/core/session-manager.ts:SessionManager.open, _loadEntries, _rewriteFile, migrateV1ToV2, migrateV2ToV3, migrateToCurrentVersion",
	results,
};
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
console.log(`Wrote ${results.length} pinned migration captures for ${actualPiSha} to ${outputPath}`);
