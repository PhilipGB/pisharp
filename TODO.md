# PiSharp capability parity

**Goal:** implement Pi’s in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless needed for a core capability.

**Stop condition:** a full audit against current Pi `main` finds no material in-scope gaps and required validation/differential evidence passes.

## Current checkpoint

- Exact code head `e50ea5786038f5657869e1a881ceaec028c263d0` is pushed. Linux CI run [36182423444](https://github.com/PhilipGB/pisharp/actions/runs/36182423444) passed restore, format verification, warnings-as-errors build, and all 510 tests (0 failed, 0 skipped). Local format, build and full-suite validation also passed on this tree.
- Durable Pi parity baseline remains `b3487650f6378f1b0d1643dd254445ceb4a98035`. Current Pi `main` was refreshed to `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31` for terminal-image, theme and session inspection; this remains scoped evidence, not a full upstream audit.
- Major TUI workflows now include active/idle screen composition, Markdig AST rendering, cell-aware wrapping, scroll/search, mouse selection, modal model/session/settings pickers, themes, images, and safe themed extension tool-call/result renderers. Provider-backed Linux PTY evidence covers an extension tool round trip and terminal restoration. Residuals include syntax highlighting/math layout, partial-viewport image cropping and cell-size probing, theme CLI/search-path settings, richer extension widgets, and finer mouse interactions.

## What prevents parity

- Sessions: Pi JSONL import/export/interoperability, crash recovery and concurrent access semantics.
- RPC/JSON/SDK: Pi-compatible framing, commands, events and process behavior.
- Settings/resources/extensions, multimodal breadth, provider/auth coverage, listed TUI residuals, and the full current-main audit remain incomplete. See the parity matrix and execution ledger.

## Priority and next action

1. Implement current Pi’s `/import <path.jsonl>` capability: parse and preserve Pi session trees, replace the active session safely, and cover current-main confirmation/path/CWD behavior with deterministic tests. Add Pi JSONL export where needed for round-trip interoperability.
2. Continue through RPC/JSON/SDK, settings/resources/extensions, remaining multimodal behavior and provider/auth breadth. Carry the recorded TUI residuals into the final audit; do not restart broad TUI polish unless a material capability or differential requires it.
