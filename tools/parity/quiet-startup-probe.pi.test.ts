import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { it } from "vitest";
import { setKeybindings } from "@earendil-works/pi-tui";
import { SettingsManager } from "../src/core/settings-manager.ts";
import { KeybindingsManager } from "../src/core/keybindings.ts";
import { InteractiveMode } from "../src/modes/interactive/interactive-mode.ts";
import { SettingsSelectorComponent, type SettingsConfig, type SettingsCallbacks } from "../src/modes/interactive/components/settings-selector.ts";
import { initTheme } from "../src/modes/interactive/theme/theme.ts";

it("captures current Pi quiet startup settings and presentation gates", async () => {
    const results = [];
    for (const scenario of JSON.parse(process.env.PISHARP_QUIET_SCENARIOS!)) {
        const root = mkdtempSync(join(tmpdir(), "pi-quiet-probe-"));
        const agent = join(root, "agent");
        mkdirSync(agent);
        mkdirSync(join(root, ".pi"));
        try {
            writeFileSync(join(agent, "settings.json"), JSON.stringify(scenario.user));
            if (scenario.project) writeFileSync(join(root, ".pi/settings.json"), JSON.stringify(scenario.project));
            const settingsManager = SettingsManager.create(root, agent, { projectTrusted: scenario.trusted ?? false });
            const view = { settingsManager, options: { verbose: scenario.verbose } };
            results.push({ id: scenario.id, value: String(settingsManager.getQuietStartup()),
                header: (InteractiveMode as any).prototype.shouldShowStartupHeader.call(view) });
        } finally { rmSync(root, { recursive: true, force: true }); }
    }
    const root = mkdtempSync(join(tmpdir(), "pi-quiet-save-"));
    const writes = [];
    try {
        const agent = join(root, "agent");
        mkdirSync(agent);
        writeFileSync(join(agent, "settings.json"), JSON.stringify({ hideThinkingBlock: true }));
        const settings = SettingsManager.create(root, agent, { projectTrusted: false });
        for (const value of ["header", true, false] as const) {
            settings.setQuietStartup(value);
            await settings.flush();
            writes.push(JSON.parse(readFileSync(join(agent, "settings.json"), "utf8")));
        }
    } finally { rmSync(root, { recursive: true, force: true }); }
    initTheme("dark");
    setKeybindings(new KeybindingsManager());
    const config = { warnings: {}, defaultModel: "not set", availableDefaultModels: [], availableThinkingLevels: [],
        modelThinkingLevels: {}, availableThemes: [], quietStartup: false } as unknown as SettingsConfig;
    const list = new SettingsSelectorComponent(config, {} as SettingsCallbacks).getSettingsList();
    const row = (list as any).items.find((item: any) => item.id === "quiet-startup");
    const setting = { label: row.label, description: row.description, values: row.values };
    writeFileSync(process.env.PISHARP_QUIET_OUTPUT!, JSON.stringify({ results, writes, setting }));
});
