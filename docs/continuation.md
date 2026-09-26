# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix tracks scope, the detailed inventory records interfaces, and `execution-ledger.json` holds per-capability evidence. Pi Packages remain excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`. Current upstream `main` was refreshed on 2026-09-26 to `2b0a123de98318c2ff8069661721ce0c3794c34e`. Scoped inspections covered session import/CWD behavior, RPC state, queue modes, compaction and retry controls, `get_entries`/`get_tree`, themes and terminal images. These do not constitute the final full audit.

## Current implementation and validation

Latest pushed and validated PiSharp source head is `04d66e2793c7fff1ef9d27d9463de53e9a7c5024`; exact-head Linux CI run `36268510576` passed format, a warnings-as-errors build, and 601 tests (0 failed, 0 skipped). RPC `set_auto_compaction` persists the global setting and updates the active canonical run. RPC retry controls have deterministic lifecycle and CLI-process coverage.

RPC `compact` now lives in `RpcCompactionCommandHandler`, accepts Pi's `customInstructions`, cancels and settles an active run, returns summary/cut-point/token/usage/details data from canonical compaction, persists details for JSONL export, and emits manual lifecycle events. In-process and CLI-process tests cover success and abort. Exact-head CI passed. Token estimates remain approximate: they do not use Pi's provider-usage-aware projected context or include the same system prompt projection; pinned process differential is still open.

`Program.cs` remains a mixed startup/application file above 1,000 lines; `RpcMode.cs` is about 500 lines. Existing model, retry, session, queue, state and settings controllers form boundaries; continue decomposition where the next parity capability benefits.

## Resume

Project the complete available Pi model metadata consistently across `get_available_models`, `set_model`, `cycle_model`, and `get_state`. Inspect current Pi request/response types and provider model data first; add rich and sparse actual-process tests, preserve provider neutrality and avoid exposing credentials. Broader gaps remain in RPC differentials, session recovery/concurrency, settings/resources/extensions, multimodal/provider/auth, coding tools and TUI behavior.
