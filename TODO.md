# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest exact-head Linux CI: `37eff5a9c7a15c5b12e8ff948fa53714046a2fda`, run [36134474053](https://github.com/PhilipGB/pisharp/actions/runs/36134474053), passed format, warnings-as-errors build and all 423 tests.
- The TUI has a persistent idle alternate-screen shell, shared searchable overlay/list, and all/scoped model picker. `/resume` now opens a searchable project-session picker; direct `/resume <id|name>` remains supported, and `app.session.resume` is configurable with no default key. Cross-project session scope and full Pi selector controls remain gaps.
- Pi baseline: `earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d`, fetched 2026-09-25. Current Pi source was checked for session-selector behavior.

## What still prevents parity

- TUI gaps: cross-project session scope and full session/fork/settings pickers; editor selection, clipboard and external editor; broader context-aware keybindings; mouse/selection; themes, terminal images and extension UI; Markdown LaTeX layout, syntax highlighting and wider dialect behavior.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Settings/resources/extensions, multimodal behavior, provider/auth breadth and a current full differential audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Finish TUI application workflows through the shared picker host, starting with a fork picker; then close the editor and application keybinding gaps. Keep model fuzzy-ranking and project-only session scope differences explicit.
2. Complete the TUI acceptance gate, then proceed through session interoperability, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before image/theme work, materially changed capability families and final audit.
