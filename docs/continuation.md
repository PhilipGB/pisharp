# Continuation handoff — partial milestone (NOT Pi parity)

## Reference and verification

- Workspace `/home/philip/Documents/projects/dotnet/pisharp`, branch `main`, remote `origin`. Normative reference `/tmp/pisharp-upstream` at `002fc8385268300ca91a5fc95f935c2afbbdac02` (reported version 0.87.1). Pi Packages are the **only** planned exclusion.
- Upstream pinned `npx vitest run packages/coding-agent/test/tools.test.ts --reporter=dot`: 84/84 passed. Pinned edit-planner results recorded in `docs/parity/fixtures/edit-baseline.md`. Only narrow fixtures match locally; `docs/parity/feature-matrix.md` marks **no whole feature Verified**. Inventory in `docs/parity/detailed-inventory.md` is incomplete.
- At this checkpoint: `dotnet format PiSharp.slnx --no-restore`, `dotnet build PiSharp.slnx --no-restore --warnaserror` passed with zero warnings, and `dotnet test PiSharp.slnx --no-build --no-restore --logger 'console;verbosity=normal'` passed **26/26** (including killed-descendant shell test and pinned v3 projection fixtures). Re-run after further edits.
- On 2026-09-23, `192.168.0.97:8000/v1/models` advertised `Qwen3.8-27B-GGUF` as loaded. Live `--local --print` text returned `PISHARP_LIVE_OK`; model ran bash `printf PISHARP_TOOL_OK`; default snapshot saved prompt `Remember the secret word BLUEBERRY` and `--continue` on a new process answered `BLUEBERRY`. Separate `--no-session` live run in `/tmp/pisharp-live-4OlWIY` performed write→read→batched edit→bash, and `sample.txt` really contained `ALPHA BETA\n`. These are narrow demonstrations, not upstream parity tests. Never send `OPENAI_API_KEY` to custom endpoints.

## Current architecture and deficits

- `src/PiSharp.Core`: edit planner, append-only tree, v3 Pi JSONL codec and compaction/context-edit-aware branch projection. Two small hand-authored branch fixtures compared to pinned upstream `buildSessionContext`; private atomic JSONL file adapter in Runtime. **Still not wired to the agent/CLI, v1/v2 migration absent, and these narrow fixtures do not establish Pi session parity.**
- `src/PiSharp.Runtime`: MAF `ChatClientAgent`, four default `AIFunction` tools, per-path write/edit serialization, bounded shell-output tail/private full-output spill, Linux setsid-based process-group abort/timeout, preliminary MAF session snapshots with atomic file replacement. Snapshots save only after completed turns and are **not Pi JSONL**, have no branching and no recovery of incomplete tool calls. Scripted-provider and HTTP/SSE tests use no credentials.
- `src/PiSharp.Cli`: OpenAI SDK Chat Completions client, local `.97` opt-in, `--print`, line-oriented REPL, `--continue`/`--session <existing>`/`--no-session`. All modes use the same `PiAgent`/session mechanism. **No actual TUI**, multimodal input, keyboard editor, JSON/RPC protocols, provider discovery/auth, project trust, settings, compaction, commands (other than `/quit`), skills/templates/themes, extension API or Pi-compatible session store. Built-in optional grep/find/ls and full read/edit/bash contracts are not yet complete. Do not claim usability for untrusted projects.
- Important shell-output fix: after the shell exits, pipe draining is covered by the timeout because background descendants can inherit the pipes. Test kills descendant after shell exits. Any further shell changes should preserve this.

## Next concrete work (keep progressing)

1. Audit current work (`git status`, `git diff`); rerun format/build/tests, then commit/push a coherent milestone. Prior pushed commit was `12f3499`. Update README/matrix on each slice.
2. Expand the experimental **Pi v3 JSONL codec/projection** beyond two fixtures, add v1/v2 migrations and full error/partial-write contracts, then bridge active-branch messages/tool calls into MAF history without retaining abandoned branches. Only then switch CLI from the separate MAF snapshot. Implement `/tree`, `/fork`, `/clone`, `/resume`, `/new` on that foundation. Test model tool-call state after save/restart, not just text.
3. Finish read/edit/bash conformance (image read, UTF-8 boundaries, output/diff/error rendering, cancellation), implement optional grep/find/ls and `--tools` controls, then compare exact upstream outputs. `rg` and `fd` are not installed on this host; choose a managed implementation or explicit dependency, do not assume they exist.
4. Implement real Linux terminal rendering/editor with pseudo-TTY tests, then JSON/RPC adapters on **the same MAF runtime**. Complete detailed upstream inventory, providers/settings/resources/extensions; test side-effectful flows deterministically without credentials. Opt-in live runs remain separate.

## Reproduction

```sh
git -C /tmp/pisharp-upstream rev-parse HEAD
dotnet restore PiSharp.slnx
dotnet format PiSharp.slnx --verify-no-changes --no-restore
dotnet build PiSharp.slnx --warnaserror
dotnet test PiSharp.slnx --no-build --logger 'console;verbosity=normal'
dotnet run --project src/PiSharp.Cli -- --local --no-session --print 'Reply hello'
```

This is an intermediate, incomplete implementation. Keep the feature matrix honest; Verified requires an observable comparison with the pinned upstream.
