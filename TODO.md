# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, using Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages stay excluded unless needed for a core capability.

**Stop condition:** a full current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and major architecture bottlenecks are resolved.

## Current state

- Latest pushed, exact-head CI-validated source: `1388202638e287cc9099e410eff64487cda7cacc`; Linux CI run [36286810290](https://github.com/PhilipGB/pisharp/actions/runs/36286810290) passed format, warnings-as-errors build, and 613 tests (0 failed, 0 skipped).
- The earlier `35be9dc8f8614dcdc66ceaab3136c8ad4301f406` run [36282903215](https://github.com/PhilipGB/pisharp/actions/runs/36282903215) failed 4/607 tests after format/build passed. Provider history reconciliation in `b71ed3119b3827a525515aa50d952f7b99d5a4f1` fixed the regressions; exact-head run [36286403870](https://github.com/PhilipGB/pisharp/actions/runs/36286403870) passed 612/612. `138820263` adds process-level abort lifecycle coverage and passed 613/613.
- Current Pi `main` was refreshed on 2026-09-27 and remains `2b0a123de98318c2ff8069661721ce0c3794c34e`; durable parity baseline: `b3487650f6378f1b0d1643dd254445ceb4a98035`.
- RPC model selection now uses the extracted `ModelRuntimeController`; model listing and `set_model` work during a blocked tool turn, and a process fixture verifies the next provider request uses the selected model. `cycle_model` shares the transition path; busy-cycle-specific process coverage, per-model thinking application and current-Pi process differentials remain open.
- RPC abort process coverage verifies partial assistant output, `turn_end < agent_end < agent_settled < abort response`, `willRetry: false`, and consistent snapshots. Current Pi source and tests were inspected; no Pi process differential has been run.
- Working-tree slice in progress: match Pi `--fork <path>` across project boundaries. Current Pi copies the full source entry tree into the invocation project and writes the target `cwd`; PiSharp now loads native or Pi JSONL sources, preserves every branch and the selected head, and rehomes the fork. Focused tree and cross-project fork tests pass 10/10. Full local validation passed format verification, warnings-as-errors build with 0 warnings/errors, and 616/616 tests (0 failed, 0 skipped); exact-head CI is pending.
- No overall parity claim. Major gaps remain in RPC differentials, session recovery/concurrency, settings/resources/extensions, multimodal/provider/auth breadth, coding tools and TUI behavior.
- Architecture remains concentrated: `Program.cs` is about 1,160 lines, `ConversationRun.cs` about 944 lines, and `RpcMode.cs` 517 lines. Extract cohesive responsibilities when the next capability touches them.

## Current priority

Finish cross-project `--fork` compatibility, validate and record it, then continue session/RPC interoperability and recovery against current Pi. Do not pause at the documentation or CI checkpoint.

## Exact next action

Complete full format/build/test validation for the current cross-project fork slice; commit, push and verify exact-head CI. Then compare active RPC session replacement (`switch_session`, `fork`, `clone`) cancellation, response/event order and failure behavior against current Pi `2b0a123de98318c2ff8069661721ce0c3794c34e`, adding process coverage for the highest-value remaining difference. Continue into session recovery/concurrency and later capability groups.

## Architecture and residuals to preserve

- Keep `Program.cs` a composition root; extract behavior at the boundary required by the next capability.
- `ConversationRun.cs` remains the largest concentration risk; prefer cohesive queue, retry, compaction or provider-turn boundaries over mechanical splitting.
- `PiJsonlSessionInterchange.cs` remains large; separate import/migration, mapping, export or I/O only when session interoperability requires it.
- Keep RPC capability tests in focused classes rather than growing `RpcModeTests.cs` further.
- Preserve tracked TUI gaps: word-aware wrapping, shared ANSI-aware visual-row layout, and nested Markdown style restoration. Current Pi system-theme/OSC palette updates are tracked for the theme/final-audit pass.
- Before final parity, reread the feature matrix and ledger, audit current Pi `main`, collect required behavioral/differential evidence, and confirm exact-head CI.
