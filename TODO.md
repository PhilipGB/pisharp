# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest known-good implementation head: `1abedd5ccde6640d0105da04e592aea02072be1e`; Linux CI run [36155964704](https://github.com/PhilipGB/pisharp/actions/runs/36155964704) passed format verification, warnings-as-errors build and all 455 tests.
- The external-editor slice is committed: Ctrl+G launches the configured editor from a private temporary directory/file, supports trusted user/project `externalEditor` settings, and suspends/restores alternate-screen and raw terminal modes. A fork-picker PTY readiness race was fixed in `33b6ef7`; exact-head Linux CI passes 438/438.
- Text clipboard paste/copy, bounded Linux/macOS/Windows helpers and remote/headless OSC 52 are committed as `d331d7d`; `b27a4b7` fixes the procfs race in the Bash process-tree test helper without weakening its assertion.
- Mouse input now routes through a focused controller: SGR wheel scrolling, transcript and prompt drag selection/copy, grapheme-safe editor cursor placement, picker option clicks, and terminal-mode restoration. `EditorViewport` maps visible cells back to source offsets, preserving wrapped and Unicode draft text. `1abedd5` adds prompt selection/cursor placement; focused viewport/screen tests pass 16/16, and exact-head Linux CI passes 455/455.
- Pi reference: `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, refreshed 2026-09-25. Material deltas since `5fd446ca1843682e8da3fec4ceb71c42f56fbace` are classified in `docs/continuation.md`.

## What still prevents parity

- TUI: keyboard selection and fuller history/undo, image clipboard paste, WSL clipboard interop, broader completion, double/triple-click and drag auto-scroll, mouse-aware extension widgets, themes, terminal images and extension UI; remaining Markdown dialect and rendering behavior. `TerminalScreen` still needs a focused compositor/lifecycle split before substantial new surface.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Resources/extensions, multimodal behavior, provider/auth breadth and the final current-upstream audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Refactor screen layout/output out of `TerminalScreen`; then continue editor history/undo and slash/path completion, plus representative PTY coverage. The Markdig AST-to-terminal-renderer boundary is in place; remaining Markdown rendering gaps include syntax highlighting and Pi-style math layout. Decide whether the TUI acceptance gate is met only after its major visible workflows have representative process tests. Residual mouse work: word/line clicks, drag auto-scroll and mouse-aware extension widgets.
2. After the TUI gate, continue with session interoperability, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before terminal-image/theme work, materially changed capability families and the final audit.
