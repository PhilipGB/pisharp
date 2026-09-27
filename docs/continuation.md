# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix tracks scope, the detailed inventory records interfaces, and `execution-ledger.json` holds per-capability evidence. Pi Packages remain excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`. Current upstream `main` was verified on 2026-09-27 at `2b0a123de98318c2ff8069661721ce0c3794c34e`. Scoped source/test inspections cover RPC state/model switching, aborted-turn event order, and session fork behavior; this is not the final full audit.

## Latest exact-head evidence

Latest pushed, exact-head CI-validated source head is `e3ab887613146c2a535be9ac5949308215616502`; documentation-only run [36289412915](https://github.com/PhilipGB/pisharp/actions/runs/36289412915) passed format, a warnings-as-errors build with 0 warnings/errors, and 617 tests (0 failed, 0 skipped). The preceding code head `f509750da57bece3e64a3bcbb905e3852782afa4` passed the same gates and all 617 tests in run `36289250296`. The cross-project fork checkpoint `9a0fbe66` passed run `36288246889` with 616 tests. The earlier provider-history fix `b71ed3119b3827a525515aa50d952f7b99d5a4f1` passed run `36286403870` with 612 tests. Its parent `35be9dc8` failed 4/607 tests; those provider history and compaction regressions were fixed before the green checkpoints.

`ModelRuntimeController` now owns shared RPC model/runtime transitions. `get_available_models` and `set_model` are accepted during a blocked tool turn; process coverage verifies that the next provider request uses the newly selected model. A new real CLI RPC process fixture verifies partial-turn abort projection and ordering through `agent_settled`; current Pi `agent-loop.ts`, `agent-session.ts`, and abort tests were inspected at the pinned head. No current-Pi process differential has been run.

## Active session slice

Current Pi `SessionManager.forkFrom` copies a source session into the invocation project, updates the new session header `cwd` to that target, and records the source path as parent. PiSharp now loads native or Pi JSONL sources, preserves the full tree and selected head, and writes the fork into the invocation project. The CLI process test covers both formats, target CWD, parent path, transcript, inactive branch and source immutability. Focused `ConversationTreeTests` and `PiSessionStartupTargetTests` passed 10/10; exact-head CI run `36288246889` passed 616/616.

## Active RPC session replacement slice

At current Pi `2b0a123de98318c2ff8069661721ce0c3794c34e`, `AgentSessionRuntime.switchSession` constructs `SessionManager.open` and checks the stored CWD before `teardownCurrent`; malformed nonempty session files and missing project directories therefore fail before active work is aborted. PiSharp now prevalidates an existing switch target through `ProjectSessionRuntimeFactory` before `RpcMode` cancels and settles the current run. A Linux CLI process test blocks a provider response, submits malformed Pi JSONL, observes its correlated error while the prompt remains active, then releases the provider and verifies normal completion. Current Pi source and runtime tests were inspected; no Pi process differential has been run. Pi's behavior for a missing target path differs from PiSharp's current file-not-found response and remains open.

Full local and exact-head CI validation for `f509750da57bece3e64a3bcbb905e3852782afa4` passed format verification, a warnings-as-errors build with 0 warnings/errors, and 617/617 tests (0 failed, 0 skipped).

## Active RPC fork slice

Current Pi includes regression test `packages/coding-agent/test/suite/regressions/8724-in-memory-fork-active-tool.test.ts`: forking during an active tool turn settles the outgoing turn and does not append its aborted messages to the replacement session. PiSharp's new Linux RPC process test covers a blocked provider stream: it verifies `turn_end < agent_end < agent_settled < fork response`, retains the partial aborted assistant message on the outgoing event, and confirms the new branch is empty. Focused session-replacement process tests pass 2/2; full local format verification, warnings-as-errors build with 0 warnings/errors, and 618/618 tests pass. Exact-head CI is pending. The current fixture does not yet cover a blocked tool or run Pi as a process.

## Resume

Commit and push the active-fork process fixture, then verify exact-head CI. Compare Pi's missing-target behavior and active `clone` cancellation/failure ordering; implement and add process evidence for the next material gap. Then continue session crash/concurrency recovery, settings/resources/extensions, multimodal/provider/auth breadth, coding-tool residuals, and the final current-Pi audit.

Do not infer full parity from any green slice. Keep architecture work tied to the next feature boundary: `Program.cs` remains over 1,100 lines, `ConversationRun.cs` around 944 lines, and `PiJsonlSessionInterchange.cs` around 740 lines. Keep `RpcMode.cs` moving toward transport/dispatch, and put new RPC capability tests in focused classes. Preserve TUI residuals for word-aware wrapping, shared ANSI-aware visual-row layout, nested Markdown style restoration, and the current Pi system-theme/OSC palette delta.
