# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix tracks scope, the detailed inventory records interfaces, and `execution-ledger.json` holds per-capability evidence. Pi Packages remain excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`. Current upstream `main` was verified on 2026-09-27 at `2b0a123de98318c2ff8069661721ce0c3794c34e`. Scoped source/test inspections cover RPC state/model switching, aborted-turn event order, and session fork behavior; this is not the final full audit.

## Latest exact-head evidence

Latest pushed, exact-head CI-validated source head is `e955daffa03e768115af29ae1c90fa7c06070373`; Linux CI run [36292314859](https://github.com/PhilipGB/pisharp/actions/runs/36292314859) passed format, a warnings-as-errors build with 0 warnings/errors, and 623 tests (0 failed, 0 skipped). It includes the active-clone snapshot and missing-source preflight. The previous active-fork process fixture's parent `d8ec4c8d6dc33e5e0a00bdb4b29f3fcc3703fcdd` failed 1/618 because it queried session data immediately after `agent_settled`, before the RPC task had returned; it now polls `get_state` until idle. Earlier exact-head checkpoints remain: `e3ab8876` passed 617 tests in run `36289412915`; `f509750d` passed 617 in run `36289250296`; cross-project fork `9a0fbe66` passed 616 in run `36288246889`; provider-history fix `b71ed311` passed 612 in run `36286403870`. Its parent `35be9dc8` failed 4/607 tests, fixed before those green checkpoints.

`ModelRuntimeController` now owns shared RPC model/runtime transitions. `get_available_models` and `set_model` are accepted during a blocked tool turn; process coverage verifies that the next provider request uses the newly selected model. A new real CLI RPC process fixture verifies partial-turn abort projection and ordering through `agent_settled`; current Pi `agent-loop.ts`, `agent-session.ts`, and abort tests were inspected at the pinned head. No current-Pi process differential has been run.

## Active session slice

Current Pi `SessionManager.forkFrom` copies a source session into the invocation project, updates the new session header `cwd` to that target, and records the source path as parent. PiSharp now loads native or Pi JSONL sources, preserves the full tree and selected head, and writes the fork into the invocation project. The CLI process test covers both formats, target CWD, parent path, transcript, inactive branch and source immutability. Focused `ConversationTreeTests` and `PiSessionStartupTargetTests` passed 10/10; exact-head CI run `36288246889` passed 616/616.

## Active RPC session replacement slice

At current Pi `2b0a123de98318c2ff8069661721ce0c3794c34e`, `AgentSessionRuntime.switchSession` constructs `SessionManager.open` and checks the stored CWD before `teardownCurrent`; malformed nonempty session files and missing project directories therefore fail before active work is aborted. PiSharp prevalidates existing targets before cancellation. Missing `.jsonl` targets now open as lazy Pi JSONL sessions in the invocation project. A Linux CLI process test verifies the switch response follows blocked-provider abort settlement, the file remains absent until the replacement receives a prompt, and that subsequent prompt is executed by and persisted through the new run. RPC prompt execution and explicit saves now use the currently adopted `ConversationRun`. A fenced `PiJsonlSessionFileStore` shares the atomic writer with native storage and refuses overwriting an existing or externally changed file. `RpcSessionReplacementProcessTests.InvalidSwitchTargetDoesNotCancelAnActiveRun` covers failure preflight. Exact-head run `36290986668` passed format, warnings-as-errors build, and 621/621 tests. No Pi process differential has been run.

Full local and exact-head CI validation for `f509750da57bece3e64a3bcbb905e3852782afa4` passed format verification, a warnings-as-errors build with 0 warnings/errors, and 617/617 tests (0 failed, 0 skipped).

## Active RPC fork slice

Current Pi includes regression test `packages/coding-agent/test/suite/regressions/8724-in-memory-fork-active-tool.test.ts`: forking during an active tool turn settles the outgoing turn and does not append its aborted messages to the replacement session. PiSharp's Linux RPC process test covers blocked provider streaming: it verifies `turn_end < agent_end < agent_settled < fork response`, retains partial aborted assistant output on the outgoing event, and confirms the new branch is empty. The first exact-head CI run `36289925328` failed 1/618 because the test raced task completion after `agent_settled`; commit `bfb37df4d9d75a7462df5c8884ee172467b3e5d2` adds an idle-state poll and CI passed 621/621. The active-tool fork slice adds `RpcActiveToolForkProcessTests`: a Linux CLI RPC process blocks in Bash, forks, confirms the aborted assistant in `agent_end`, waits for `agent_settled` before the response, and checks the replacement transcript is empty. The focused process test passes 1/1 locally; exact-head CI for this slice is pending. No Pi process differential has been run.

## Active RPC clone slice

Current Pi RPC `clone` passes the current leaf to `AgentSessionRuntime.fork(position: "at")`; that runtime constructs the branch before `teardownCurrent` aborts the active run. PiSharp now snapshots the current branch, removes the unfinished run's internal recovery checkpoints, validates the source session version and saves the replacement branch before cancellation. After settlement it opens the staged session runtime. `RpcActiveCloneProcessTests` verifies an active blocked-provider clone contains the accepted prompt but not the aborted assistant/recovery record and checks `turn_end < agent_end < agent_settled < clone response`. `RpcClonePreflightProcessTests` removes the source file during a blocked prompt and verifies Pi's clone error arrives while the run stays active, with no extra session created; restoring the original bytes lets the turn complete normally. Focused tests pass 2/2; local format verification and warnings-as-errors build pass, and the latest full run passes 623/623. An earlier full invocation had one transient failure in `CompactionTests.MultiCycleParallelToolGraphSplitsOnlyAfterCompletedBatches`; focused and subsequent full reruns passed. Exact-head CI is pending. No Pi process differential has been run.

## Resume

Current next task: finish and exact-head-validate the active-tool fork snapshot/process slice, then continue session crash/concurrency recovery, settings/resources/extensions, multimodal/provider/auth breadth, coding-tool residuals and the final current-Pi audit.

Do not infer full parity from any green slice. Keep architecture work tied to the next feature boundary: `Program.cs` remains over 1,100 lines, `ConversationRun.cs` around 944 lines, and `PiJsonlSessionInterchange.cs` around 740 lines. Keep `RpcMode.cs` moving toward transport/dispatch, and put new RPC capability tests in focused classes. Preserve TUI residuals for word-aware wrapping, shared ANSI-aware visual-row layout, nested Markdown style restoration, and the current Pi system-theme/OSC palette delta.
