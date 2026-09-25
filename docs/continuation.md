# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix records upstream scope and the execution ledger holds per-capability evidence. Pi Packages are excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, fetched 2026-09-25. Current upstream `main` was refreshed separately to `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31` before terminal-image work. The current `packages/tui/src/terminal-image.ts` and `test/terminal-image.test.ts` include Kitty cell-size distortion correction (#8938), with tests for thin images, width/height constraints, stable reserved rows and crop metadata. The current ref remains scoped inspection evidence; do not silently replace the durable baseline. Refresh and reconcile upstream before materially changed capability families and the final audit.

## Current implementation and validation

PiSharp uses Microsoft Agent Framework and Microsoft.Extensions.AI for the provider/tool loop, with its own canonical C# session model. Pi JSONL session interoperability and Pi-compatible RPC/JSON/SDK behavior remain major gaps. The TUI uses Markdig 1.3.2 AST parsing with separate terminal renderers, shared terminal-cell wrapping, transcript buffer/viewport/search components, `TerminalScreenCompositor`, a reusable modal picker, and focused mouse routing. The editor includes multiline history/undo, keyboard selection and copy, kill ring/yank, configurable actions, external editing, text clipboard access, and path/command completion with a searchable picker.

Exact code head `ff301f231d80b0ec81f093cf8d09d5b5b45b821c` passed Linux CI run `36172481288`: restore, format verification, warnings-as-errors build and all 491 tests. Interactive prompt and read-tool images now use bounded Kitty/iTerm2 output with fallback, safe in-process lifecycle data and cleanup. Partial-viewport images still fall back to text and cell pixel dimensions use the 9x18 default. This is not the TUI acceptance gate or a full parity claim.

## Resume

The terminal-image slice is implemented and exact-head Linux CI passes. Before theme work, refresh current Pi `main`; inspect its terminal-color/theme implementation, discovery/settings path and tests. Build a token-based theme system that reaches Markdown, status, tool blocks, overlays and pickers, then continue the TUI gate through extension presentation and representative PTY/process flows. Next address Pi JSONL sessions, RPC/JSON/SDK, settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-main audit.
