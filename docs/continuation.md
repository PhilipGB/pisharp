# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix tracks scope, the detailed inventory records interfaces, and `execution-ledger.json` holds per-capability evidence. Pi Packages remain excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`. Current upstream `main` was checked on 2026-09-26 at `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31`. Recent scoped inspections covered session import/CWD behavior, RPC retry events, `get_entries`/`get_tree`, themes and terminal images. They do not constitute the final full audit. Refresh upstream before materially changed capability families and before final parity claims.

## Current implementation and validation

PiSharp uses Microsoft Agent Framework and Microsoft.Extensions.AI for provider/tool execution with a canonical C# session model. Exact source head `2f0250ee91a500cdb6b08dce2e940b849089ba26` passed Linux CI run `36217090057`: format verification, warnings-as-errors build with 0 warnings/errors, and 574 tests (0 failed, 0 skipped). This head includes imported Pi JSONL v1-v3 sessions and cross-project runtime switching, RPC session replacement/fork/clone commands, per-low-level-turn RPC `agent_start`/`agent_end`, a distinct bounded session retry lifecycle, and Pi-shaped `session_info_changed` before the `set_session_name` response. The test suite has unit, subprocess, and Linux PTY coverage for these slices; broad Pi differential parity remains unverified.

Project runtime creation and adoption have explicit session-context types, while RPC model, retry and session command families and event projection have separate boundaries. `Program.cs` remains a mixed startup/application file at 1,132 lines; `RpcMode.cs` is 451 lines; `TerminalScreen.cs` is 631 lines after earlier extractions. Continue decomposition when it creates a useful capability boundary. The latest cross-project work should make follow-up session and RPC changes simpler without becoming an architecture-only phase.

## Resume

Implement Pi-compatible `get_entries` and `get_tree` payloads next. At current Pi `main`, entries omit the header and use the upstream entry union, while the tree uses nested `{ entry, children, label?, labelTimestamp? }` nodes and returns `{ tree, leafId }`. PiSharp currently returns canonical session nodes with a `format: "pisharp"` marker. First characterize current Pi's RPC fixtures, then share a session-entry projection with the existing Pi JSONL exporter and test cursor, ordering, labels and process output. Remaining RPC event families, session recovery/interchange differentials, settings/resources/extensions, multimodal/provider/auth breadth, coding tools and TUI residuals remain open. See the ledger and inventory for exact evidence and gaps.
