# System-theme pastel and ANSI-256 colors

Oracle: current Pi `0495646a8322ff99ce40ac2f9e15f1f49f56bb11`, including pastel-cap change `409e808f5834dc4f2f37b54abf24d3a2f6ecad75`.

Palette and terminal foreground colors retain their source OKLCH chroma ceiling when moved to another lightness. Both OKHSL saturation and the chroma ceiling use Pi's existing family falloff. The shared `TerminalColorSpace` conversion serves theme parsing and generation, removing the duplicate private OKLCH converter from `TerminalTheme`.

The pre-fix comparison found 26 Frappe, nine Latte and 19 Dracula role differences. Frappe's accent changed from PiSharp's `#eb76d1` to Pi's `#cc92bd`; user/custom panels now resolve to `#283d73` and `#5d2b52`. Light Latte's accent resolves to `#a23388`. Two light/dark exact-color tests failed first, then passed with the cap and retained the 4.5:1 body-text checks.

Extending the probe to ANSI-256 found three existing gray-selection differences: Latte's pending-tool panel, background-only body text and grayscale high-thinking color. Pi selects the lower entry when gray distances tie; two dedicated gray-boundary cases failed before the shared selector was corrected. Weighted gray rounding follows Pi's positive half-up rule.

The paired fixture covers Frappe, Latte, Dracula, Solarized light, background-only, mid-gray, grayscale and dark/light indexed fallback. Each input runs in truecolor and 256color. All 18 cases match all 56 role ANSI prefixes and appearance: 1008 exact prefix comparisons. Colors and faint/default/indexed sequences are preserved without normalization. Dictionary serialization order has no visual meaning.

These are exact color-prefix comparisons. They do not compare component text, reset sequences, screen-cell layout, cursor state or the whole TUI. Those remain separate parity requirements.

`SystemThemePastelTests` and existing `TerminalThemeTests` pass 19/19, zero skips. Current Pi's `system-theme.test.ts` passes 8/8, zero skips. Final format verification passes; the warnings-as-errors build has zero warnings/errors. The serial full suite passes 1091/1091 with zero skips in 3m53s. Published source `099c63cea62067b83eb0acb10d30797d77e36e98` passes exact-head Linux CI [36991960991](https://github.com/PhilipGB/pisharp/actions/runs/36991960991), including all 1091 tests with zero skips and a zero-warning/error build.

Reproduce from the repository root:

```sh
python3 tools/parity/system-theme-differential.py --pi /path/to/current/pi --output /tmp/system-theme.json
```

The Pi checkout needs its dependencies installed. The probe executes Pi's real generator and color-prefix helpers; PiSharp's probe executes the actual terminal theme builder and token lookup. [The JSON fixture](system-theme-pastel-2026-10-02.json) preserves both matching results and the upstream SHA.
