# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, using Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages stay excluded unless needed for a core capability.

**Stop condition:** a full current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and major architecture bottlenecks are resolved.

## Current state

- Latest pushed, exact-head CI-validated source: `e955daffa03e768115af29ae1c90fa7c06070373`; Linux CI run [36292314859](https://github.com/PhilipGB/pisharp/actions/runs/36292314859) passed format, warnings-as-errors build with 0 warnings/errors, and 623 tests (0 failed, 0 skipped). The active-clone slice and missing-source preflight are included. Its local validation had one transient earlier compaction request-count failure; focused rerun and the next full run passed.
- The active-tool fork slice adds `RpcActiveToolForkProcessTests`; its Linux CLI process case passed 1/1 locally. Exact-head CI for this slice is pending.
- The earlier `35be9dc8f8614dcdc66ceaab3136c8ad4301f406` run [36282903215](https://github.com/PhilipGB/pisharp/actions/runs/36282903215) failed 4/607 tests after format/build passed. Provider history reconciliation in `b71ed3119b3827a525515aa50d952f7b99d5a4f1` fixed the regressions; exact-head run [36286403870](https://github.com/PhilipGB/pisharp/actions/runs/36286403870) passed 612/612. `138820263` adds process-level abort lifecycle coverage and passed 613/613.
- Current Pi `main` was refreshed on 2026-09-27 and remains `2b0a123de98318c2ff8069661721ce0c3794c34e`; durable parity baseline: `b3487650f6378f1b0d1643dd254445ceb4a98035`.
- RPC model selection now uses the extracted `ModelRuntimeController`; model listing and `set_model` work during a blocked tool turn, and a process fixture verifies the next provider request uses the selected model. `cycle_model` shares the transition path; busy-cycle-specific process coverage, per-model thinking application and current-Pi process differentials remain open.
- RPC abort process coverage verifies partial assistant output, `turn_end < agent_end < agent_settled < abort response`, `willRetry: false`, and consistent snapshots. Current Pi source and tests were inspected; no Pi process differential has been run.
- `--fork <path>` across project boundaries is implemented: PiSharp loads native or Pi JSONL sources, preserves every branch and the selected head, and writes the child into the invocation project. Focused tree and cross-project fork tests passed 10/10; exact-head CI run `36288246889` passed all 616 tests.
- RPC `switch_session` prevalidates existing targets before cancelling active work and now matches Pi's missing-path behavior: an absent `.jsonl` path becomes a lazy Pi JSONL session in the invocation project. A blocked-provider process test verifies abort settlement before the switch response, no file creation until the next prompt, and subsequent prompt persistence to the replacement session. Prompts and explicit RPC saves now use the current replacement `ConversationRun`; previously they could keep running/saving through the original run. `PiJsonlSessionFileStore` uses a fenced atomic writer shared with `ConversationStore` and refuses external replacement. Exact-head CI run `36290986668` passed all 621 tests.
- `RpcSessionReplacementProcessTests.ForkDuringBlockedRunSettlesBeforeTheResponseAndOmitsTheAbortedTurn` verifies provider-stream fork settlement and branch isolation. `RpcActiveToolForkProcessTests` now blocks a real Bash tool during a CLI RPC prompt, forks, verifies the aborted `agent_end` and settlement-before-response order, and confirms the replacement transcript excludes the turn. Current Pi regression `8724-in-memory-fork-active-tool` was inspected; this is PiSharp process evidence, not a Pi process differential.
- RPC fork now captures the selected branch and prompt text before cancelling active work, then creates and adopts the replacement from that snapshot. RPC clone snapshots the current branch and stages its session file before cancelling the active run, matching Pi's `fork(currentLeaf, { position: "at" })` order. `ConversationSession.ForkForSessionReplacement` keeps accepted conversation messages while omitting the unfinished run's internal recovery checkpoints. Blocked-provider clone process tests verify branch contents and settlement order; a second verifies a missing source returns Pi's error while the active run continues. Focused clone tests pass 2/2 and active-tool fork passes 1/1 locally. The prior pushed source at e955daffa passed exact-head CI with 623/623; CI for the current fork slice is pending.
- No overall parity claim. Major gaps remain in RPC differentials, session recovery/concurrency, settings/resources/extensions, multimodal/provider/auth breadth, coding tools and TUI behavior.
- Architecture remains concentrated: `Program.cs` is about 1,160 lines, `ConversationRun.cs` about 944 lines, and `RpcMode.cs` 517 lines. Extract cohesive responsibilities when the next capability touches them.

## Current priority

Finish and exact-head-validate the active-tool fork slice, then continue session interoperability and recovery against current Pi. Do not pause at documentation or CI checkpoints.

## Exact next action

Commit and push the active-tool fork snapshot/process slice, inspect exact-head CI, then continue session crash/concurrency recovery. No Pi process differential has been run.

## Architecture and residuals to preserve

- Keep `Program.cs` a composition root; extract behavior at the boundary required by the next capability.
- `ConversationRun.cs` remains the largest concentration risk; prefer cohesive queue, retry, compaction or provider-turn boundaries over mechanical splitting.
- `PiJsonlSessionInterchange.cs` remains large; separate import/migration, mapping, export or I/O only when session interoperability requires it.
- Keep RPC capability tests in focused classes rather than growing `RpcModeTests.cs` further.
- Preserve tracked TUI gaps: word-aware wrapping, shared ANSI-aware visual-row layout, and nested Markdown style restoration. Current Pi system-theme/OSC palette updates are tracked for the theme/final-audit pass.
- Before final parity, reread the feature matrix and ledger, audit current Pi `main`, collect required behavioral/differential evidence, and confirm exact-head CI.
