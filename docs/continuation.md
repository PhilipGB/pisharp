# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix records upstream scope and the execution ledger holds per-capability evidence. Pi Packages are excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, fetched 2026-09-25. Current upstream `main` was refreshed separately to `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31` for terminal-image and theme work. The current `packages/tui/src/terminal-image.ts` and tests include Kitty cell-size distortion correction (#8938); current theme JSON, parser/controller, selector and terminal-color sources/tests were also inspected. The dark/light JSON palettes embedded in PiSharp match current Pi's files. This ref remains scoped evidence; do not silently replace the durable baseline. Refresh and reconcile upstream before materially changed capability families and the final audit.

## Current implementation and validation

PiSharp uses Microsoft Agent Framework and Microsoft.Extensions.AI for the provider/tool loop, with its own canonical C# session model. Pi JSONL session interoperability and Pi-compatible RPC/JSON/SDK behavior remain major gaps. The TUI uses Markdig 1.3.2 AST parsing with separate terminal renderers, shared terminal-cell wrapping, transcript buffer/viewport/search components, `TerminalScreenCompositor`, a reusable modal picker, focused mouse routing, and token-based dark/light palettes. User and trusted-project themes are selectable through `/settings`; `/reload` refreshes discovery, and attached terminals can report colors through OSC 10/11 for automatic appearance selection. Markdown, footer and overlay surfaces use shared theme tokens. The editor includes multiline history/undo, keyboard selection and copy, kill ring/yank, configurable actions, external editing, text clipboard access, and path/command completion with a searchable picker.

Exact code head `cca2071349b9c7377e14bc405f2d1ac516fa302d` passed Linux CI run `36177958487`: restore, format verification, warnings-as-errors build and all 504 tests (0 failed, 0 skipped). Interactive prompt and read-tool images use bounded Kitty/iTerm2 output with fallback, safe in-process lifecycle data and cleanup. Partial-viewport images still fall back to text and cell pixel dimensions use the 9x18 default. This theme slice is not the TUI acceptance gate or a full parity claim.

## Resume

Continue the TUI gate at the tool-rendering boundary. Compare current Pi's call/result renderer lifecycle, add a safe .NET extension renderer contract and theme-aware tool blocks, and validate them through deterministic tests and a representative PTY workflow. Keep terminal lifecycle, interaction and composition separate from tool renderer callbacks. CLI theme flags, settings-defined theme paths, extension-specific tool presentation and partial-viewport image crop/probe remain open. After representative TUI process/PTY workflows, continue Pi JSONL sessions, RPC/JSON/SDK, settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-main audit.
