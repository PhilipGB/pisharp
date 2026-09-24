# PiSharp capability parity — durable execution state

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, with behavioral and differential evidence. Pi Packages are excluded unless needed for a core capability.

**Stop condition:** a full parity audit against current Pi `main` finds no material in-scope capability gaps, and required validation and differential workflows pass.

## Baseline

- PiSharp started from clean `main` at `1520f311196fdbd6a3f2eae07f09bc3f80e55353`. The latest successful Linux CI run is [run 36072095462](https://github.com/PhilipGB/pisharp/actions/runs/36072095462), for `99943d16cfada33fd926429a9988dd123f92d151` on 2026-09-24; it covers the pushed edit matching/error slice.
- Pinned Pi: `earendil-works/pi@d5629e20489ccf770ed90b5a33941cb3b7ef24d0`. Refreshed upstream `main` as `b2bd111f2d46eed1a4689c32f30fde6306498827`; pinned/current read, MIME, and image processing sources and tests are unchanged. Refresh before the final audit and relevant capability families.
- Read/image processing is committed at `8b109ecac49d1c88df852186c52006553d6231e9`; its Release suite passed 307/307 with 0 skipped, focused read/image tests passed 10/10, and the linked Linux CI run succeeded. Image differences remain listed in the matrix and fixture notes.

## Priorities

1. **Coding tools (active):** complete `read`, `bash`, `edit`, `write`, `grep`, `find`, and `ls` contracts using upstream implementation/tests, deterministic local cases, pinned differentials, and real agent turns. Once major behavior is covered, move on rather than polishing obscure edge cases.
   - `read`: small and streamed text paths decode UTF-8 consistently, preserve BOMs, and avoid UTF-16 BOM auto-detection. Image processing now decodes and validates, converts BMP to PNG, resizes to 2000×2000 / 4.5 MiB base64 limits, and sends inline content through local Responses and Chat Completions tool turns; high-entropy provider payload tests cover the byte limit and session reload. Still open: EXIF orientation, Lanczos3 equivalence, model-specific resize profiles, files over 20 MiB, animated-image transformations, exact metadata/errors, broader filesystem/cancellation cases, and pinned image request/result differentials.
   - `bash`: live text updates now flow through the shared lifecycle stream at a 100ms cadence; shell selection honors validated `shellPath`; combined stdout/stderr, inherited process environment, UTF-8 final flush, bounded tail/full spill, cwd and finite timeout validation, 100ms rearmed post-exit drain, and process-group timeout/abort cleanup have local unit, process, agent and JSON event evidence. The focused Bash set passed 13/13; the full Release suite passed 314/314 with 0 skipped; build and format checks passed. Still open: Pi session-derived `PI_*` variables, PTY/terminal presentation, direct RPC wire fixture, precise pinned result/error differentials, wider signal/platform behavior, and broad settings reload parity.
   - `edit`: exact/batched original-snapshot matching, BOM/CRLF, strict UTF-8, and Pi's ECMAScript trailing-whitespace set are covered; missing-target errors now match `ENOENT`, and success text matches. Focused conformance tests pass 9/9 and the full Release suite passes 317/317. Still open: Pi renderer `diff`, unified `patch`, `firstChangedLine`, permission/error details and broader pinned differential fixtures.
   - `write`: recursive parent creation, successful result text, same-file serialization, symlink following, cancellation and mode behavior have local evidence. Current Pi calls `fsWriteFile` directly with the platform default mode; PiSharp uses atomic replace, creates new Linux files as `0600`, and wraps filesystem errors. Hard-link behavior also differs. Focused source/test parity remains open.
   - `grep`, `find`, `ls`: the managed fallback now reads root and nested `.gitignore` wildcard/negation rules outside Git, matching Pi's reference case. SearchToolsTests pass 3/3; the full Release suite passes 318/318, format passes, and the warnings-as-errors build has 0 warnings/errors. Still open: `.ignore`/`.fdignore` and global excludes, fd/rg matching, traversal, truncation and renderer details, plus symlink/permission differentials.
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

Commit and push the outside-repository `.gitignore` fix, then continue the fd/rg and filesystem differential pass. Return to write error mapping and Bash direct RPC/result differentials during the broader parity pass.
