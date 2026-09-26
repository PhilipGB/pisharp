# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix tracks scope, the detailed inventory records interfaces, and `execution-ledger.json` holds per-capability evidence. Pi Packages remain excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`. Current upstream `main` was checked on 2026-09-26 at `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31`. Recent scoped inspections covered session import/CWD behavior, RPC retry events, `get_entries`/`get_tree`, themes and terminal images. They do not constitute the final full audit. Refresh upstream before materially changed capability families and before final parity claims.

## Current implementation and validation

PiSharp uses Microsoft Agent Framework and Microsoft.Extensions.AI for provider/tool execution with a canonical C# session model. Last known-good exact source head `b9ac897ffced87d84557b684075960eb0ecafe6e` passed Linux CI run `36224724170`: format verification, warnings-as-errors build with 0 warnings/errors, and 586 tests (0 failed, 0 skipped). The current uncommitted worktree adds Pi-shaped `entry_appended` for retry context edits and reconciles completed provider/tool turns when MAF loses them after a later failure. Local validation passed format verification, warnings-as-errors build with 0 warnings/errors, and 590 tests (0 failed, 0 skipped); exact-head CI is pending. Implemented subsets also include Pi JSONL v1-v3 import/export and cross-project runtime switching; RPC session replacement/fork/clone; Pi-shaped `get_entries` and `get_tree`; and `set_session_name` persisting a Pi `session_info` entry before emitting `session_info_changed` and its response. Broad Pi differential parity remains unverified.

Project runtime creation and adoption have explicit session-context types. RPC model, retry and session command families, event projection, and the new failed-provider-history reconciler have separate boundaries. `Program.cs` remains a mixed startup/application file at 1,136 lines; `RpcMode.cs` is 451 lines; `TerminalScreen.cs` is 631 lines after earlier extractions. Continue decomposition when it creates a useful capability boundary, alongside parity work.

## Resume

After committing/pushing this slice and checking exact-head CI, implement Pi-compatible `set_steering_mode` and `set_follow_up_mode` with `all` and `one-at-a-time` delivery behavior over the canonical run queues. The RPC event-family ledger remains in progress: `entry_appended` is currently covered for retry context edits, while extension boundary appends, compaction events, process-level error/cancellation differentials, broader response shapes and current-Pi differential fixtures remain open. Microsoft.Extensions.AI 10.10.0 aggregates OpenAI function-call fragments into complete call content, so tool argument deltas currently arrive as one complete JSON fragment. Session recovery/interchange, settings/resources/extensions, multimodal/provider/auth breadth, coding tools and TUI residuals remain open; see the ledger and inventory.
