#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import { isDeepStrictEqual } from "node:util";
import { pathToFileURL } from "node:url";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piArg, inputArg, outputArg, piSharpExportArg] = process.argv.slice(2);
if (!piArg || !inputArg || !outputArg) {
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts session-jsonl-roundtrip-oracle.mjs <pinned-pi> <session.jsonl> <output.json> [pisharp-export.jsonl]");
	process.exit(2);
}

const pi = resolve(piArg);
const inputPath = resolve(inputArg);
const outputPath = resolve(outputArg);
const piSharpExportPath = piSharpExportArg ? resolve(piSharpExportArg) : undefined;
const actualPiSha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);
const inputText = readFileSync(inputPath, "utf8");
const piSharpExportText = piSharpExportPath ? readFileSync(piSharpExportPath, "utf8") : undefined;
const semanticHash = (value) => createHash("sha256").update(JSON.stringify(value)).digest("hex");
const [{ SessionManager }, { serializeSessionBranch }] = await Promise.all([
	import(pathToFileURL(resolve(pi, "packages/coding-agent/src/core/session-manager.ts")).href),
	import(pathToFileURL(resolve(pi, "packages/coding-agent/src/core/session-export.ts")).href),
]);

function project(session) {
	return {
		sessionId: session.getSessionId(),
		leafId: session.getLeafId(),
		branch: session.getBranch().map((entry) => ({
			id: entry.id,
			parentId: entry.parentId,
			type: entry.type,
		})),
		entryRecords: session.getBranch(),
		context: session.buildSessionContext(),
	};
}

function observe() {
	const root = mkdtempSync(join(tmpdir(), "pi-jsonl-roundtrip-oracle-"));
	const sourcePath = join(root, "source.jsonl");
	const exportPath = join(root, "export.jsonl");
	try {
		writeFileSync(sourcePath, inputText);
		const source = SessionManager.open(sourcePath, root, root);
		const before = project(source);
		const exportedText = serializeSessionBranch(source);
		writeFileSync(exportPath, exportedText);
		const exportedEntries = exportedText.split(/\r?\n/).filter((line) => line.trim()).slice(1)
			.map((line) => JSON.parse(line)).map((entry) => ({
				id: entry.id ?? null,
				parentId: entry.parentId ?? null,
				type: entry.type ?? null,
			}));
		const reopened = SessionManager.open(exportPath, root, root);
		const after = project(reopened);
		return {
			before,
			exportedEntries,
			after: { sessionId: after.sessionId, leafId: after.leafId, branch: after.branch },
			afterSemanticSha256: semanticHash(after),
			semanticRoundTrip: isDeepStrictEqual(before, after),
		};
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
}

function observePiSharpExport() {
	const root = mkdtempSync(join(tmpdir(), "pi-jsonl-pisharp-export-oracle-"));
	const sourcePath = join(root, "pisharp-export.jsonl");
	const exportPath = join(root, "pi-export.jsonl");
	try {
		writeFileSync(sourcePath, piSharpExportText);
		const source = SessionManager.open(sourcePath, root, root);
		const before = project(source);
		const exportedText = serializeSessionBranch(source);
		writeFileSync(exportPath, exportedText);
		const reopened = SessionManager.open(exportPath, root, root);
		const after = project(reopened);
		return {
			before,
			after: { sessionId: after.sessionId, leafId: after.leafId, branch: after.branch },
			afterSemanticSha256: semanticHash(after),
			semanticRoundTrip: isDeepStrictEqual(before, after),
		};
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
}

const first = observe();
const second = observe();
const piSharpRuns = piSharpExportText ? [observePiSharpExport(), observePiSharpExport()] : undefined;
const report = {
	schema: 1,
	caseId: "pinned-pi-session-jsonl-export-and-reopen",
	pinnedPiSha: actualPiSha,
	input: inputPath.startsWith(`${process.cwd()}/`) ? relative(process.cwd(), inputPath) : inputPath,
	inputSha256: createHash("sha256").update(inputText).digest("hex"),
	source: "packages/coding-agent/src/core/session-manager.ts:SessionManager.open/getBranch/buildSessionContext; packages/coding-agent/src/core/session-export.ts:serializeSessionBranch",
	independentRuns: 2,
	samePiCalibration: isDeepStrictEqual(first, second),
	semanticRoundTrip: first.semanticRoundTrip,
	result: first,
};
if (piSharpRuns) {
	report.piSharpExport = {
		input: "native session export captured by PiJsonlSessionInterchangeTests.RichPinnedSessionFixtureRoundTripsThroughNativePersistenceAndPiJsonl",
		inputSha256: createHash("sha256").update(piSharpExportText).digest("hex"),
		independentRuns: piSharpRuns.length,
		samePiCalibration: isDeepStrictEqual(piSharpRuns[0], piSharpRuns[1]),
		matchesPinnedFixture: isDeepStrictEqual(first.before, piSharpRuns[0].before),
		semanticRoundTrip: piSharpRuns[0].semanticRoundTrip,
		result: {
			sessionId: piSharpRuns[0].before.sessionId,
			leafId: piSharpRuns[0].before.leafId,
			branch: piSharpRuns[0].before.branch,
			contextMessageRoles: piSharpRuns[0].before.context.messages.map((message) => message.role),
			contextSha256: semanticHash(piSharpRuns[0].before.context),
			entryRecordsSha256: semanticHash(piSharpRuns[0].before.entryRecords),
			afterSemanticSha256: piSharpRuns[0].afterSemanticSha256,
		},
	};
}
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
if (!report.samePiCalibration || !report.semanticRoundTrip)
	throw new Error("Pinned Pi export/reopen did not produce stable semantic state");
if (report.piSharpExport && (!report.piSharpExport.samePiCalibration ||
	!report.piSharpExport.matchesPinnedFixture || !report.piSharpExport.semanticRoundTrip))
	throw new Error("Pinned Pi did not reproduce the PiSharp export as stable fixture semantics");
console.log(`Wrote stable pinned Pi export/reopen evidence for ${actualPiSha} to ${outputPath}`);
