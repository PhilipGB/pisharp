# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest known-good committed head: `f87caad457353056712bff08c03c90ae3585a3db`; Linux CI run [36141556326](https://github.com/PhilipGB/pisharp/actions/runs/36141556326) passed format verification, warnings-as-errors build and all 431 tests.
- The current uncommitted editor slice adds Ctrl+G external-editor handoff through a private temporary directory/file, trusted user/project setting support and alternate-screen/raw-mode suspend/restore. Focused tests pass 9/9; format verification and warnings-as-errors build pass; the full suite passes 438/438 with 0 skips. Commit and exact-head CI are pending.
- Pi reference: `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, refreshed 2026-09-25. Material deltas since `5fd446ca1843682e8da3fec4ceb71c42f56fbace` are classified in `docs/continuation.md`.

## What still prevents parity

- TUI: remaining settings schema and editor controls; editor selection, clipboard, image paste and fuller history/completion; cross-project session controls; wider context-aware keybindings; mouse/selection; themes, terminal images and extension UI; remaining Markdown dialect and rendering behavior.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Resources/extensions, multimodal behavior, provider/auth breadth and the final current-upstream audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Finish format/build/full-suite validation for the external-editor slice, then commit, push and inspect exact-head Linux CI.
2. Continue the TUI editor/application gaps with selection and clipboard, then close the remaining major TUI capabilities before sessions, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before terminal-image/theme work, materially changed capability families and the final audit.
