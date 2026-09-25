# PiSharp capability parity — durable execution state

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest fully validated code head on `main`: `bb576e37c09a8fde1734f03e966e5233f62aabdd`; Linux CI [36132336108](https://github.com/PhilipGB/pisharp/actions/runs/36132336108) passed restore, format, warnings-as-errors build and all 421 tests. Provider test fixtures now dispose listeners once and create fresh listeners for bind retries.
- Pi baseline: `earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d`, fetched 2026-09-25. Pi 0.87.1 uses Marked 18.0.11 tokenization and terminal-cell-aware grapheme wrapping.
- The current TUI slice adds a persistent idle alternate-screen shell, reusable searchable overlay/list, and an all/scoped model picker via Ctrl+L or `/model`. Local and exact-head Linux CI pass (421/421).

## What still prevents parity

- TUI remains incomplete: session/settings pickers, editor and keybinding breadth, mouse/selection, themes, terminal images, extension UI, and Markdown differences (LaTeX layout, syntax highlighting, broader Marked dialect behavior).
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Settings/resources/extensions, multimodal behavior, provider/auth breadth, and a current full differential audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Continue TUI acceptance with session/resume pickers, editor/keybinding capability, mouse/selection and theme/image support; keep the model matcher's Pi differential gap recorded rather than overstating its parity.
2. Move through session interoperability and RPC/JSON/SDK, then settings/resources/extensions, multimodal and provider/auth breadth. Refresh and reconcile Pi before image/theme work, changed capability families, and the final audit.
