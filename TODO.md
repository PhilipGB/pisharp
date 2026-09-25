# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest known-good implementation head: `41235a19a7b058feb4dd46b024eb4e95035f420b`; Linux CI run [36153996965](https://github.com/PhilipGB/pisharp/actions/runs/36153996965) passed format verification, warnings-as-errors build and all 452 tests.
- The external-editor slice is committed: Ctrl+G launches the configured editor from a private temporary directory/file, supports trusted user/project `externalEditor` settings, and suspends/restores alternate-screen and raw terminal modes. A fork-picker PTY readiness race was fixed in `33b6ef7`; exact-head Linux CI passes 438/438.
- Text clipboard paste/copy, bounded Linux/macOS/Windows helpers and remote/headless OSC 52 are committed as `d331d7d`; `b27a4b7` fixes the procfs race in the Bash process-tree test helper without weakening its assertion.
- Mouse slice `41235a1` adds SGR transcript/list wheel scrolling, visible transcript drag selection/copy, picker option clicks, terminal-cell/grapheme mapping and mouse-mode restoration. Focused tests pass 31/31; exact-head Linux CI passes format, warnings-as-errors build and 452/452 tests.
- Pi reference: `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, refreshed 2026-09-25. Material deltas since `5fd446ca1843682e8da3fec4ceb71c42f56fbace` are classified in `docs/continuation.md`.

## What still prevents parity

- TUI: editor-native selection, image clipboard paste, WSL clipboard interop, fuller history/completion, double/triple-click selection and drag auto-scroll, mouse-aware extension widgets, themes, terminal images and extension UI; remaining Markdown dialect and rendering behavior.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Resources/extensions, multimodal behavior, provider/auth breadth and the final current-upstream audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Continue TUI work with prompt editor mouse/cell selection, keyboard selection, fuller history/undo and slash/path completion; add representative PTY coverage before deciding whether the TUI acceptance gate is met. Residual transcript mouse work: word/line clicks, drag auto-scroll and mouse-aware extension widgets.
2. After the TUI gate, continue with session interoperability, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before terminal-image/theme work, materially changed capability families and the final audit.
