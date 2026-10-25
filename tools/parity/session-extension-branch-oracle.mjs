#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import { isDeepStrictEqual } from "node:util";
import { pathToFileURL } from "node:url";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piArg, inputArg, expectedArg, outputArg] = process.argv.slice(2);
if (!piArg || !inputArg || !expectedArg || !outputArg) {
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts session-extension-branch-oracle.mjs <pinned-pi> <session.jsonl> <expected.json> <output.json>");
	process.exit(2);
}

const pi = resolve(piArg);
const inputPath = resolve(inputArg);
const expectedPath = resolve(expectedArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);
const inputText = readFileSync(inputPath, "utf8");
const expected = JSON.parse(readFileSync(expectedPath, "utf8"));
const { SessionManager } = await import(pathToFileURL(resolve(pi, "packages/coding-agent/src/core/session-manager.ts")).href);

function observe(headId) {
	const root = mkdtempSync(join(tmpdir(), "pi-extension-branch-oracle-"));
	const sessionPath = join(root, "session.jsonl");
	try {
		writeFileSync(sessionPath, inputText);
		const session = SessionManager.open(sessionPath, root, root);
		session.branch(headId);
		const branch = session.getBranch();
		return {
			leafId: session.getLeafId(),
			branch: branch.map((entry) => ({ id: entry.id, parentId: entry.parentId, type: entry.type })),
			extensionState: branch.filter((entry) => entry.type === "custom").map((entry) => ({
				id: entry.id,
				customType: entry.customType,
				data: entry.data ?? null,
			})),
			context: session.buildSessionContext().messages.map((message) => ({
				role: message.role,
				content: message.content,
			})),
		};
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
}

function observeAll() {
	return Object.fromEntries(Object.keys(expected).map((headId) => [headId, observe(headId)]));
}

const first = observeAll();
const second = observeAll();
const report = {
	schema: 1,
	caseId: "session-extension-state-selected-branch-native-reload",
	pinnedPiSha: actualPiSha,
	input: inputPath.startsWith(`${process.cwd()}/`) ? relative(process.cwd(), inputPath) : inputPath,
	inputSha256: createHash("sha256").update(inputText).digest("hex"),
	source: "packages/coding-agent/src/core/session-manager.ts:SessionManager.open/branch/getBranch/buildSessionContext; packages/coding-agent/test/session-manager/save-entry.test.ts",
	independentRuns: 2,
	samePiCalibration: isDeepStrictEqual(first, second),
	results: first,
	allExpectedResultsMatched: isDeepStrictEqual(first, expected),
};
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
if (!report.samePiCalibration || !report.allExpectedResultsMatched)
	throw new Error("Pinned Pi extension-state branch projection was unstable or differed from the fixture");
console.log(`Wrote pinned Pi extension-state branch evidence for ${actualPiSha} to ${outputPath}`);
