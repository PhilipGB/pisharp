import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { setKeybindings } from "@earendil-works/pi-tui";
import { it } from "vitest";
import { KeybindingsManager } from "../src/core/keybindings.ts";
import { ENV_AGENT_DIR, getAgentDir } from "../src/config.ts";
import { InteractiveMode } from "../src/modes/interactive/interactive-mode.ts";
import { initTheme } from "../src/modes/interactive/theme/theme.ts";

it("captures current Pi Radius MCP setup outcomes", async () => {
    initTheme("dark");
    setKeybindings(new KeybindingsManager());
    const previousAgent = process.env[ENV_AGENT_DIR];
    const results = [];
    try {
        for (const scenario of JSON.parse(process.env.PISHARP_RADIUS_SCENARIOS!)) {
            const root = mkdtempSync(join(tmpdir(), "pi-radius-probe-"));
            const agent = join(root, "agent");
            mkdirSync(agent);
            mkdirSync(join(root, ".pi"));
            process.env[ENV_AGENT_DIR] = agent;
            if (getAgentDir() !== agent) throw new Error("The Pi probe agent directory is not isolated");
            try {
                const path = join(agent, "mcp.json");
                if (scenario.global) writeFileSync(path, JSON.stringify(scenario.global));
                if (scenario.project) writeFileSync(join(root, ".pi/mcp.json"), JSON.stringify(scenario.project));
                let offered = false;
                let component;
                const view = {
                    sessionManager: { getCwd: () => root },
                    ui: { requestRender: () => {} },
                    showSelector: (factory) => { offered = true; component = factory(() => {}).component; },
                    showError: (error) => { throw new Error(error); },
                    handleReloadCommand: async () => {},
                };
                (InteractiveMode as any).prototype.offerRadiusMcpServer.call(view, "radius", "Radius");
                if (component) {
                    if (scenario.action === "no") component.handleInput("\x1b[B");
                    component.handleInput(scenario.action === "cancel" ? "\x1b" : "\r");
                }
                results.push({ id: scenario.id, offered,
                    global: existsSync(path) ? JSON.parse(readFileSync(path, "utf8")) : null });
            } finally { rmSync(root, { recursive: true, force: true }); }
        }
    } finally {
        if (previousAgent === undefined) delete process.env[ENV_AGENT_DIR];
        else process.env[ENV_AGENT_DIR] = previousAgent;
    }
    writeFileSync(process.env.PISHARP_RADIUS_OUTPUT!, JSON.stringify(results));
});
