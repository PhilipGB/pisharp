#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { isDeepStrictEqual } from "node:util";
import { pathToFileURL } from "node:url";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piArg, casesArg, outputArg] = process.argv.slice(2);
if (!piArg || !casesArg || !outputArg) {
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts session-jsonl-parser-oracle.mjs <pinned-pi> <cases.json> <output.json>");
	process.exit(2);
}

const pi = resolve(piArg);
const inputPath = resolve(casesArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);
const input = JSON.parse(readFileSync(inputPath, "utf8"));
if (input.pinnedPiSha !== PINNED_PI_SHA) throw new Error(`Case pin mismatch: ${input.pinnedPiSha}`);
const { loadEntriesFromFile } = await import(pathToFileURL(resolve(pi, "packages/coding-agent/src/core/session-manager.ts")).href);

function observe(item) {
	const root = mkdtempSync(join(tmpdir(), "pi-jsonl-parser-oracle-"));
	const path = join(root, `${item.id}.jsonl`);
	const content = item.generatedContent === "oversizedRecord"
		? `${JSON.stringify({ type: "session", version: 3, id: "oversized", cwd: "/tmp" })}\n` +
		  `${JSON.stringify({ type: "message", id: "large", parentId: null, message: { role: "user", content: "x".repeat(item.payloadBytes), timestamp: 1 } })}\n` +
		  `${JSON.stringify({ type: "message", id: "small", parentId: null, message: { role: "user", content: "kept", timestamp: 2 } })}\n`
		: item.content;
	try {
		writeFileSync(path, content);
		let entries;
		let error;
		try {
			entries = loadEntriesFromFile(path).map((entry) => ({
				type: entry && typeof entry === "object" ? entry.type ?? null : typeof entry,
				id: entry && typeof entry === "object" ? entry.id ?? null : null,
			}));
		} catch (caught) {
			error = caught instanceof Error ? { name: caught.name, message: caught.message } : String(caught);
		}
		const output = readFileSync(path);
		return {
			id: item.id,
			entries: entries ?? null,
			error: error ?? null,
			inputBytes: Buffer.byteLength(content),
			inputSha256: createHash("sha256").update(content).digest("hex"),
			outputSha256: createHash("sha256").update(output).digest("hex"),
			appendedNewline: output.length === Buffer.byteLength(content) + 1 && output.at(-1) === 0x0a,
		};
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
}

const results = input.cases.map((item) => {
	const first = observe(item);
	const second = observe(item);
	if (!isDeepStrictEqual(first, second)) throw new Error(`Pinned parser output differed between runs for ${item.id}`);
	return { ...first, independentRuns: 2, samePiCalibration: true };
});
const report = {
	schema: 1,
	caseId: "jsonl-record-recovery-and-newline-repair",
	pinnedPiSha: actualPiSha,
	inputSha256: createHash("sha256").update(readFileSync(inputPath)).digest("hex"),
	source: "packages/coding-agent/src/core/session-manager.ts:loadEntriesFromFile",
	results,
};
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
console.log(`Wrote ${results.length} pinned parser captures for ${actualPiSha} to ${outputPath}`);
