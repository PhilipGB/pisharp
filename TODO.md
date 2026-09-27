# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, using Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages stay excluded unless needed for a core capability.

**Stop condition:** a full current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and major architecture bottlenecks are resolved.

## Current state

- Latest pushed, exact-head CI-validated source: `e3ab887613146c2a535be9ac5949308215616502`; Linux CI run [36289412915](https://github.com/PhilipGB/pisharp/actions/runs/36289412915) passed format, warnings-as-errors build with 0 warnings/errors, and 617 tests (0 failed, 0 skipped). This was a documentation-only follow-up to code commit `f509750da57bece3e64a3bcbb905e3852782afa4`, whose exact-head run [36289250296](https://github.com/PhilipGB/pisharp/actions/runs/36289250296) also passed all 617 tests.
- The earlier `35be9dc8f8614dcdc66ceaab3136c8ad4301f406` run [36282903215](https://github.com/PhilipGB/pisharp/actions/runs/36282903215) failed 4/607 tests after format/build passed. Provider history reconciliation in `b71ed3119b3827a525515aa50d952f7b99d5a4f1` fixed the regressions; exact-head run [36286403870](https://github.com/PhilipGB/pisharp/actions/runs/36286403870) passed 612/612. `138820263` adds process-level abort lifecycle coverage and passed 613/613.
- Current Pi `main` was refreshed on 2026-09-27 and remains `2b0a123de98318c2ff8069661721ce0c3794c34e`; durable parity baseline: `b3487650f6378f1b0d1643dd254445ceb4a98035`.
- RPC model selection now uses the extracted `ModelRuntimeController`; model listing and `set_model` work during a blocked tool turn, and a process fixture verifies the next provider request uses the selected model. `cycle_model` shares the transition path; busy-cycle-specific process coverage, per-model thinking application and current-Pi process differentials remain open.
- RPC abort process coverage verifies partial assistant output, `turn_end < agent_end < agent_settled < abort response`, `willRetry: false`, and consistent snapshots. Current Pi source and tests were inspected; no Pi process differential has been run.
- `--fork <path>` across project boundaries is implemented: PiSharp loads native or Pi JSONL sources, preserves every branch and the selected head, and writes the child into the invocation project. Focused tree and cross-project fork tests passed 10/10; exact-head CI run `36288246889` passed all 616 tests.
- RPC `switch_session` prevalidates existing session targets and their stored project directories before cancelling active work. Current Pi opens `SessionManager` and checks its CWD before teardown. `RpcSessionReplacementProcessTests.InvalidSwitchTargetDoesNotCancelAnActiveRun` uses a blocked loopback provider and verifies the malformed-target error precedes normal completion of the still-running prompt. Exact-head CI run `36289250296` passed 617/617 with format and warnings-as-errors build clean.
- Working-tree test slice: `RpcSessionReplacementProcessTests.ForkDuringBlockedRunSettlesBeforeTheResponseAndOmitsTheAbortedTurn` exercises `fork` during blocked provider streaming. It verifies `turn_end < agent_end < agent_settled < fork response` and that the replacement branch excludes the interrupted turn. Current Pi regression test `8724-in-memory-fork-active-tool` covers the related active-tool case; PiSharp's test currently blocks the provider stream, not a tool. Focused replacement tests pass 2/2, full format/build pass, and 618/618 tests pass locally; exact-head CI is pending.
- Remaining in this replacement slice: Pi treats a missing session path as a new session, while PiSharp currently reports a file error; active `fork`/`clone` failure and event-order behavior still need comparison. No current-Pi RPC process differential has been run.
- No overall parity claim. Major gaps remain in RPC differentials, session recovery/concurrency, settings/resources/extensions, multimodal/provider/auth breadth, coding tools and TUI behavior.
- Architecture remains concentrated: `Program.cs` is about 1,160 lines, `ConversationRun.cs` about 944 lines, and `RpcMode.cs` 517 lines. Extract cohesive responsibilities when the next capability touches them.

## Current priority

Continue active RPC session replacement comparisons, then session interoperability and recovery against current Pi. Do not pause at documentation or CI checkpoints.

## Exact next action

Commit and push the active-fork process test and evidence, then verify exact-head CI. Compare missing-target switching and active RPC `clone` failure/event ordering against current Pi `2b0a123de98318c2ff8069661721ce0c3794c34e`; implement and process-test the highest-value remaining difference. Then continue into session recovery/concurrency and later capability groups.

## Architecture and residuals to preserve

- Keep `Program.cs` a composition root; extract behavior at the boundary required by the next capability.
- `ConversationRun.cs` remains the largest concentration risk; prefer cohesive queue, retry, compaction or provider-turn boundaries over mechanical splitting.
- `PiJsonlSessionInterchange.cs` remains large; separate import/migration, mapping, export or I/O only when session interoperability requires it.
- Keep RPC capability tests in focused classes rather than growing `RpcModeTests.cs` further.
- Preserve tracked TUI gaps: word-aware wrapping, shared ANSI-aware visual-row layout, and nested Markdown style restoration. Current Pi system-theme/OSC palette updates are tracked for the theme/final-audit pass.
- Before final parity, reread the feature matrix and ledger, audit current Pi `main`, collect required behavioral/differential evidence, and confirm exact-head CI.
