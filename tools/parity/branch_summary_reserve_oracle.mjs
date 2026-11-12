#!/usr/bin/env node
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const [pi, input, output] = process.argv.slice(2);
const pin = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const sha = execFileSync("git", ["-C", pi, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
if (sha !== pin) throw new Error(`Wrong Pi SHA: ${sha}`);
const load = path => import(pathToFileURL(resolve(pi, path)).href);
const [{ SettingsManager, InMemorySettingsStorage }, { createHarness },
    { fauxAssistantMessage }, { createAssistantMessageEventStream },
    { collectEntriesForBranchSummary, prepareBranchEntries }] = await Promise.all([
    load("packages/coding-agent/src/core/settings-manager.ts"),
    load("packages/coding-agent/test/suite/harness.ts"),
    load("packages/ai/src/providers/faux.ts"),
    load("packages/ai/src/utils/event-stream.ts"),
    load("packages/coding-agent/src/core/compaction/branch-summarization.ts"),
]);
const number = x => Number.isFinite(x) ? x : String(x);
const markers = ["OLDEST-MARKER", "MIDDLE-MARKER", "RECENT-MARKER", "SUMMARY-MARKER", "COMPACTION-MARKER"];
const text = message => message.summary ?? (typeof message.content === "string" ? message.content :
    (message.content ?? []).filter(x => x.type === "text").map(x => x.text).join("\n"));
const projection = value => markers.filter(x => value.includes(x));
const cases = [];
for (const item of JSON.parse(readFileSync(input, "utf8")).cases) {
    const storage = new InMemorySettingsStorage();
    storage.withLock("global", () => item.userRaw);
    storage.withLock("project", () => item.projectRaw);
    const manager = SettingsManager.fromStorage(storage, { projectTrusted: item.projectTrusted });
    const branch = manager.getSettings().branchSummary;
    const raw = branch?.reserveTokens;
    const reserve = manager.getBranchSummarySettings().reserveTokens;
    const context = item.contextWindow || 128000;
    const budget = context - reserve;
    const h = await createHarness({ models: [{ id: "reserve-fixture", contextWindow: item.contextWindow,
        maxTokens: item.maxTokens }] });
    h.session.settingsManager = manager;
    const m = h.sessionManager;
    const user = s => m.appendMessage({ role: "user", content: s, timestamp: 0 });
    const assistant = s => m.appendMessage({ ...fauxAssistantMessage(s), timestamp: 0 });
    let error = null;
    try {
        user("SHARED");
        const common = m.getLeafId();
        user("OLDEST-MARKER".padEnd(40, "x"));
        if (item.history === "files") {
            m.appendMessage({ ...fauxAssistantMessage(""), content: [{ type: "toolCall", id: "old-read",
                name: "read", arguments: { path: "old.txt" } }], timestamp: 0 });
        }
        if (item.history === "summary") {
            m.branchWithSummary(m.getLeafId(), "SUMMARY-MARKER".padEnd(160, "x"),
                { readFiles: ["nested.txt"], modifiedFiles: [] });
        } else if (item.history === "compaction") {
            m.appendCompaction("COMPACTION-MARKER".padEnd(160, "x"), m.getLeafId(), 100);
        } else assistant("MIDDLE-MARKER".padEnd(40, "x"));
        user("RECENT-MARKER".padEnd(40, "x"));
        const old = m.getLeafId();
        m.branch(common);
        const target = assistant("TARGET");
        m.branch(old);
        const entries = collectEntriesForBranchSummary(m, old, target).entries;
        const prepared = prepareBranchEntries(entries, budget);
        const requests = [];
        h.session.agent.streamFunction = (model, context, options) => {
            requests.push({ text: context.messages.map(text).join("\n"), maxTokens: options.maxTokens });
            const stream = createAssistantMessageEventStream();
            queueMicrotask(() => stream.push({ type: "done", reason: "stop", message: {
                ...fauxAssistantMessage("fixture summary"), api: model.api, provider: model.provider, model: model.id,
            } }));
            return stream;
        };
        // Explicitly choose Summarize when prompted; the existing truthy skip gate defaults to no summary.
        await h.session.navigateTree(target, { summarize: !manager.getBranchSummarySkipPrompt(),
            customInstructions: item.customInstructions });
        const summary = m.getEntries().findLast(x => x.type === "branch_summary" && x.fromId === old);
        const request = requests[0]?.text ?? "";
        const conversation = request.match(/<conversation>\n([\s\S]*?)\n<\/conversation>/)?.[1] ?? "";
        cases.push({ id: item.id, loaded: true, parentPresent: Object.hasOwn(manager.getSettings(), "branchSummary"),
            reservePresent: raw !== undefined, raw: raw === undefined ? "<absent>" : JSON.stringify(raw),
            effective: number(Number(reserve)), contextWindow: context, budget: number(budget), limiting: budget > 0,
            skips: !!manager.getBranchSummarySkipPrompt(),
            preparedMarkers: projection(prepared.messages.map(text).join("\n")),
            preparedCount: prepared.messages.length, markers: projection(conversation),
            providerCalls: requests.length, outputCaps: requests.map(x => x.maxTokens),
            conversation, summaryCreated: !!summary, targetSelected: summary ? summary.parentId === target : m.getLeafId() === target,
            emptySummary: summary?.summary === "No content to summarize",
            readFiles: summary?.details?.readFiles ?? [], modifiedFiles: summary?.details?.modifiedFiles ?? [],
            customPrompt: request.includes("Additional focus: CUSTOM BRANCH FOCUS"), error });
    } catch (e) { cases.push({ id: item.id, loaded: true, error: String(e) }); }
    finally { h.cleanup(); }
}
writeFileSync(output, JSON.stringify({ piSha: sha, cases }, null, 2) + "\n");
