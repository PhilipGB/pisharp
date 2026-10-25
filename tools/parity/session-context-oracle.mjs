#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { isDeepStrictEqual } from "node:util";
import { pathToFileURL } from "node:url";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piArg, inputArg, expectedArg, outputArg] = process.argv.slice(2);
if (!piArg || !inputArg || !expectedArg || !outputArg) {
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts session-context-oracle.mjs <pinned-pi> <session.jsonl> <expected-context.json> <output.json>");
	process.exit(2);
}

const pi = resolve(piArg);
const inputPath = resolve(inputArg);
const expectedPath = resolve(expectedArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) {
	throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);
}

const [{ buildSessionContext }, inputText, expected] = await Promise.all([
	import(pathToFileURL(resolve(pi, "packages/coding-agent/src/core/session-manager.ts")).href),
	Promise.resolve(readFileSync(inputPath, "utf8")),
	Promise.resolve(JSON.parse(readFileSync(expectedPath, "utf8"))),
]);
const records = inputText.split(/\r?\n/).filter((line) => line.trim()).map((line) => JSON.parse(line));
if (records[0]?.type !== "session") throw new Error("Context fixture must start with a session header");
const entries = records.slice(1);
const heads = Object.keys(expected);

function observe() {
	return Object.fromEntries(heads.map((head) => [head, buildSessionContext(
		JSON.parse(JSON.stringify(entries)), head,
	)]));
}

const first = observe();
const second = observe();
if (!isDeepStrictEqual(first, second)) throw new Error("Pinned Pi context output changed between independent runs");
const results = heads.map((head) => ({
	head,
	pinnedResult: first[head],
	expectedResult: expected[head],
	matchesExpected: isDeepStrictEqual(first[head], expected[head]),
}));
const report = {
	schema: 1,
	caseId: "session-v3-selected-branch-context",
	pinnedPiSha: actualPiSha,
	input: inputPath,
	inputSha256: createHash("sha256").update(inputText).digest("hex"),
	source: "packages/coding-agent/src/core/session-manager.ts:buildSessionContext",
	independentRuns: 2,
	samePiCalibration: isDeepStrictEqual(first, second),
	results,
	allExpectedResultsMatched: results.every((item) => item.matchesExpected),
};
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
if (!report.allExpectedResultsMatched) throw new Error("Pinned Pi output diverged from the checked fixture");
