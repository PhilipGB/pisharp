import { writeFileSync } from "node:fs";
import { setCapabilityOverrides, setKeybindings } from "@earendil-works/pi-tui";
import { it, vi } from "vitest";
import { KeybindingsManager } from "../src/core/keybindings.ts";
import { createLoginMenuSelector } from "../src/modes/interactive/components/radius-login-selector.ts";
import { initTheme } from "../src/modes/interactive/theme/theme.ts";

it("captures exact current Pi selected Radius shimmer", () => {
    setKeybindings(new KeybindingsManager());
    let time = 0;
    const clock = vi.spyOn(performance, "now").mockImplementation(() => time);
    try {
        const results = JSON.parse(process.env.PISHARP_RADIUS_SCENARIOS!).map((scenario) => {
            setCapabilityOverrides({ trueColor: scenario.mode === "truecolor" });
            initTheme("dark");
            time = 0;
            const text = "Sign in with Radius";
            const label = text + " • not configured";
            const component = createLoginMenuSelector({ requestRender: () => {} } as any, "Select authentication method:",
                ["Sign in with an account", "Sign in with an API key", label], { text, label }, () => {}, () => {});
            try {
                component.handleInput("\x1b[B");
                component.handleInput("\x1b[B");
                time = scenario.time;
                const line = component.render(120).find((line) => line.includes("→ "))!;
                const afterArrow = line.slice(line.indexOf("→ ") + 2).replace(/^\x1b\[39m/, "");
                const ansi = afterArrow.slice(0, afterArrow.indexOf("\x1b[39m") + 5);
                return { id: scenario.id, ansi };
            } finally { component.dispose(); }
        });
        writeFileSync(process.env.PISHARP_RADIUS_OUTPUT!, JSON.stringify(results));
    } finally { clock.mockRestore(); setCapabilityOverrides({}); }
});
