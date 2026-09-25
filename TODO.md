# PiSharp capability parity

**Goal:** implement Pi’s in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless needed for a core capability.

**Stop condition:** a full audit against current Pi `main` finds no material in-scope gaps and required validation/differential evidence passes.

## Current checkpoint

- Exact code head `ff301f231d80b0ec81f093cf8d09d5b5b45b821c` is pushed. Linux CI run [36172481288](https://github.com/PhilipGB/pisharp/actions/runs/36172481288) passed restore, format verification, warnings-as-errors build, and all 491 tests.
- Durable Pi parity baseline remains `b3487650f6378f1b0d1643dd254445ceb4a98035`. Current Pi `main` was refreshed to `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31` before terminal-image work; this is scoped image evidence, not a full upstream audit.
- Interactive prompt and read-tool images render through bounded Kitty/iTerm2 presentation with safe fallbacks, Pi #8938 sizing, transcript-safe markers, and cleanup. Partial viewport images use text fallback; cell dimensions use the 9x18 default.

## What prevents parity

- TUI: theme tokens/settings, extension renderers, remaining mouse/editor behavior, syntax highlighting and Pi math layout; image cropping and terminal cell-size probing.
- Sessions: Pi JSONL import/export/interoperability, crash recovery and concurrent access semantics.
- RPC/JSON/SDK: Pi-compatible framing, commands, events and process behavior.
- Settings/resources/extensions, multimodal breadth, provider/auth coverage and the full current-main audit remain incomplete. See the parity matrix and execution ledger.

## Priority and next action

1. Refresh current Pi `main`; inspect terminal color/theme code, theme discovery/settings, and tests. Implement theme tokens and theme discovery, selection and reload across Markdown, status, tools, overlays and pickers. Keep the TUI gate open for extension presentation and representative PTY/process evidence.
2. Continue with Pi JSONL session interoperability, RPC/JSON/SDK, settings/resources/extensions, remaining multimodal behavior, provider/auth breadth and the final current-main audit.
