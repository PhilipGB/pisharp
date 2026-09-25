# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest exact-head Linux CI: `ba25c8adbc861e0dece4372bf4c7f97424014fe1`, run [36136775710](https://github.com/PhilipGB/pisharp/actions/runs/36136775710), format verification, warnings-as-errors build and all 424 tests passed.
- No-argument `/fork` opens the shared searchable overlay, preselects the newest active-branch user message, seeds the selected prompt for editing, and keeps `/fork <id>` plus configurable `app.session.fork`; focused PTY/keymap tests pass 3/3. Exact-head CI is green on `ba25c8a`.
- Pi baseline: `earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d`, fetched 2026-09-25.

## What still prevents parity

- TUI: settings picker; cross-project session scope and full session controls; editor selection, clipboard, external editor, history and completion; wider context-aware keybindings; mouse/selection; themes, terminal images and extension UI; LaTeX layout, syntax highlighting and wider Markdown dialect behavior.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-upstream audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Implement `/settings` through the shared overlay for PiSharp-supported settings, then continue the editor/application interaction gaps without reworking completed agent/tool areas.
2. Complete the TUI acceptance gate, then proceed through session interoperability, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before image/theme work, materially changed capability families and final audit.
