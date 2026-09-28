# Terminal wrapping and visual-row baseline

Current Pi `main` was checked on 2026-09-28 at `e1787702d32e0a7fd7387840c918faa4d1286187`. The paired implementation is `packages/tui/src/utils.ts:wrapTextWithAnsi`; its tests are in `packages/tui/test/wrap-ansi.test.ts`. Pi wraps at word boundaries, drops whitespace used as a break, reopens active SGR and OSC 8 state on continuation rows, and hard-splits only words wider than the available cells.

The current Pi function was executed directly under Node 24.21.0. These vectors matched PiSharp after the change:

| Input | Width | Wrapped visible rows |
|---|---:|---|
| `one two three` | 8 | `one two` / `three` |
| `alpha   beta` | 8 | `alpha` / `beta` |
| `red blue green` with red SGR | 8 | `red blue` / `green` |
| OSC 8 linked `one two` | 5 | `one` / `two` |
| `abcdefghij` | 4 | `abcd` / `efgh` / `ij` |
| mixed English/CJK paragraph from `TerminalTextLayoutTests` | 40 | `This is an example 中文汉字测试段落内容` / `中文汉字测试段落内容.` |
| `hi 👩‍💻 there` | 7 | `hi 👩‍💻` / `there` |

PiSharp's `TerminalTextLayout.Create` now supplies wrapped rows, visual-row lookup, and row starts from one ANSI-aware pass. The transcript viewport and compositor use that mapping for scroll windows and search centering; transcript search omits ANSI/OSC controls from searchable text while preserving raw source offsets. Inline Markdown styles close to their parent SGR state, so code and links inside emphasis or headings do not erase the enclosing style. Fail-first tests cover dropped break whitespace, nested style restoration, OSC 8 search offsets, image markers, CJK/emoji wrapping, viewport rows and the vectors above.

One deliberate safety difference remains: current Pi emits a width-2 CJK grapheme even when width is one; PiSharp preserves its established behavior and substitutes `?` so every output row fits. This fixture covers a bounded rendering slice; full Markdown/TUI parity is open. Exact-head Linux CI run [36417080999](https://github.com/PhilipGB/pisharp/actions/runs/36417080999) for source `90c08f7ed94c64b425e2accf1b6c3b8e3d674264` passed restore, format, warnings-as-errors build, and 720/720 tests (0 skipped). The follow-on CLI theme change passed exact-head CI run [36420934092](https://github.com/PhilipGB/pisharp/actions/runs/36420934092) with 729/729 tests (0 skipped).
