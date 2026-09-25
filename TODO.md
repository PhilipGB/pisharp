# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest known-good committed head: `93457c9bbb939f4275f419b2d17d29dd2a552e51`; Linux CI run [36137560145](https://github.com/PhilipGB/pisharp/actions/runs/36137560145) passed format verification, warnings-as-errors build and all 424 tests.
- The current uncommitted `/settings` slice adds a shared searchable overlay, user/trusted-project scope, inheritance, atomic JSON updates and configurable `app.settings.open` for a supported scalar subset. Local format, build and 429 tests pass; commit and exact-head CI are pending.
- Pi reference: `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, refreshed 2026-09-25. Material deltas since `5fd446ca1843682e8da3fec4ceb71c42f56fbace` are classified in `docs/continuation.md`.

## What still prevents parity

- TUI: remaining settings schema and editor controls; editor selection, clipboard, external editor, history and completion; cross-project session controls; wider context-aware keybindings; mouse/selection; themes, terminal images and extension UI; remaining Markdown dialect and rendering behavior.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Resources/extensions, multimodal behavior, provider/auth breadth and the final current-upstream audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Commit and push the locally validated `/settings` slice, then inspect exact-head Linux CI.
2. Continue the TUI editor/application gaps, starting with editor completion and external-editor behavior; proceed to sessions, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth after the TUI acceptance gate. Refresh Pi before terminal-image/theme work, materially changed capability families and the final audit.
