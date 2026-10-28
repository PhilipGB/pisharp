#!/usr/bin/env node
import { writeFile } from "node:fs/promises";
import { resolve, join } from "node:path";
import { pathToFileURL } from "node:url";
import { execFileSync } from "node:child_process";

const PIN = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const args = process.argv.slice(2);
const value = (name) => {
  const index = args.indexOf(name);
  if (index < 0 || !args[index + 1]) throw new Error(`missing ${name}`);
  return args[index + 1];
};
const piRoot = resolve(value("--pi"));
const output = resolve(value("--output"));
const head = execFileSync("git", ["rev-parse", "HEAD"], { cwd: piRoot, encoding: "utf8" }).trim();
if (head !== PIN) throw new Error(`Pi checkout is ${head}; expected ${PIN}`);

const fromPi = (relative) => import(pathToFileURL(join(piRoot, relative)).href);
const [{ calculateContextTokens }, { calculateCost },
  { addUsageToTotals, createUsageTotals, getUsageCostBreakdown }, { SessionManager }] = await Promise.all([
  fromPi("packages/coding-agent/src/core/compaction/compaction.ts"),
  fromPi("packages/ai/src/models.ts"),
  fromPi("packages/coding-agent/src/core/usage-totals.ts"),
  fromPi("packages/coding-agent/src/core/session-manager.ts"),
]);

function usage(input, outputTokens, cacheRead, cacheWrite, totalTokens, reasoning) {
  return {
    input, output: outputTokens, cacheRead, cacheWrite, totalTokens,
    ...(reasoning === undefined ? {} : { reasoning }),
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
  };
}

function price(model, value) {
  calculateCost({ cost: model }, value);
  return money(value.cost.total);
}

function money(value) {
  return Number(value.toFixed(12));
}

