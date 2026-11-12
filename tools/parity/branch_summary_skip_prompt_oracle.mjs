#!/usr/bin/env node
/**
 * Freeze pinned Pi's `branchSummary.skipPrompt` settings boundary.
 *
 * The probe loads each case through the real SettingsManager storage path, so the
 * observed value reflects Pi's presence-based deep merge, trust gate and raw
 * getter exactly as the interactive tree flow consumes it:
 *
 *     if (!this.settingsManager.getBranchSummarySkipPrompt()) { show prompt }
 *
 * Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts \
 *          branch_summary_skip_prompt_oracle.mjs <pinned-pi> <cases.json> <output.json>
 */
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piSourceArg, casesArg, outputArg] = process.argv.slice(2);
if (!piSourceArg || !casesArg || !outputArg) {
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts "
		+ "branch_summary_skip_prompt_oracle.mjs <pinned-pi> <cases.json> <output.json>");
	process.exit(2);
}

const piSource = resolve(piSourceArg);
const actualSha = execFileSync("git", ["-C", piSource, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualSha !== PINNED_PI_SHA) {
	throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualSha}`);
}

const settingsModule = await import(
	pathToFileURL(resolve(piSource, "packages/coding-agent/src/core/settings-manager.ts")).href);
const input = JSON.parse(readFileSync(resolve(casesArg), "utf8"));

/** JSON.stringify drops undefined; make absence explicit so the evidence is comparable. */
function describe(value) {
	return value === undefined ? "<undefined>" : JSON.stringify(value);
}

function observe(item) {
	const storage = new settingsModule.InMemorySettingsStorage();
	if (item.user !== null) storage.withLock("global", () => JSON.stringify(item.user));
	if (item.project !== null) storage.withLock("project", () => JSON.stringify(item.project));

	const manager = settingsModule.SettingsManager.fromStorage(storage, {
		projectTrusted: item.projectTrusted ?? true,
	});
	const merged = manager.getSettings();
	const branch = merged.branchSummary;
	const skip = manager.getBranchSummarySkipPrompt();
	return {
		id: item.id,
		mergedBranchSummary: describe(branch),
		hasBranchSummaryKey: Object.hasOwn(merged, "branchSummary"),
		skipPrompt: describe(branch?.skipPrompt),
		skipPromptType: typeof branch?.skipPrompt,
		getter: describe(skip),
		getterType: typeof skip,
		// The interactive tree flow asks the question whenever the getter is falsey.
		prompts: !skip,
		reserveTokens: describe(manager.getBranchSummarySettings().reserveTokens),
	};
}

writeFileSync(resolve(outputArg), JSON.stringify({
	piSha: actualSha,
	cases: input.cases.map(observe),
}, null, 2) + "\n");
