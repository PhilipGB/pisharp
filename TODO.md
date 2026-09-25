# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless needed for a core capability.

**Stop condition:** a full audit against current Pi `main` finds no material in-scope gaps and required validation/differential evidence passes.

## Current checkpoint

- Last green source head `ab898e974712144a1f93ebdab587c8f60141e022` passed Linux CI [36198706518](https://github.com/PhilipGB/pisharp/actions/runs/36198706518): format, warnings-as-errors build with 0 warnings/errors, 535 tests (0 failed, 0 skipped). The listener retry race exposed by [36198169246](https://github.com/PhilipGB/pisharp/actions/runs/36198169246) is fixed by using a fresh listener per bind attempt. Current local `agent_start`/`agent_settled` projections pass format, build (0 warnings/errors), 535/535 tests and focused RPC tests 22/22; exact-head CI is pending.
- Durable Pi parity baseline remains `b3487650f6378f1b0d1643dd254445ceb4a98035`; current Pi `main` was refreshed to `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31` for scoped image, theme, session and RPC inspection. This is not a full audit.
- Pi JSONL v1-v3 import and v3 export now preserve branches, active head, context edits, compactions and unknown entries; startup `--session`, `/import` and `/export-jsonl` are covered. Import currently requires the source CWD to equal the running project directory.
- The TUI has a persistent active/idle screen, Markdig AST rendering, shared cell-aware wrapping, transcript scrolling/search, modal pickers, mouse selection, themes, image output and safe extension tool renderers. Recorded residuals and the broader TUI differential remain open.

## What prevents parity

- Sessions still lack Pi-equivalent cross-project import/switching, complete crash recovery and concurrent access semantics; tree/session commands and export need broader differential evidence.
- RPC/JSON/SDK remain materially wire-incompatible: most event payloads, message normalization, session controls and process behavior differ from Pi. Thinking changes, queue snapshots, `agent_start` and `agent_settled` now have Pi-shaped RPC events locally; `agent_end` payload/retry semantics, active-run thinking changes, per-model/provider maps and the remaining turn/message/tool/session event families remain open.
- Settings/resources/extensions, multimodal breadth, provider/auth coverage, listed TUI residuals and the full current-main audit remain incomplete.

## Priority and next action

1. Commit and push the locally validated `agent_start` projection and preflight event handling; confirm exact-head Linux CI, then implement Pi-shaped `agent_end` with message and retry data.
2. Continue sessions/interoperability, settings/resources/extensions, multimodal and provider/auth breadth. Carry the recorded TUI residuals into the final audit; avoid broad TUI polish without a material capability gap or differential.
