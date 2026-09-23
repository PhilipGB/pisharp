# Continuation — incomplete PiSharp implementation

## Baseline and evidence

- Pinned upstream: `earendil-works/pi@002fc8385268300ca91a5fc95f935c2afbbdac02` in `/tmp/pisharp-upstream`. Pi Packages are the only *planned* exclusion. Do not call the application Pi-compatible yet.
- The upstream pinned tool suite passed 84/84. Narrow upstream edit/read/ls and historical Pi JSONL fixtures remain in `docs/parity/fixtures`; their previous experimental JSONL implementation was removed to avoid maintaining competing durable histories. **The new canonical format is PiSharp-specific, not Pi JSONL.** Historical fixtures are archival evidence only and no longer executed.
- Current canonical sessions: `ConversationSession` + `ConversationStore` + `ConversationRun` in `src/PiSharp.Runtime/Sessions`. Project-scoped private `.session.json`, atomic replacement, explicit selected branch, MAF history reconstructed from selected path. CLI `--continue`, `--session`, `--no-session`, `/tree`, `/branch`, `/fork`, `/new`, `/name`, `/session`, `/model` use that one history. The former linear MAF snapshot and unused Pi JSONL codec were removed. **Old snapshot files are not migrated.** A tool call/result round trip survives restart without replay. Function failures are persisted explicitly because Microsoft.Extensions.AI intentionally does not serialize exceptions. Cancellation records the prompt, emitted text and tool event summaries in an interruption marker, but sudden process crashes may lose the in-flight turn; stronger recovery still needed. Bare v1 text-only sessions can migrate; ambiguous v1 tool turns fail closed. Store saves now use advisory locking plus optimistic conflict detection; stale concurrent writers are rejected.
- Tools: failed read/write/edit/bash/ls now throw `ToolFailureException`, which MAF exposes as a failing function result; bash cancellation propagates. Large UTF-8 text reads now scan the entire file for line counts while retaining only the bounded output window; differential tests compare them with the in-memory pinned text planner. Same-directory atomic write/edit replaces target while preserving Linux mode and following existing symlinks. Bash output remains bounded with private full-output spill; child process group is killed even if shell exits first. Deterministic tests cover tool failure reaching the model, serialization after restart, cancellation, branches and file operations. Exact upstream error/update event parity is **not** established.
- The local model endpoint `http://192.168.0.97:8000/v1` advertised `Qwen3.8-27B-GGUF` loaded. A separate-process `--local --print` then `--continue` smoke run in `/tmp/pisharp-canonical-live-emXetY` recalled `VIOLET-KITE`. This is NOT an upstream differential test; no live credentials should be part of CI.
- At this checkpoint format verification and warning-as-error build passed with zero warnings; **51/51** deterministic tests passed, including a Linux pseudo-TTY interaction test. Opt-in `.97` JSON/RPC calls returned `PISHARP_JSON_OK` and `PISHARP_RPC_OK`. RPC now also exposes idle-only tree/entries/text/name operations, but still uses `format: pisharp` rather than Pi's wire contract. Full parity remains outstanding.

## Remaining priority

1. Audit session/incomplete-turn crash recovery, corrupted and network-filesystem stores, model/provider switching and migration policy. Add recovery fixtures and PTY integration; ensure no silent history loss. Improve terminal session controls.
2. Build real Linux VT terminal/editor with raw-mode input, multiline editing, history, shortcuts, paste and resize, pseudo-TTY tests; the CLI has only a **basic single-line viewport**, not a full TUI.
3. Opt-in managed grep/find exist with narrow local tests; finish upstream differential fixtures, glob/ignore/large-file/UTF-8 conformance. This host lacks `rg`/`fd`; Git supplies ignore rules in repositories and fallback skips common dependency trees. Complete image read, bash events, multimodal and file bounds.
4. Expand experimental JSON/RPC into Pi-compatible schemas and all commands/events (current subset only), then provider/auth/settings, model catalog, compaction, skills/templates/themes, project trust, extension API, complete feature inventory. Update `docs/parity/feature-matrix.md` only with evidence.
5. Project context AGENTS/CLAUDE files now reach MAF with size bounds and override precedence, but project trust, settings and reload are missing; test against pinned Pi. Keep committing tested slices. No whole feature is Verified.

## Reproduce

```sh
git -C /tmp/pisharp-upstream rev-parse HEAD
dotnet restore PiSharp.slnx
dotnet format PiSharp.slnx --verify-no-changes --no-restore
dotnet build PiSharp.slnx --warnaserror
dotnet test PiSharp.slnx --no-build
dotnet run --project src/PiSharp.Cli -- --local --no-session --print 'Reply hello'
```
