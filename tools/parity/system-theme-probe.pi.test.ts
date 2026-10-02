import { writeFileSync } from "node:fs";
import { backgroundAnsi, foregroundAnsi, parseColor } from "@earendil-works/pi-tui";
import { it } from "vitest";
import { generateSystemThemeColors } from "../src/modes/interactive/theme/system-theme.ts";

it("captures current Pi system-theme ANSI colors", () => {
    const panels = new Set(["selectedBg", "searchMatchBg", "userMessageBg", "customMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg"]);
    const results = JSON.parse(process.env.PISHARP_THEME_SCENARIOS!).map((scenario) => {
        const rgb = (value: string) => ({ r: parseInt(value.slice(1, 3), 16), g: parseInt(value.slice(3, 5), 16), b: parseInt(value.slice(5), 16) });
        const generated = generateSystemThemeColors({
            background: scenario.background ? rgb(scenario.background) : undefined,
            foreground: scenario.foreground ? rgb(scenario.foreground) : undefined,
            palette: scenario.palette?.map(rgb), appearanceHint: scenario.appearance ?? "dark",
        });
        const colors = Object.fromEntries(Object.entries(generated.colors).map(([token, value]) => {
            const background = panels.has(token);
            let ansi = value === "" ? `\x1b[${background ? 49 : 39}m` :
                (background ? backgroundAnsi : foregroundAnsi)(parseColor(value), scenario.mode);
            if (!background && generated.dim.includes(token)) ansi += "\x1b[2m";
            return [token, ansi];
        }));
        return { id: scenario.id, appearance: generated.appearance, colors };
    });
    writeFileSync(process.env.PISHARP_THEME_OUTPUT!, JSON.stringify(results));
});
