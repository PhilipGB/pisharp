# PiSharp capability parity — durable execution state

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages are excluded unless required for a core capability.

**Stop condition:** a full audit against current Pi main finds no material in-scope gaps and required validation/differential evidence passes.

## Current state

- Latest known-good PiSharp main: `b8c3a21af5ee5b4cedeb9c8feb8a0911d5d01acd`; Linux CI run [36126888086](https://github.com/PhilipGB/pisharp/actions/runs/36126888086) passed format, warnings-as-errors build and the full suite. The preceding parser commit's CI exposed a disposed-listener retry bug in a provider fixture; the retry now creates a fresh listener, with assertions unchanged.
- Current Pi pin: `earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d`, refreshed 2026-09-25. Pi 0.87.1 uses Marked 18.0.11 tokenization and ANSI-aware grapheme wrapping.
- Main now uses Markdig 1.3.2 AST parsing with separate terminal renderers, and splits transcript buffer, viewport and search state out of `TerminalScreen`. Remaining Markdown differences include Pi's Unicode LaTeX layout, multiline backslash display math, syntax highlighting and broader Marked dialect behavior.

## What still prevents parity

- TUI breadth remains the active priority: idle full-screen application, reusable overlays/pickers, editor/keybinding completion, mouse/selection, themes, images and extension UI. Markdown still differs from Pi in full dialect coverage, LaTeX layout, syntax highlighting and streaming edge cases.
- Pi JSONL/session interoperability and Pi-compatible RPC/SDK remain major gaps.
- Settings/resources/extensions, multimodal handling, provider/auth breadth and a current full differential audit remain incomplete. See the feature matrix and detailed inventory for evidence and residuals.

## Priority and next action

1. Continue TUI acceptance work with a reusable overlay/list host and idle application surface, then model/scoped-model and session pickers; favor externally visible capability gaps.
2. Continue through session interoperability and RPC/JSON/SDK, then settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before image/theme work and before the final audit.
