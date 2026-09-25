# PiSharp capability parity — durable execution state

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages are excluded unless required for a core capability.

**Stop condition:** a full audit against current Pi main finds no material in-scope gaps and required validation/differential evidence passes.

## Current state

- Latest known-good PiSharp main: `ff4254c4168dcfee1d16868c93fb7778bc64e0dd`; Linux CI run [36123407896](https://github.com/PhilipGB/pisharp/actions/runs/36123407896) passed. The long-token GFM table-width failure is fixed by shared terminal-cell-aware wrapping.
- Current Pi pin: `earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d`, refreshed 2026-09-25. Pi 0.87.1 uses Marked 18.0.11 tokenization and ANSI-aware grapheme wrapping.
- The current uncommitted slice adopts Markdig 1.3.2 AST parsing with separate terminal renderers, and splits transcript buffer, viewport and search state out of `TerminalScreen`. Local format verification and warnings-as-errors build pass; the full suite passes 416/416. Exact-head Linux CI is pending.

## What still prevents parity

- TUI breadth remains the active priority: idle full-screen application, reusable overlays/pickers, editor/keybinding completion, mouse/selection, themes, images and extension UI. Markdown still differs from Pi in full dialect coverage, LaTeX layout, syntax highlighting and streaming edge cases.
- Pi JSONL/session interoperability and Pi-compatible RPC/SDK remain major gaps.
- Settings/resources/extensions, multimodal handling, provider/auth breadth and a current full differential audit remain incomplete. See the feature matrix and detailed inventory for evidence and residuals.

## Priority and next action

1. Commit the validated Markdig and transcript-component slice, then confirm Linux CI on that exact head.
2. Continue TUI acceptance work with a reusable overlay/list host and idle application surface, then model/scoped-model and session pickers; favor externally visible capability gaps.
3. Continue through session interoperability and RPC/JSON/SDK, then settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before image/theme work and before the final audit.
