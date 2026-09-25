# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless needed for a core capability.

**Stop condition:** a full audit against current Pi `main` finds no material in-scope gaps and required validation/differential evidence passes.

## Current checkpoint

- Main is green at `17d682c2d7206a052b8a65ca24cf9c514166e39e`; Linux CI run [36166108417](https://github.com/PhilipGB/pisharp/actions/runs/36166108417) passed format, warnings-as-errors build and all 480 tests. Local format, build and 480 tests pass on the same commit.
- Durable Pi parity baseline remains `b3487650f6378f1b0d1643dd254445ceb4a98035`. Current Pi `main` was fetched and confirmed at `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31`; current Kitty sizing minimizes cell distortion while respecting image width/height limits and placement rows.
- TUI includes Markdig AST rendering, shared display-cell wrapping, transcript state/viewport/search, a screen compositor, reusable picker host, mouse routing, editor history/selection/undo, searchable path completion and bounded clipboard image paste. Major TUI parity remains incomplete.

## What still prevents parity

- TUI/multimodal: Kitty/iTerm image rendering and fallback, full theme tokens/settings, extension UI, remaining mouse/editor behavior, syntax highlighting and Pi math layout.
- Sessions: Pi JSONL import/export/interoperability, crash recovery and concurrent access semantics.
- RPC/JSON/SDK: Pi wire-compatible framing, commands, events and process behavior.
- Resources/extensions, provider/auth breadth, remaining multimodal behavior and the final current-main audit remain incomplete. See the parity matrix and execution ledger.

## Priority and next action

1. Implement terminal image presentation as a focused component: capability detection, validated image dimensions, aspect-preserving Kitty/iTerm sizing, transcript-safe reserved rows and fallback text. Use current Pi `d6af72e` renderer and tests as the scoped reference; add deterministic renderer/compositor tests and PTY evidence.
2. Continue the TUI acceptance gate through themes, extension presentation and material mouse/editor flows. Once major visible workflows have process coverage, record small residuals and move to Pi JSONL session interoperability, then RPC/JSON/SDK and remaining parity areas.
