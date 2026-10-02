import { writeFileSync } from "node:fs";
import { fauxAssistantMessage, getCurrentSystemMessage, getCurrentTools } from "@earendil-works/pi-ai";
import { registerFauxProvider } from "@earendil-works/pi-ai/compat";
import { Type } from "typebox";
import { expect, it } from "vitest";
import type { ExtensionAPI, ExtensionFactory } from "../../src/core/extensions/types.ts";
import { createTestExtensionsResult, createTestResourceLoader } from "../utilities.ts";
import { createHarness, createTestUiContext, type Harness } from "./harness.ts";

const search = "mcp__docs__search";
const tool = (name: string, deferred = false, defaultActive = true) => ({
    name, label: name, description: name, parameters: Type.Object({}),
    exposure: deferred ? "deferred" as const : "direct" as const, defaultActive,
    execute: async () => ({ content: [{ type: "text" as const, text: "found" }], details: {} }),
});

it("records delayed deferred-tool loadouts against current Pi", async () => {
    const results = [];
    for (const id of JSON.parse(process.env.PISHARP_DEFERRED_SCENARIOS!)) {
        const harnesses: Harness[] = [];
        let api: ExtensionAPI;
        let includeSearch = true;
        const factory: ExtensionFactory = (pi) => {
            api = pi;
            pi.registerTool(tool("base"));
            pi.registerTool(tool("added", false, false));
            if (includeSearch) pi.registerTool(tool(search, true));
        };
        let unregisterReplacement: (() => void) | undefined;
        try {
            let extensions = await createTestExtensionsResult([factory]);
            const resourceLoader = {
                ...createTestResourceLoader(), getExtensions: () => extensions,
                reload: async () => { extensions = await createTestExtensionsResult([factory]); },
            };
            const first = await createHarness({ tools: [], resourceLoader, initialActiveToolNames: ["base", search] });
            harnesses.push(first);
            await first.session.bindExtensions({ uiContext: createTestUiContext() });
            first.setResponses([fauxAssistantMessage("seed")]);
            await first.session.prompt("seed");
            includeSearch = false;
            let resumed: Harness;
            if (id === "reload") {
                await first.session.reload();
                resumed = first;
                const replacement = registerFauxProvider({ api: first.faux.api });
                resumed.setResponses = replacement.setResponses;
                unregisterReplacement = replacement.unregister;
            } else {
                resumed = await createHarness({
                    tools: [], sessionManager: first.sessionManager, extensionFactories: [factory],
                    ...(id === "excluded" ? { excludedToolNames: [search] } : {}),
                    ...(id === "allowed" ? { allowedToolNames: ["base"] } : {}),
                });
                resumed.session.agent.state.messages = resumed.sessionManager.buildSessionContext().messages;
                harnesses.push(resumed);
                await resumed.session.bindExtensions({ uiContext: createTestUiContext() });
            }
            const before = resumed.session.getActiveToolNames();
            const requests: string[][] = [];
            const prompt = async (cancel = false) => {
                resumed.setResponses([(context) => {
                    requests.push(getCurrentTools(context.messages).map((entry) => entry.name));
                    if (cancel) void resumed.session.abort();
                    return fauxAssistantMessage("done", cancel ? { stopReason: "aborted", errorMessage: "Request was aborted" } : {});
                }]);
                await resumed.session.prompt("go");
            };
            if (id === "additive") resumed.session.setActiveToolsByName(["base", "added"]);
            if (id === "replacement") resumed.session.setActiveToolsByName(["added"]);
            if (id === "prompt" || id === "cancel") await prompt(id === "cancel");
            api!.registerTool(tool(search, true));
            const registered = resumed.session.getActiveToolNames();
            await prompt();
            if (id === "reload" && requests.length === 0) throw new Error(JSON.stringify(resumed.session.messages.at(-1)));
            const persisted = (getCurrentSystemMessage(resumed.sessionManager.buildSessionContext().messages)?.toolsAdded ?? [])
                .map((entry) => entry.name);
            results.push({ id, before, registered, requests, persisted });
        } finally {
            unregisterReplacement?.();
            while (harnesses.length) harnesses.pop()!.cleanup();
        }
    }
    expect(results).toHaveLength(8);
    writeFileSync(process.env.PISHARP_DEFERRED_OUTPUT!, JSON.stringify(results));
});
