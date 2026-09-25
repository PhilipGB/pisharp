# PiSharp capability parity

**Goal:** implement Pi’s in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless needed for a core capability.

**Stop condition:** a full audit against current Pi `main` finds no material in-scope gaps and required validation/differential evidence passes.

## Current checkpoint

- Exact code head `cca2071349b9c7377e14bc405f2d1ac516fa302d` is pushed. Linux CI run [36177958487](https://github.com/PhilipGB/pisharp/actions/runs/36177958487) passed restore, format verification, warnings-as-errors build, and all 504 tests (0 failed, 0 skipped).
- Durable Pi parity baseline remains `b3487650f6378f1b0d1643dd254445ceb4a98035`. Current Pi `main` was refreshed to `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31` for terminal-image and theme inspection; this is scoped evidence, not a full upstream audit.
- The idle/active alternate-screen TUI has dark/light theme tokens, user/trusted-project theme discovery, `/settings` selection and `/reload`, OSC 10/11 appearance detection, and shared Markdown/footer/overlay styling. Embedded palettes match current Pi. Kitty/iTerm2 images have safe fallback; partial-viewport cropping and terminal cell-pixel probing remain open.

## What prevents parity

- TUI: themed tool blocks and extension call/result renderers, remaining mouse/editor behavior, syntax highlighting and Pi math layout; image cropping and terminal cell-size probing. CLI theme flags and settings-defined theme paths are absent.
- Sessions: Pi JSONL import/export/interoperability, crash recovery and concurrent access semantics.
- RPC/JSON/SDK: Pi-compatible framing, commands, events and process behavior.
- Settings/resources/extensions, multimodal breadth, provider/auth coverage and the full current-main audit remain incomplete. See the parity matrix and execution ledger.

## Priority and next action

1. Continue the TUI gate at tool rendering: compare current Pi's call/result renderer lifecycle, add a safe .NET extension renderer contract and themed tool-block presentation, then cover it with deterministic tests and a representative PTY flow. Keep screen lifecycle and composition separate from renderer callbacks.
2. After representative TUI process/PTY workflows, proceed to Pi JSONL session interoperability, RPC/JSON/SDK, settings/resources/extensions, remaining multimodal behavior, provider/auth breadth and the final current-main audit.
