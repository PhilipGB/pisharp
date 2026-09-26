# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix tracks scope, the detailed inventory records interfaces, and `execution-ledger.json` holds per-capability evidence. Pi Packages remain excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`. Current upstream `main` was checked on 2026-09-26 at `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31`. Recent scoped inspections covered session import/CWD behavior, RPC retry and queue delivery modes, `get_entries`/`get_tree`, themes and terminal images. They do not constitute the final full audit. Refresh upstream before materially changed capability families and before final parity claims.

## Current implementation and validation

PiSharp uses Microsoft Agent Framework and Microsoft.Extensions.AI for provider/tool execution with a canonical C# session model. Pushed source head `4a56c8224bf37e7471492b00ba02ea97a4a9a7a8` passed exact-head Linux CI run `36228794249`: format verification, warnings-as-errors build with 0 warnings/errors, and 595 tests (0 failed, 0 skipped). The latest slice adds independent `all`/`one-at-a-time` steering and follow-up delivery, keeps messages separate in canonical history, persists RPC mode changes and reports modes/pending count in `get_state`. This is local behavioral evidence; a pinned Pi process differential remains open. Previous work includes Pi JSONL v1-v3 import/export and cross-project runtime switching; RPC session replacement/fork/clone; Pi-shaped `get_entries` and `get_tree`; retry `entry_appended`; and `set_session_name` persistence plus event ordering.

Project runtime creation and adoption have explicit session-context types. RPC model, retry and session command families, event projection, failed-provider-history reconciliation, queue-mode handling and user-settings RPC control have separate boundaries. `Program.cs` remains a mixed startup/application file above 1,000 lines; `RpcMode.cs` is about 450 lines; `TerminalScreen.cs` is about 630 lines after earlier extractions. Continue decomposition where a cohesive boundary supports the next capability, alongside parity work.

## Resume

Inspect current Pi `get_state` implementation and tests, then extract a cohesive RPC state-query handler and match the Pi payload, including model and lifecycle/session fields. Add deterministic CLI RPC process tests, validate, push and verify exact-head Linux CI. RPC and session differential evidence, recovery/concurrency, settings/resources/extensions, multimodal/provider/auth breadth, coding-tool residuals and TUI/layout behavior remain open; consult the ledger and inventory.
