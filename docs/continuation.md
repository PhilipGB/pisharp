# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix tracks scope, the detailed inventory records interfaces, and `execution-ledger.json` holds per-capability evidence. Pi Packages remain excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`. Current upstream `main` was verified on 2026-09-27 at `2b0a123de98318c2ff8069661721ce0c3794c34e`. Scoped source/test inspections cover RPC state/model switching, aborted-turn event order, and session fork behavior; this is not the final full audit.

## Latest exact-head evidence

Latest pushed, exact-head CI-validated PiSharp source is `1388202638e287cc9099e410eff64487cda7cacc`; run [36286810290](https://github.com/PhilipGB/pisharp/actions/runs/36286810290) passed format, a warnings-as-errors build with 0 warnings/errors, and 613 tests (0 failed, 0 skipped). The preceding provider-history fix `b71ed3119b3827a525515aa50d952f7b99d5a4f1` passed run `36286403870` with 612 tests. Its parent `35be9dc8` failed 4/607 tests; those provider history and compaction regressions were fixed before the green checkpoints.

`ModelRuntimeController` now owns shared RPC model/runtime transitions. `get_available_models` and `set_model` are accepted during a blocked tool turn; process coverage verifies that the next provider request uses the newly selected model. A new real CLI RPC process fixture verifies partial-turn abort projection and ordering through `agent_settled`; current Pi `agent-loop.ts`, `agent-session.ts`, and abort tests were inspected at the pinned head. No current-Pi process differential has been run.

## Active session slice

Current Pi `SessionManager.forkFrom` copies a source session into the invocation project, updates the new session header `cwd` to that target, and records the source path as parent. PiSharp had instead adopted the source session's project CWD and used its bound store to read the source. The working-tree change introduces `SessionForkFactory`, `ConversationSession.ForkInto`, and invocation-project targeting for `--fork`. Native session and Pi JSONL sources are covered by a real CLI RPC process test. Focused `ConversationTreeTests` and `PiSessionStartupTargetTests` passed 10/10. Full local validation passed format verification, warnings-as-errors build with 0 warnings/errors, and 616/616 tests (0 failed, 0 skipped); exact-head CI is pending.

## Resume

Finish full validation for the active fork slice, commit and push it, then verify exact-head CI before recording that source head as green. Continue by comparing RPC `switch_session`, `fork`, and `clone` replacement behavior under active work: cancellation/settlement, response ordering, malformed input and failures. Add process coverage where the comparison identifies a material gap. Then continue session crash/concurrency recovery, settings/resources/extensions, multimodal/provider/auth breadth, coding-tool residuals, and the final current-Pi audit.

Do not infer full parity from any green slice. Keep architecture work tied to the next feature boundary: `Program.cs` remains over 1,100 lines, `ConversationRun.cs` around 944 lines, and `PiJsonlSessionInterchange.cs` around 740 lines. Keep `RpcMode.cs` moving toward transport/dispatch, and put new RPC capability tests in focused classes. Preserve TUI residuals for word-aware wrapping, shared ANSI-aware visual-row layout, nested Markdown style restoration, and the current Pi system-theme/OSC palette delta.