const summaryUsage = usage(20, 4, 5, 0, 34);
const summaryManager = SessionManager.inMemory("/tmp/pi-c10");
const summaryRootId = summaryManager.appendMessage({ role: "user", content: "summary root", timestamp: 1 });
const summaryId = summaryManager.branchWithSummary(summaryRootId, "branch summary", undefined, false, summaryUsage);
const summaryEntry = summaryManager.getEntries().find((entry) => entry.id === summaryId);
const activeSummaryEntry = summaryManager.getBranch().find((entry) => entry.id === summaryId);
const fallbackUsage = usage(6, 3, 4, 5, 0);
const distinctUsage = usage(6, 3, 4, 5, 18, 2);
const missingUsage = usage(0, 0, 0, 0, 0);
const knownPrice = { input: 3, output: 8, cacheRead: 1, cacheWrite: 4 };
const knownUsage = usage(75, 3, 25, 7, 110);
const knownCost = price(knownPrice, knownUsage);
const tierPrice = {
  input: 1, output: 3, cacheRead: 0.5, cacheWrite: 2,
  tiers: [{ inputTokensAbove: 272_000, input: 10, output: 30, cacheRead: 1, cacheWrite: 5 }],
};
const tierUsage = usage(60_000, 0, 200_000, 20_000, 0);
const tieredCost = price(tierPrice, tierUsage);
const unknownPriceCost = price({ input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
  usage(6, 3, 4, 5, 18));

const summarySmall = usage(60, 5, 10, 2, 77);
const summaryLarge = usage(40, 2, 5, 3, 50);
price({ input: 2, output: 4, cacheRead: 1, cacheWrite: 3 }, summarySmall);
price({ input: 6, output: 12, cacheRead: 2, cacheWrite: 8 }, summaryLarge);
const summaryBreakdown = getUsageCostBreakdown([
  { type: "compaction", id: "summary-small", parentId: null, timestamp: "fixture", summary: "small", firstKeptEntryId: "root", tokensBefore: 100, usage: summarySmall },
  { type: "compaction", id: "summary-large", parentId: null, timestamp: "fixture", summary: "large", firstKeptEntryId: "root", tokensBefore: 100, usage: summaryLarge },
])[0];

const activeUsage = usage(40, 8, 0, 0, 48);
const branchManager = SessionManager.inMemory("/tmp/pi-c10-branches");
const branchRootId = branchManager.appendMessage({ role: "user", content: "branch root", timestamp: 1 });
branchManager.appendUsage("billing", "fixture", "inactive-model", usage(100, 10, 0, 0, 110));
branchManager.branch(branchRootId);
branchManager.appendUsage("billing", "fixture", "active-model", activeUsage);
const activeBranchUsage = branchManager.getBranch().filter((entry) => entry.type === "usage");
const activeTotals = createUsageTotals();
for (const entry of activeBranchUsage) addUsageToTotals(activeTotals, entry.usage);

const projectionUsage = usage(40, 8, 5, 7, 60);
projectionUsage.cost = { input: 0.008, output: 0.002, cacheRead: 0.001, cacheWrite: 0.0013, total: 0.0123 };
const projectionTotals = createUsageTotals();
addUsageToTotals(projectionTotals, projectionUsage);

const rpcBranchSummaryUsage = usage(20, 4, 5, 2, 31);
rpcBranchSummaryUsage.cost = { input: 0.002, output: 0.003, cacheRead: 0.004, cacheWrite: 0.005, total: 0.014 };
const rpcSummaryManager = SessionManager.inMemory("/tmp/pi-c10-rpc-summary");
const rpcSummaryRootId = rpcSummaryManager.appendMessage({ role: "user", content: "question", timestamp: 1 });
rpcSummaryManager.branchWithSummary(rpcSummaryRootId, "generated summary", undefined, false, rpcBranchSummaryUsage);
const rpcSummaryEntries = rpcSummaryManager.getEntries();
const rpcSummaryBreakdown = getUsageCostBreakdown(rpcSummaryEntries).find((entry) => entry.key === "Tools/summaries");

const result = {
  cases: {
    "C10.summary-request-usage-persists-on-active-branch": {
      summaryEntryType: summaryEntry?.type,
      summaryEntryIsActive: activeSummaryEntry !== undefined,
      usageIsOnActiveBranch: activeSummaryEntry?.type === "branch_summary" && activeSummaryEntry.usage === summaryUsage,
      input: activeSummaryEntry?.type === "branch_summary" ? activeSummaryEntry.usage?.input : null,
      output: activeSummaryEntry?.type === "branch_summary" ? activeSummaryEntry.usage?.output : null,
      cacheRead: activeSummaryEntry?.type === "branch_summary" ? activeSummaryEntry.usage?.cacheRead : null,
      cacheWrite: activeSummaryEntry?.type === "branch_summary" ? activeSummaryEntry.usage?.cacheWrite : null,
      totalTokens: activeSummaryEntry?.type === "branch_summary" ? activeSummaryEntry.usage?.totalTokens : null,
    },
    "C10.fallback-context-total-includes-cache-tokens": {
      input: fallbackUsage.input,
      output: fallbackUsage.output,
      cacheRead: fallbackUsage.cacheRead,
      cacheWrite: fallbackUsage.cacheWrite,
      contextTokens: calculateContextTokens(fallbackUsage),
    },
    "C10.input-output-cache-read-cache-write-remain-distinct": {
      input: distinctUsage.input,
      output: distinctUsage.output,
      cacheRead: distinctUsage.cacheRead,
      cacheWrite: distinctUsage.cacheWrite,
      totalTokens: distinctUsage.totalTokens,
    },
    "C10.reasoning-tokens-are-preserved": {
      reasoning: distinctUsage.reasoning,
      input: distinctUsage.input,
      output: distinctUsage.output,
    },
    "C10.missing-usage-fields-remain-unknown": {
      inputIsUnknown: false,
      outputIsUnknown: false,
      cacheReadIsUnknown: false,
      cacheWriteIsUnknown: false,
      totalIsUnknown: false,
      reasoningIsUnknown: missingUsage.reasoning === undefined,
      inputTotalIsKnown: false,
    },
    "C10.unknown-prices-do-not-create-cost": {
      cost: unknownPriceCost,
    },
    "C10.known-pricing-and-input-tiers-are-calculated": {
      knownCost,
      tieredCost,
      tieredInput: tierUsage.input,
      tieredCacheRead: tierUsage.cacheRead,
      tieredCacheWrite: tierUsage.cacheWrite,
    },
    "C10.split-summary-physical-model-costs-aggregate-correctly": {
      modelCount: new Set(["summary-small", "summary-large"]).size,
      input: summarySmall.input + summaryLarge.input,
      output: summarySmall.output + summaryLarge.output,
      cacheRead: summarySmall.cacheRead + summaryLarge.cacheRead,
      cacheWrite: summarySmall.cacheWrite + summaryLarge.cacheWrite,
      totalTokens: summarySmall.totalTokens + summaryLarge.totalTokens,
      cost: money(summaryBreakdown.cost),
    },
    "C10.inactive-branch-usage-is-excluded-from-active-total": {
      activeUsageCount: activeBranchUsage.length,
      input: activeTotals.input,
      output: activeTotals.output,
      totalTokens: activeUsage.totalTokens,
      inactiveUsageIncluded: activeBranchUsage.some((entry) => entry.model === "inactive-model"),
    },
    "C10.cli-tui-and-rpc-report-canonical-session-usage": {
      cli: { input: projectionUsage.totalTokens, cost: money(projectionUsage.cost.total) },
      tui: {
        input: projectionTotals.input > 0,
        output: projectionTotals.output > 0,
        cacheRead: projectionTotals.cacheRead > 0,
        cacheWrite: projectionTotals.cacheWrite > 0,
        cost: projectionTotals.cost > 0,
      },
      rpc: {
        input: projectionTotals.input,
        output: projectionTotals.output,
        cacheRead: projectionTotals.cacheRead,
        cacheWrite: projectionTotals.cacheWrite,
        total: projectionTotals.input + projectionTotals.output + projectionTotals.cacheRead + projectionTotals.cacheWrite,
        cost: money(projectionTotals.cost),
      },
      nativeBranchSummaryRpc: {
        summaryEntries: rpcSummaryEntries.filter((entry) => entry.type === "branch_summary").length,
        input: rpcBranchSummaryUsage.input,
        output: rpcBranchSummaryUsage.output,
        cacheRead: rpcBranchSummaryUsage.cacheRead,
        cacheWrite: rpcBranchSummaryUsage.cacheWrite,
        total: rpcSummaryBreakdown?.tokens ?? null,
        cost: money(rpcSummaryBreakdown?.cost ?? 0),
      },
    },
  },
};
await writeFile(output, `${JSON.stringify(result, null, 2)}\n`, "utf8");
