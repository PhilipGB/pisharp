# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest known-good committed head: `b27a4b7bb8b3e9333a398e770b9e1801c675da46`; Linux CI run [36150182304](https://github.com/PhilipGB/pisharp/actions/runs/36150182304) passed format verification, warnings-as-errors build and all 447 tests.
- The external-editor slice is committed: Ctrl+G launches the configured editor from a private temporary directory/file, supports trusted user/project `externalEditor` settings, and suspends/restores alternate-screen and raw terminal modes. A fork-picker PTY readiness race was fixed in `33b6ef7`; exact-head Linux CI passes 438/438.
- Text clipboard paste/copy, bounded Linux/macOS/Windows helpers and remote/headless OSC 52 are committed as `d331d7d`; `b27a4b7` fixes the procfs race in the Bash process-tree test helper without weakening its assertion.
- Working tree adds SGR mouse-wheel transcript/list scrolling, drag selection and copy, picker option clicks, terminal-cell/grapheme mapping and mouse-mode restoration. Focused tests pass 31/31; format, warnings-as-errors build and the full suite pass locally (452/452); exact-head CI is pending.
- Pi reference: `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, refreshed 2026-09-25. Material deltas since `5fd446ca1843682e8da3fec4ceb71c42f56fbace` are classified in `docs/continuation.md`.

## What still prevents parity

- TUI: editor-native selection, image clipboard paste, WSL clipboard interop, fuller history/completion, double/triple-click selection and drag auto-scroll, mouse-aware extension widgets, themes, terminal images and extension UI; remaining Markdown dialect and rendering behavior.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Resources/extensions, multimodal behavior, provider/auth breadth and the final current-upstream audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Commit/push the mouse interaction slice and verify exact-head Linux CI; then complete editor-native selection/history/completion and representative PTY coverage before deciding whether the TUI acceptance gate is met.
2. Continue with session interoperability, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before terminal-image/theme work, materially changed capability families and the final audit.
