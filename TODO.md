# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, using Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages stay excluded unless needed for a core capability.

**Stop condition:** a full current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and major architecture bottlenecks are resolved.

## Current state

- Latest pushed, exact-head CI-validated source: `bfb37df4d9d75a7462df5c8884ee172467b3e5d2`; Linux CI run [36290986668](https://github.com/PhilipGB/pisharp/actions/runs/36290986668) passed format, warnings-as-errors build with 0 warnings/errors, and 621 tests (0 failed, 0 skipped). Its parent `d8ec4c8d6dc33e5e0a00bdb4b29f3fcc3703fcdd` failed 1/618 tests because the new fork process fixture queried session data while `agent_settled` had been emitted but the RPC task had not returned; the fixture now polls `get_state` until idle.
- The earlier `35be9dc8f8614dcdc66ceaab3136c8ad4301f406` run [36282903215](https://github.com/PhilipGB/pisharp/actions/runs/36282903215) failed 4/607 tests after format/build passed. Provider history reconciliation in `b71ed3119b3827a525515aa50d952f7b99d5a4f1` fixed the regressions; exact-head run [36286403870](https://github.com/PhilipGB/pisharp/actions/runs/36286403870) passed 612/612. `138820263` adds process-level abort lifecycle coverage and passed 613/613.
- Current Pi `main` was refreshed on 2026-09-27 and remains `2b0a123de98318c2ff8069661721ce0c3794c34e`; durable parity baseline: `b3487650f6378f1b0d1643dd254445ceb4a98035`.
- RPC model selection now uses the extracted `ModelRuntimeController`; model listing and `set_model` work during a blocked tool turn, and a process fixture verifies the next provider request uses the selected model. `cycle_model` shares the transition path; busy-cycle-specific process coverage, per-model thinking application and current-Pi process differentials remain open.
- RPC abort process coverage verifies partial assistant output, `turn_end < agent_end < agent_settled < abort response`, `willRetry: false`, and consistent snapshots. Current Pi source and tests were inspected; no Pi process differential has been run.
- `--fork <path>` across project boundaries is implemented: PiSharp loads native or Pi JSONL sources, preserves every branch and the selected head, and writes the child into the invocation project. Focused tree and cross-project fork tests passed 10/10; exact-head CI run `36288246889` passed all 616 tests.
- RPC `switch_session` prevalidates existing targets before cancelling active work and now matches Pi's missing-path behavior: an absent `.jsonl` path becomes a lazy Pi JSONL session in the invocation project. A blocked-provider process test verifies abort settlement before the switch response, no file creation until the next prompt, and subsequent prompt persistence to the replacement session. Prompts and explicit RPC saves now use the current replacement `ConversationRun`; previously they could keep running/saving through the original run. `PiJsonlSessionFileStore` uses a fenced atomic writer shared with `ConversationStore` and refuses external replacement. Exact-head CI run `36290986668` passed all 621 tests.
- `RpcSessionReplacementProcessTests.ForkDuringBlockedRunSettlesBeforeTheResponseAndOmitsTheAbortedTurn` verifies `turn_end < agent_end < agent_settled < fork response` and excludes the interrupted turn from the replacement branch. Current Pi regression `8724-in-memory-fork-active-tool` still lacks a matching PiSharp blocked-tool process test.
- RPC clone now snapshots the current branch and stages its session file before cancelling the active run, matching Pi's `fork(currentLeaf, { position: "at" })` order. `ConversationSession.ForkForSessionReplacement` keeps accepted conversation messages while omitting the unfinished run's internal recovery checkpoints. A blocked-provider process test verifies clone contents and settlement order; a second verifies a missing source returns Pi's error while the active run continues. Focused tests pass 2/2; local format/build are clean and the latest full suite passes 623/623. One preceding full run had a single `CompactionTests.MultiCycleParallelToolGraphSplitsOnlyAfterCompletedBatches` failure (expected 3 provider requests, observed 4); its focused rerun and the next full run passed. Exact-head CI is pending.
- No overall parity claim. Major gaps remain in RPC differentials, session recovery/concurrency, settings/resources/extensions, multimodal/provider/auth breadth, coding tools and TUI behavior.
- Architecture remains concentrated: `Program.cs` is about 1,160 lines, `ConversationRun.cs` about 944 lines, and `RpcMode.cs` 517 lines. Extract cohesive responsibilities when the next capability touches them.

## Current priority

Continue active RPC session replacement comparisons, starting with the missing blocked-tool fork process case; then continue session interoperability and recovery against current Pi. Do not pause at documentation or CI checkpoints.

## Exact next action

Push the active-clone snapshot/preflight slice and inspect exact-head CI. Then add the matching active-tool fork process case against Pi regression `8724-in-memory-fork-active-tool`, followed by session crash/concurrency recovery. No Pi process differential has been run.

## Architecture and residuals to preserve

- Keep `Program.cs` a composition root; extract behavior at the boundary required by the next capability.
- `ConversationRun.cs` remains the largest concentration risk; prefer cohesive queue, retry, compaction or provider-turn boundaries over mechanical splitting.
- `PiJsonlSessionInterchange.cs` remains large; separate import/migration, mapping, export or I/O only when session interoperability requires it.
- Keep RPC capability tests in focused classes rather than growing `RpcModeTests.cs` further.
- Preserve tracked TUI gaps: word-aware wrapping, shared ANSI-aware visual-row layout, and nested Markdown style restoration. Current Pi system-theme/OSC palette updates are tracked for the theme/final-audit pass.
- Before final parity, reread the feature matrix and ledger, audit current Pi `main`, collect required behavioral/differential evidence, and confirm exact-head CI.
