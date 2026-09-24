# PiSharp capability parity — durable execution state

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, with behavioral and differential evidence. Pi Packages are excluded unless needed for a core capability.

**Stop condition:** a full parity audit against current Pi `main` finds no material in-scope capability gaps, and required validation and differential workflows pass.

## Baseline

- PiSharp started from clean `main` at `1520f311196fdbd6a3f2eae07f09bc3f80e55353`. Its latest Linux CI run succeeded: [run 36053525565](https://github.com/PhilipGB/pisharp/actions/runs/36053525565), 2026-09-24.
- Pinned Pi: `earendil-works/pi@d5629e20489ccf770ed90b5a33941cb3b7ef24d0`. Current upstream `main` fetched as `19a0361be89bf78ccf9bbaed9a496d6484759f67`; coding-agent tool sources and tests are unchanged since the pin. Refresh before final audit and before major capability families when relevant.
- Current local changes are uncommitted. `dotnet format PiSharp.slnx --verify-no-changes` passed; `dotnet build PiSharp.slnx --warnaserror` passed with 0 warnings/errors; `dotnet test PiSharp.slnx` passed 297/297. The focused read tests passed 9/9. CI has not run for these edits.

## Priorities

1. **Coding tools (active):** complete `read`, `bash`, `edit`, `write`, `grep`, `find`, and `ls` contracts using upstream implementation/tests, deterministic local cases, pinned differentials, and real agent turns. Once major behavior is covered, move on rather than polishing obscure edge cases.
   - `read`: small and streamed text paths now decode UTF-8 consistently, preserve BOMs, and avoid UTF-16 BOM auto-detection. Focused tests pass. Still open: image detection/processing and image content through the model loop, exact metadata/errors/cancellation, and broader edge-case differentials.
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

Implement Pi-compatible image reads through the real MAF tool loop. First verify how the installed `FunctionResultContent` and OpenAI/llama adapters carry image content; then add a deterministic agent-turn fixture proving the next supported multimodal request contains the image and persisted history remains usable. Continue with image processing/format coverage and then the `bash` contract.
