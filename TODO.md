# PiSharp capability parity — durable execution state

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest known-green PiSharp `main`: `1714e9857733b02bb3c84e3cb14f0799464b924a`; Linux CI [36127077490](https://github.com/PhilipGB/pisharp/actions/runs/36127077490) passed format, warnings-as-errors build and all 416 tests.
- Pi baseline: `earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d`, fetched 2026-09-25. Pi 0.87.1 uses Marked 18.0.11 tokenization and terminal-cell-aware grapheme wrapping.
- Local TUI work adds a persistent idle alternate-screen shell, reusable searchable overlay/list, and an all/scoped model picker via Ctrl+L or `/model`. Format, warnings-as-errors build and the full suite pass locally (421/421); exact-head Linux CI is pending.

## What still prevents parity

- TUI remains incomplete: session/settings pickers, editor and keybinding breadth, mouse/selection, themes, terminal images, extension UI, and Markdown differences (LaTeX layout, syntax highlighting, broader Marked dialect behavior).
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Settings/resources/extensions, multimodal behavior, provider/auth breadth, and a current full differential audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Validate and land the persistent TUI shell and reusable model picker, then continue TUI acceptance with session/resume pickers and editor/keybinding capability.
2. Move through session interoperability and RPC/JSON/SDK, then settings/resources/extensions, multimodal and provider/auth breadth. Refresh and reconcile Pi before image/theme work, changed capability families, and the final audit.
