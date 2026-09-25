# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest code CI checkpoint: `37eff5a9c7a15c5b12e8ff948fa53714046a2fda`, Linux run [36134474053](https://github.com/PhilipGB/pisharp/actions/runs/36134474053), format/build/all 423 tests passed. Docs checkpoint `2582c1f74a8940d6cdf604bcfe42272bdc252d9f` also passed exact-head Linux CI run [36134783838](https://github.com/PhilipGB/pisharp/actions/runs/36134783838).
- Fork-picker slice is implemented in the working tree: no-argument `/fork` opens the shared searchable overlay, preselects the newest active-branch user message, seeds the selected prompt for editing, and keeps `/fork <id>` plus configurable `app.session.fork`. Focused tests pass 3/3; format, warnings-as-errors build and all 424 tests pass locally. Commit and exact-head CI are pending.
- Pi baseline: `earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d`, fetched 2026-09-25.

## What still prevents parity

- TUI: settings picker; cross-project session scope and full session controls; editor selection, clipboard, external editor, history and completion; wider context-aware keybindings; mouse/selection; themes, terminal images and extension UI; LaTeX layout, syntax highlighting and wider Markdown dialect behavior.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-upstream audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Commit and inspect CI for the fork-picker slice. Then continue through the shared TUI overlay with `/settings`, and close the editor/application interaction gaps without reworking completed agent/tool areas.
2. Complete the TUI acceptance gate, then proceed through session interoperability, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before image/theme work, materially changed capability families and final audit.
