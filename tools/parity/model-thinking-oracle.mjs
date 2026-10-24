#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const [piSourceArg, casesArg, outputArg] = process.argv.slice(2);
if (!piSourceArg || !casesArg || !outputArg) {
	console.error("Usage: node --import <pinned-pi>/packages/coding-agent/src/experimental/source-resolver.ts model-thinking-oracle.mjs <pinned-pi> <cases.json> <output.json>");
	process.exit(2);
}

const piSource = resolve(piSourceArg);
const casesPath = resolve(casesArg);
const outputPath = resolve(outputArg);
const actualPiSha = execFileSync("git", ["-C", piSource, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (actualPiSha !== PINNED_PI_SHA) {
	throw new Error(`Pinned Pi source mismatch: expected ${PINNED_PI_SHA}, got ${actualPiSha}`);
}

const [resolver, sessionModule, modelModule, simpleOptions, settingsModule, input] = await Promise.all([
	import(pathToFileURL(resolve(piSource, "packages/coding-agent/src/core/model-resolver.ts")).href),
	import(pathToFileURL(resolve(piSource, "packages/coding-agent/src/core/agent-session.ts")).href),
	import(pathToFileURL(resolve(piSource, "packages/ai/src/models.ts")).href),
	import(pathToFileURL(resolve(piSource, "packages/ai/src/api/simple-options.ts")).href),
	import(pathToFileURL(resolve(piSource, "packages/coding-agent/src/core/settings-manager.ts")).href),
	Promise.resolve(JSON.parse(readFileSync(casesPath, "utf8"))),
]);

function normalizeModel(model) {
	return model ? `${model.provider}/${model.id}` : null;
}

async function normalizeCase(item) {
	const models = (item.models ?? input.models).map((model) => ({ ...model }));
	if (item.operation === "reference") {
		const result = resolver.resolveModelReference(item.request, models, (provider) => item.authenticatedProviders.includes(provider));
		return result.status === "resolved"
			? { status: result.status, model: normalizeModel(result.model), thinkingLevel: result.thinkingLevel ?? null, warning: result.warning ?? null }
			: result.status === "ambiguous"
				? { status: result.status, candidates: result.candidates.map(normalizeModel).sort(), usableCount: result.usableCount, thinkingLevel: result.thinkingLevel ?? null }
				: { status: result.status, target: result.target ? {
					provider: result.target.provider,
					modelText: result.target.modelText,
					modelId: result.target.modelId,
					thinkingLevel: result.target.thinkingLevel ?? null,
					providerModels: result.target.providerModels.map(normalizeModel),
				} : null };
	}
	if (item.operation === "cli") {
		const runtime = {
			getModels: () => models,
			hasConfiguredAuth: (provider) => item.authenticatedProviders.includes(provider),
		};
		const result = resolver.resolveCliModel({ ...item.options, modelRuntime: runtime });
		return {
			model: normalizeModel(result.model),
			requestedThinkingLevel: result.thinkingLevel ?? null,
			effectiveThinkingLevel: item.options.cliThinking ?? result.thinkingLevel ?? null,
			warning: result.warning ?? null,
			error: result.error ?? null,
		};
	}
	if (item.operation === "scope") {
		const result = resolver.resolveModelScopeFromModels(item.patterns, models);
		return {
			models: result.scopedModels.map((scoped) => ({ model: normalizeModel(scoped.model), thinkingLevel: scoped.thinkingLevel ?? null })),
			diagnostics: result.diagnostics.map(({ code, message, pattern }) => ({ code, message, pattern })),
		};
	}
	if (item.operation === "initial") {
		const scopedModels = (item.scopedModels ?? []).map((scoped) => ({
			model: models.find((model) => model.provider === scoped.provider && model.id === scoped.id),
			thinkingLevel: scoped.thinkingLevel,
		})).filter((scoped) => scoped.model);
		const runtime = {
			getModel: (provider, id) => models.find((model) => model.provider === provider && model.id === id),
			hasConfiguredAuth: (provider) => item.authenticatedProviders.includes(provider),
			getAvailableSnapshot: () => models.filter((model) => item.authenticatedProviders.includes(model.provider)),
		};
		const result = await resolver.findInitialModel({
			scopedModels,
			isContinuing: item.isContinuing ?? false,
			defaultProvider: item.defaultProvider,
			defaultModelId: item.defaultModelId,
			defaultThinkingLevel: item.defaultThinkingLevel,
			modelThinkingLevels: item.modelThinkingLevels,
			modelRuntime: runtime,
		});
		return { model: normalizeModel(result.model), thinkingLevel: result.thinkingLevel, fallbackMessage: result.fallbackMessage ?? null };
	}
	if (item.operation === "thinking") {
		const settingsManager = settingsModule.SettingsManager.inMemory(item.settings ?? {});
		const model = models.find((candidate) => candidate.provider === item.provider && candidate.id === item.modelId);
		if (!model) throw new Error(`Unknown thinking model ${item.provider}/${item.modelId}`);
		const requestedLevel = sessionModule.AgentSession.prototype._getThinkingLevelForModelSwitch.call(
			{ settingsManager, thinkingLevel: item.currentLevel }, model, item.scopedThinkingLevel,
		);
		return {
			requestedLevel,
			effectiveLevel: modelModule.clampThinkingLevel(model, requestedLevel),
			availableLevels: modelModule.getSupportedThinkingLevels(model),
		};
	}
	if (item.operation === "options") {
		const model = models.find((candidate) => candidate.provider === item.provider && candidate.id === item.modelId);
		if (!model) throw new Error(`Unknown options model ${item.provider}/${item.modelId}`);
		const budgets = item.customBudgets;
		const adjusted = simpleOptions.adjustMaxTokensForThinking(item.requestedMaxTokens ?? undefined, model.maxTokens,
			item.thinkingLevel, budgets);
		const sampling = simpleOptions.resolveSamplingParams(model, item.thinkingLevel, item.requestSamplingParams);
		return {
			budget: simpleOptions.thinkingBudgetForLevel(item.thinkingLevel, budgets),
			maxTokens: adjusted.maxTokens,
			thinkingBudget: adjusted.thinkingBudget,
			samplingParams: sampling ?? null,
		};
	}
	if (item.operation === "settings") {
		const storage = new settingsModule.InMemorySettingsStorage();
		storage.withLock("global", () => JSON.stringify(item.globalSettings ?? {}));
		storage.withLock("project", () => JSON.stringify(item.projectSettings ?? {}));
		const settings = settingsModule.SettingsManager.fromStorage(storage, { projectTrusted: item.projectTrusted ?? true });
		for (const operation of item.mutations ?? []) {
			if (operation.type === "defaultThinking") settings.setDefaultThinkingLevel(operation.value);
			else if (operation.type === "modelThinking") settings.setModelThinkingLevel(operation.provider, operation.modelId, operation.value);
			else if (operation.type === "removeModelThinking") settings.removeModelThinkingLevel(operation.provider, operation.modelId);
			else if (operation.type === "enabledModels") settings.setEnabledModels(operation.value ?? undefined);
			else if (operation.type === "defaultModel") settings.setDefaultModelAndProvider(operation.provider, operation.modelId);
			else throw new Error(`Unknown settings mutation ${operation.type}`);
		}
		await settings.flush();
		const globalSnapshot = {};
		storage.withLock("global", (current) => { Object.assign(globalSnapshot, JSON.parse(current ?? "{}")); return undefined; });
		return {
			defaultProvider: settings.getDefaultProvider() ?? null,
			defaultModel: settings.getDefaultModel() ?? null,
			defaultThinkingLevel: settings.getDefaultThinkingLevel() ?? null,
			modelThinkingLevel: settings.getModelThinkingLevel(item.provider ?? "fixture", item.modelId ?? "reasoner") ?? null,
			enabledModels: settings.getEnabledModels() ?? null,
			globalSnapshot,
		};
	}
	if (item.operation === "interface") {
		if (item.interface === "cli") {
			const runtime = {
				getModels: () => models,
				hasConfiguredAuth: (provider) => item.authenticatedProviders.includes(provider),
			};
			const result = resolver.resolveCliModel({ ...item.options, modelRuntime: runtime });
			return {
				interface: item.interface,
				model: normalizeModel(result.model),
				thinkingLevel: result.thinkingLevel ?? null,
				warning: result.warning ?? null,
				error: result.error ?? null,
			};
		}
		if (item.interface === "tui") {
			const settingsManager = settingsModule.SettingsManager.inMemory(item.settings ?? {});
			const model = models.find((candidate) => candidate.provider === item.provider && candidate.id === item.modelId);
			if (!model) throw new Error(`Unknown TUI model ${item.provider}/${item.modelId}`);
			const requestedLevel = sessionModule.AgentSession.prototype._getThinkingLevelForModelSwitch.call(
				{ settingsManager, thinkingLevel: item.currentLevel }, model, item.scopedThinkingLevel,
			);
			return { interface: item.interface, model: normalizeModel(model), thinkingLevel: modelModule.clampThinkingLevel(model, requestedLevel) };
		}
		if (item.interface === "rpc") {
			const model = models.find((candidate) => candidate.provider === item.provider && candidate.id === item.modelId);
			if (!model) return { interface: item.interface, error: `Model not found: ${item.provider}/${item.modelId}` };
			return {
				interface: item.interface,
				model: normalizeModel(model),
				thinkingLevel: modelModule.clampThinkingLevel(model, item.thinkingLevel),
				availableLevels: modelModule.getSupportedThinkingLevels(model),
			};
		}
		throw new Error(`Unknown interface ${JSON.stringify(item.interface)}`);
	}
	throw new Error(`Unknown oracle operation ${JSON.stringify(item.operation)} for case ${item.id}`);
}

const results = await Promise.all(input.cases.map(async (item) => ({
	id: item.id,
	category: item.category,
	operation: item.operation,
	provenance: {
		pinnedPiSha: actualPiSha,
		piSharpSha: null,
		platform: `${process.platform}-${process.arch}`,
		oracleImplementation: "direct import of pinned Pi model-resolution, session, AI options and settings APIs",
		invocation: `node --import ${resolve(piSource, "packages/coding-agent/src/experimental/source-resolver.ts")} ${resolve(process.argv[1])} ${piSource} ${casesPath} ${outputPath}`,
		inputCatalog: item.models ?? input.models,
		authenticationState: item.authenticatedProviders ?? [],
		configuration: item.options ?? item.request ?? item.patterns ?? item,
		sourceReferences: item.sourceReferences ?? [],
		comparisonPolicy: "compare normalized model/provider, thinking, settings and option fields exactly; only candidate arrays are sorted where provider selection is unordered",
	},
	input: item,
	pinnedResult: await normalizeCase(item),
	classification: "oracle-only; awaiting paired PiSharp result",
})));

const report = {
	format: 1,
	pinnedPiSha: actualPiSha,
	oracle: "direct imports from packages/coding-agent/src/core/model-resolver.ts",
	platform: `${process.platform}-${process.arch}`,
	cases: results,
};

writeFileSync(outputPath, `${JSON.stringify(report, null, 2)}\n`);
console.log(`Wrote ${report.cases.length} direct-Pi results for ${actualPiSha} to ${outputPath}`);
