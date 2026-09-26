# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix tracks scope, the detailed inventory records interfaces, and `execution-ledger.json` holds per-capability evidence. Pi Packages remain excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`. Current upstream `main` was refreshed on 2026-09-26 to `2b0a123de98318c2ff8069661721ce0c3794c34e`. Recent scoped inspections covered session import/CWD behavior, RPC retry and queue delivery modes, `get_entries`/`get_tree`, themes and terminal images. They do not constitute the final full audit. Refresh upstream before materially changed capability families and before final parity claims.

## Current implementation and validation

PiSharp uses Microsoft Agent Framework and Microsoft.Extensions.AI for provider/tool execution with a canonical C# session model. Pushed source head `654382a2c7f9483ff8cc3e16b4c847dd1ab4c2fe` passed exact-head Linux CI run `36230095495`; local validation on that exact source passed format, a warnings-as-errors build with 0 warnings/errors, and 596 tests (0 failed, 0 skipped). The latest RPC slice extracts `RpcStateCommandHandler` and covers the documented `get_state` state fields, selected model metadata, optional session fields and compaction-in-progress state. Optional/unknown model metadata and a Pi process differential remain open. Queue delivery supports independent modes and persistence. Previous work includes Pi JSONL v1-v3 import/export and cross-project runtime switching; RPC session replacement/fork/clone; Pi-shaped `get_entries` and `get_tree`; retry `entry_appended`; and `set_session_name` persistence plus event ordering.

Project runtime creation and adoption have explicit session-context types. RPC model, retry and session command families, event projection, failed-provider-history reconciliation, queue-mode handling, state-query projection and user-settings RPC control have separate boundaries. `Program.cs` remains a mixed startup/application file above 1,000 lines; `RpcMode.cs` is about 450 lines; `TerminalScreen.cs` is about 630 lines after earlier extractions. Continue decomposition where a cohesive boundary supports the next capability, alongside parity work.

## Resume

Implement Pi RPC `set_auto_compaction` with a cohesive command/settings boundary, global settings persistence, immediate active-run behavior and deterministic CLI/process coverage. Then validate, push and verify exact-head Linux CI. Full model metadata, other RPC/session differentials, recovery/concurrency, settings/resources/extensions, multimodal/provider/auth breadth, coding-tool residuals and TUI/layout behavior remain open; consult the ledger and inventory.
