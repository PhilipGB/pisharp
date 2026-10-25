#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, mkdtempSync, rmSync, mkdirSync, utimesSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";
import { isDeepStrictEqual } from "node:util";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piArg, casesArg, outputArg] = process.argv.slice(2);
if (!piArg || !casesArg || !outputArg) {
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts session-jsonl-discovery-oracle.mjs <pinned-pi> <cases.json> <output.json>");
	process.exit(2);
}

const pi = resolve(piArg);
const inputPath = resolve(casesArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);
const input = JSON.parse(readFileSync(inputPath, "utf8"));
if (input.pinnedPiSha !== PINNED_PI_SHA) throw new Error(`Case pin mismatch: ${input.pinnedPiSha}`);
const { SessionManager, findMostRecentSession, getDefaultSessionDir } = await import(join(pi, "packages/coding-agent/src/core/session-manager.ts"));

function summarize(items) {
	return items.map((item) => ({ file: basename(item.path), id: item.id, cwd: item.cwd ? basename(item.cwd) : null, name: item.name ?? null, messageCount: item.messageCount }));
}

async function observe() {
	const root = mkdtempSync(join(tmpdir(), "pi-jsonl-discovery-oracle-"));
	const cwdA = join(root, "project-a");
	const cwdB = join(root, "project-b");
	const sessionDir = join(root, "sessions");
	const agentDir = join(root, "agent");
	mkdirSync(cwdA);
	mkdirSync(cwdB);
	mkdirSync(sessionDir);
	const previousAgentDir = process.env.PI_CODING_AGENT_DIR;
	process.env.PI_CODING_AGENT_DIR = agentDir;
	try {
		const now = Date.now();
		for (const file of input.files) {
			const path = join(sessionDir, file.name);
			const entries = file.invalidHeader ? [
				{ type: "message", id: "invalid-first", parentId: null, message: { role: "user", content: "not a session header" } },
			] : [
				{
					type: "session", version: 3, id: file.id,
					timestamp: "2025-01-01T00:00:00.000Z",
					...(file.cwd ? { cwd: file.cwd === "project-a" ? cwdA : cwdB } : {}),
				},
				{ type: "session_info", name: file.sessionName },
				{ type: "message", id: `${file.id}-message`, parentId: null, timestamp: "2025-01-01T00:00:01.000Z", message: { role: "user", content: file.message, timestamp: 1735689601000 } },
			];
			writeFileSync(path, `${entries.map((entry) => JSON.stringify(entry)).join("\n")}\n`);
			const modified = new Date(now - file.minutesAgo * 60_000);
			utimesSync(path, modified, modified);
		}

		const identityDir = join(root, "identity-sessions");
		mkdirSync(identityDir);
		const identitySession = SessionManager.create(cwdA, identityDir, { id: input.identity.id });
		identitySession.appendSessionInfo(input.identity.name);
		identitySession.appendMessage({ role: "user", content: "identity lookup transcript", timestamp: now });
		const { filterAndSortSessions } = await import(
			join(pi, "packages/coding-agent/src/modes/interactive/components/session-selector-search.ts")
		);
		const identitySessions = await SessionManager.list(cwdA, identityDir);
		const listA = await SessionManager.list(cwdA, sessionDir);
		const listB = await SessionManager.list(cwdB, sessionDir);
		const listAll = await SessionManager.listAll(sessionDir);
		const resourceDir = join(root, "resource-boundaries");
		mkdirSync(resourceDir);
		const largeHeaderPath = join(resourceDir, "large-header.jsonl");
		const fallbackPath = join(resourceDir, "fallback.jsonl");
		const largeHeaderLine = JSON.stringify({
			type: "session", version: 3, id: "discovery-large-header", timestamp: "2025-01-01T00:00:00.000Z",
			cwd: cwdA, resourceProbe: "x".repeat(input.resourceBoundary.paddingBytes),
		});
		writeFileSync(largeHeaderPath, `${largeHeaderLine}\n${JSON.stringify({
			type: "message", id: "large-header-message", parentId: null, timestamp: "2025-01-01T00:00:01.000Z",
			message: { role: "user", content: "large header transcript", timestamp: 1735689601000 },
		})}\n`);
		writeFileSync(fallbackPath, `${JSON.stringify({
			type: "session", version: 3, id: "discovery-header-fallback", timestamp: "2025-01-01T00:00:00.000Z", cwd: cwdA,
		})}\n${JSON.stringify({
			type: "message", id: "fallback-message", parentId: null, timestamp: "2025-01-01T00:00:01.000Z",
			message: { role: "user", content: "fallback transcript", timestamp: 1735689601000 },
		})}\n`);
		utimesSync(largeHeaderPath, new Date(now), new Date(now));
		utimesSync(fallbackPath, new Date(now - 60_000), new Date(now - 60_000));
		const resourceListings = await SessionManager.list(cwdA, resourceDir);
		const resourceRecent = findMostRecentSession(resourceDir, cwdA);
		const defaultSessionDir = getDefaultSessionDir(cwdA);
		const defaultSessionPath = join(defaultSessionDir, input.defaultDirectory.fileName);
		const defaultEntries = [
			{ type: "session", version: 3, id: input.defaultDirectory.id, timestamp: "2025-01-01T00:00:00.000Z" },
			{ type: "message", id: `${input.defaultDirectory.id}-message`, parentId: null,
				timestamp: "2025-01-01T00:00:01.000Z", message: { role: "user", content: "default directory without cwd", timestamp: 1735689601000 } },
		];
		writeFileSync(defaultSessionPath, `${defaultEntries.map((entry) => JSON.stringify(entry)).join("\n")}\n`);
		const defaultSessions = await SessionManager.list(cwdA);
		return {
			projectA: {
				list: summarize(listA),
				mostRecent: basename(findMostRecentSession(sessionDir, cwdA) ?? "") || null,
				findLocal: basename(SessionManager.findById(cwdA, "discovery-local", sessionDir) ?? "") || null,
				findForeign: basename(SessionManager.findById(cwdA, "discovery-foreign", sessionDir) ?? "") || null,
				findMissingCwd: basename(SessionManager.findById(cwdA, "discovery-no-cwd", sessionDir) ?? "") || null,
			},
			projectB: {
				list: summarize(listB),
				mostRecent: basename(findMostRecentSession(sessionDir, cwdB) ?? "") || null,
				findForeign: basename(SessionManager.findById(cwdB, "discovery-foreign", sessionDir) ?? "") || null,
			},
			unfilteredCustomDirectory: summarize(listAll),
			identityResolution: {
				exactIdFound: SessionManager.findById(cwdA, input.identity.id, identityDir) !== undefined,
				changedCaseIdFound: SessionManager.findById(cwdA, input.identity.id.toLowerCase(), identityDir) !== undefined,
				exactIdPrefix: identitySessions.find((item) => item.id.startsWith(input.identity.exactPrefix))?.id ?? null,
				changedCasePrefix: identitySessions.find((item) => item.id.startsWith(input.identity.changedCasePrefix))?.id ?? null,
				nameSearch: filterAndSortSessions(identitySessions, input.identity.nameQuery, "relevance").map((item) => item.id),
			},
			defaultDirectoryMissingCwd: {
				listed: defaultSessions.some((item) => item.id === input.defaultDirectory.id),
				findById: SessionManager.findById(cwdA, input.defaultDirectory.id) !== undefined,
				mostRecent: basename(findMostRecentSession(defaultSessionDir) ?? "") || null,
			},
			discoveryResourceBoundary: {
				headerBytes: Buffer.byteLength(largeHeaderLine),
				headerScanLimitBytes: input.resourceBoundary.headerScanLimitBytes,
				largeHeaderListed: resourceListings.some((item) => item.id === "discovery-large-header"),
				mostRecentFallback: basename(resourceRecent ?? "") || null,
			},
		};
	} finally {
		if (previousAgentDir === undefined) delete process.env.PI_CODING_AGENT_DIR;
		else process.env.PI_CODING_AGENT_DIR = previousAgentDir;
		rmSync(root, { recursive: true, force: true });
	}
}

const first = await observe();
const second = await observe();
if (!isDeepStrictEqual(first, second)) throw new Error("Pinned discovery output differed between independent runs");
const report = {
	schema: 1,
	caseId: input.caseId,
	pinnedPiSha: actualPiSha,
	inputSha256: createHash("sha256").update(readFileSync(inputPath)).digest("hex"),
	source: "packages/coding-agent/src/core/session-manager.ts:getDefaultSessionDirPath, findMostRecentSession, SessionManager.continueRecent, SessionManager.findById, SessionManager.list, SessionManager.listAll, listSessionsFromDir, buildSessionInfo; packages/coding-agent/src/main.ts:resolveSessionPath; packages/coding-agent/src/modes/interactive/components/session-selector-search.ts:filterAndSortSessions",
	independentRuns: 2,
	samePiCalibration: true,
	result: first,
};
writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
console.log(`Wrote pinned discovery capture for ${actualPiSha} to ${outputPath}`);
