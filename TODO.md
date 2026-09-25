# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless required by a core capability.

**Stop condition:** a full audit against current Pi `main` finds no material in-scope gaps and required validation/differential evidence passes.

## Current checkpoint

- Main is green at `1a9e9cf0a6bcb47844766eff9ad8f55c7617fcb2`; Linux CI run [36162923941](https://github.com/PhilipGB/pisharp/actions/runs/36162923941) passed format, warnings-as-errors build and all 473 tests. The same commit passed those checks locally.
- Durable Pi parity baseline remains `b3487650f6378f1b0d1643dd254445ceb4a98035`. Current Pi `main` was refreshed to `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31` for terminal-image work; current Kitty sizing minimizes cell distortion and keeps image rows/crop metadata aligned.
- TUI now has a Markdig AST-to-terminal-renderer boundary, shared display-cell wrapping, transcript state/viewport/search components, screen compositor, modal picker host, mouse routing, editor history/undo, keyboard selection/copy, kill ring/yank and searchable path completion. Linux PTY and deterministic tests cover representative workflows; the broad TUI gate is not met.

## What still prevents parity

- TUI/multimodal: clipboard image paste and normalization, Kitty/iTerm rendering with safe fallback, full theme tokens/settings, extension UI, selected mouse interactions, syntax highlighting and Pi math layout.
- Sessions: Pi JSONL import/export/interoperability, crash recovery and concurrent access semantics.
- RPC/JSON/SDK: Pi wire-compatible framing, commands, events and process behavior.
- Resources/extensions, provider/auth breadth, remaining multimodal behavior and the final current-main audit remain incomplete. See the parity matrix and execution ledger.

## Priority and next action

1. Implement Pi-equivalent clipboard image acquisition/paste with bounded decoding and positional image semantics; follow it with terminal image rendering based on refreshed Pi `d6af72e`, including capability detection, aspect-preserving Kitty/iTerm sizing and a terminal-safe fallback. Add deterministic image/paste tests and representative PTY evidence.
2. Continue the TUI acceptance gate across themes, extension presentation and material mouse/editor flows. Once major visible workflows have process coverage, record small residuals and move to Pi JSONL session interoperability, then RPC/JSON/SDK and remaining parity areas.
