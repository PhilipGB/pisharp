# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest known-good committed head: `3a587ebb95e6b51a227838ecd3735c04995f3bb9`; Linux CI run [36139848340](https://github.com/PhilipGB/pisharp/actions/runs/36139848340) passed format verification, warnings-as-errors build and all 429 tests.
- The current uncommitted editor-completion slice adds Pi-style path token handling, quoted directory continuation, wrapper and CJK boundaries, hidden-file prefixing and grapheme-safe common prefixes. Focused tests pass 4/4 and full local format/build/test passes 431/431; commit and exact-head CI are pending.
- Pi reference: `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, refreshed 2026-09-25. Material deltas since `5fd446ca1843682e8da3fec4ceb71c42f56fbace` are classified in `docs/continuation.md`.

## What still prevents parity

- TUI: remaining settings schema and editor controls; editor selection, clipboard, external editor and fuller history/completion; cross-project session controls; wider context-aware keybindings; mouse/selection; themes, terminal images and extension UI; remaining Markdown dialect and rendering behavior.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Resources/extensions, multimodal behavior, provider/auth breadth and the final current-upstream audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Commit and push the locally validated path-completion slice, then inspect exact-head Linux CI.
2. Continue the TUI editor/application gaps, starting with external-editor behavior and editor selection; proceed to sessions, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth after the TUI acceptance gate. Refresh Pi before terminal-image/theme work, materially changed capability families and the final audit.
