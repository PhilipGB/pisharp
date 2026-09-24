# PiSharp capability parity — durable execution state

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, with behavioral and differential evidence. Pi Packages are excluded unless needed for a core capability.

**Stop condition:** a full parity audit against current Pi `main` finds no material in-scope capability gaps, and required validation and differential workflows pass.

## Baseline

- PiSharp started from clean `main` at `1520f311196fdbd6a3f2eae07f09bc3f80e55353`. The latest successful Linux CI run before this image slice is [run 36059356140](https://github.com/PhilipGB/pisharp/actions/runs/36059356140), for `e15f051d989ada94ccd54bb3ae7535b20c2a0c9f` on 2026-09-24.
- Pinned Pi: `earendil-works/pi@d5629e20489ccf770ed90b5a33941cb3b7ef24d0`. Refreshed upstream `main` as `b2bd111f2d46eed1a4689c32f30fde6306498827`; pinned/current read, MIME, and image processing sources and tests are unchanged. Refresh before the final audit and relevant capability families.
- Current image-read changes are uncommitted. `dotnet format PiSharp.slnx --verify-no-changes --no-restore` passed; the Release warnings-as-errors build passed with 0 warnings/errors; the Release suite passed 307/307 with 0 skipped; focused read/image tests passed 10/10. CI has not run for these edits.

## Priorities

1. **Coding tools (active):** complete `read`, `bash`, `edit`, `write`, `grep`, `find`, and `ls` contracts using upstream implementation/tests, deterministic local cases, pinned differentials, and real agent turns. Once major behavior is covered, move on rather than polishing obscure edge cases.
   - `read`: small and streamed text paths decode UTF-8 consistently, preserve BOMs, and avoid UTF-16 BOM auto-detection. Image processing now decodes and validates, converts BMP to PNG, resizes to 2000×2000 / 4.5 MiB base64 limits, and sends inline content through local Responses and Chat Completions tool turns; high-entropy provider payload tests cover the byte limit and session reload. Still open: EXIF orientation, Lanczos3 equivalence, model-specific resize profiles, files over 20 MiB, animated-image transformations, exact metadata/errors, broader filesystem/cancellation cases, and pinned image request/result differentials.
   - `bash`: high priority. Still open: live output, exact result/events, stdout/stderr behavior, full-output retention, process tree cleanup, and broad PTY/agent-turn evidence.
   - `edit`, `write`, `grep`, `find`, `ls`: compare remaining matching, encoding, symlink, permission, atomicity, glob/ignore, truncation, ordering, cancellation and result details against Pi. Existing local coverage is narrow.
   - Acceptance gate: all seven tools' major externally visible behavior is covered and remaining differences are evidenced; then begin the real TUI.
2. **TUI/editor/keybindings:** replace the editor-only UX with a separated terminal, input/keymap, transcript, renderer, status, overlay/picker and application architecture. Cover streaming, reasoning, Markdown/code/tools, resize, scroll/search, Unicode, clipboard, external editor, images, and terminal restoration.
3. **Sessions, RPC/JSON/SDK, settings/resources/themes/extensions and multimodal:** preserve Pi capabilities across PiSharp's internal architecture; implement Pi JSONL and wire-compatible protocol where upstream provides them. Keep capability ownership and trust boundaries explicit.
4. **Provider/auth breadth:** after tools and TUI, add native provider/auth semantics from current Pi; maintain OpenAI Responses and llama.cpp-compatible Chat Completions throughout.
5. **Final audit:** refresh upstream SHA; audit every in-scope CLI, command, agent, tool, TUI, keybinding, session, protocol, settings, provider, image, resource, theme, extension, recovery and error contract. Reconcile the matrix and ledger; run format, warnings-as-errors build, all tests, differentials, representative end-to-end workflows and Linux CI.

## Working decisions

- Agent execution and tools share one Microsoft Agent Framework runtime across interactive, print, JSON, RPC and SDK surfaces. Do not move permanent product orchestration into `Program.cs`.
- Preserve canonical raw history and durable tool outcomes. Never replay uncertain side effects or claim verification from code presence alone.
- Core loop, recovery and compaction already have substantial deterministic evidence. Revisit remaining core edge cases when a new capability exposes a defect, a pinned differential requires it, or final audit shows a material gap; do not spend the next stretch on narrower compaction cases.
- After each meaningful slice: add deterministic tests, update parity evidence and this file, run focused validation, run broader validation as appropriate, commit and push, then continue to the next unresolved capability.

## Immediate next action

Commit and push this validated read slice; retain its evidenced image differences for final audit. Then start `bash` live-output/result/event parity with process-group, cancellation, truncation and agent-turn fixtures. Add deterministic comparisons and keep the active matrix/ledger aligned.
