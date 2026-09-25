# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest known-good committed head: `33b6ef7c323f01f7623d097da2464be91bab06bf`; Linux CI run [36145493341](https://github.com/PhilipGB/pisharp/actions/runs/36145493341) passed format verification, warnings-as-errors build and all 438 tests.
- The external-editor slice is committed: Ctrl+G launches the configured editor from a private temporary directory/file, supports trusted user/project `externalEditor` settings, and suspends/restores alternate-screen and raw terminal modes. A fork-picker PTY readiness race was fixed in `33b6ef7`; exact-head Linux CI passes 438/438.
- Current uncommitted slice adds text clipboard paste (Ctrl+V; Alt+V on Windows), copy-last-assistant (Ctrl+X and `/copy`), Linux/macOS/Windows helpers, and remote/headless OSC 52 fallback. Format verification and warnings-as-errors build pass; the full suite passes 447/447 with 0 skips. Exact-head Linux CI is pending.
- Pi reference: `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, refreshed 2026-09-25. Material deltas since `5fd446ca1843682e8da3fec4ceb71c42f56fbace` are classified in `docs/continuation.md`.

## What still prevents parity

- TUI: remaining settings schema and editor controls; transcript/editor selection and selection copy, clipboard image paste, WSL clipboard interop, fuller history/completion, mouse, themes, terminal images and extension UI; remaining Markdown dialect and rendering behavior.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Resources/extensions, multimodal behavior, provider/auth breadth and the final current-upstream audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Commit and push the locally validated text clipboard slice, then inspect exact-head Linux CI.
2. Continue the TUI gaps with transcript/editor selection and mouse interactions; then close the remaining major TUI capabilities before sessions, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before terminal-image/theme work, materially changed capability families and the final audit.
