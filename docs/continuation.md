# Continuation — incomplete PiSharp implementation

## Baseline and evidence

- Pinned upstream: `earendil-works/pi@002fc8385268300ca91a5fc95f935c2afbbdac02` in `/tmp/pisharp-upstream`. Pi Packages are the only *planned* exclusion. Do not call the application Pi-compatible yet.
- The upstream pinned tool suite passed 84/84. Narrow upstream edit/read/ls and historical Pi JSONL fixtures remain in `docs/parity/fixtures`; their previous experimental JSONL implementation was removed to avoid maintaining competing durable histories. **The new canonical format is PiSharp-specific, not Pi JSONL.** Historical fixtures are archival evidence only and no longer executed.
- Current canonical sessions: `ConversationSession` + `ConversationStore` + `ConversationRun` in `src/PiSharp.Runtime/Sessions`. Project-scoped private `.session.json`, atomic replacement, explicit selected branch, MAF history reconstructed from selected path. CLI `--continue`, `--session`, `--no-session`, `/tree`, `/branch`, `/fork`, `/new`, `/name`, `/session` use that one history. The former linear MAF snapshot and unused Pi JSONL codec were removed. **Old snapshot files are not migrated.** A tool call/result round trip survives restart without replay. Function failures are persisted explicitly because Microsoft.Extensions.AI intentionally does not serialize exceptions. Cancellation records an interruption marker, but partially emitted provider messages may not be in MAF history; stronger recovery still needed.
- Tools: failed read/write/edit/bash/ls now throw `ToolFailureException`, which MAF exposes as a failing function result; bash cancellation propagates. Same-directory atomic write/edit replaces target while preserving Linux mode and following existing symlinks. Bash output remains bounded with private full-output spill; child process group is killed even if shell exits first. Deterministic tests cover tool failure reaching the model, serialization after restart, cancellation, branches and file operations. Exact upstream error/update event parity is **not** established.
- The local model endpoint `http://192.168.0.97:8000/v1` advertised `Qwen3.8-27B-GGUF` loaded. A separate-process `--local --print` then `--continue` smoke run in `/tmp/pisharp-canonical-live-emXetY` recalled `VIOLET-KITE`. This is NOT an upstream differential test; no live credentials should be part of CI.
- At this checkpoint, `dotnet format PiSharp.slnx --verify-no-changes --no-restore` passed; warning-as-error build passed with zero warnings; `dotnet test PiSharp.slnx --no-build --no-restore` passed **31/31**. Re-run after further edits.

## Remaining priority

1. Audit session/incomplete-turn recovery, corrupted and concurrent stores, model/provider switching, tool-call failure persistence and migration policy. Add recovery fixtures and PTY integration; ensure no silent history loss. Improve terminal session controls.
2. Build real Linux VT terminal/editor with raw-mode input, multiline editing, history, shortcuts, paste and resize, pseudo-TTY tests; the CLI is still a **line-based REPL**.
3. Add opt-in grep/find (this host lacks `rg`/`fd`), complete read image, bash event output, file read bounds, multimodal and exact tool contracts against pinned upstream.
4. Implement JSON and RPC on the same `ChatClientAgent`/canonical sessions, provider/auth/settings, model catalog, compaction, skills/templates/themes, project trust, extension API, complete feature inventory. Update `docs/parity/feature-matrix.md` only with evidence.
5. Keep committing and pushing coherent tested slices. No whole feature has been marked Verified.

## Reproduce

```sh
git -C /tmp/pisharp-upstream rev-parse HEAD
dotnet restore PiSharp.slnx
dotnet format PiSharp.slnx --verify-no-changes --no-restore
dotnet build PiSharp.slnx --warnaserror
dotnet test PiSharp.slnx --no-build
dotnet run --project src/PiSharp.Cli -- --local --no-session --print 'Reply hello'
```
