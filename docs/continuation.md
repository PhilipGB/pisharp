# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix tracks scope, the detailed inventory records interfaces, and `execution-ledger.json` holds per-capability evidence. Pi Packages remain excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`. Current upstream `main` was refreshed on 2026-09-26 to `2b0a123de98318c2ff8069661721ce0c3794c34e`. Scoped inspections covered session import/CWD behavior, RPC state, queue modes, compaction and retry controls, `get_entries`/`get_tree`, themes and terminal images. These do not constitute the final full audit.

## Current implementation and validation

Latest pushed and validated PiSharp source head is `49233922512294af3dd4a34381fb0d7e1e52e78d`; exact-head Linux CI run `36276515605` passed format, a warnings-as-errors build with 0 warnings/errors, and 604 tests (0 failed, 0 skipped). Local format and build passed; the local build used `UseSharedCompilation=false` after a transient compiler output-file error. RPC model commands and `get_state` share `RpcModelProjector`, which emits Pi-shaped full model metadata from sparse/rich catalogs and filters headers/credential fields; a CLI-process test also checks nested provider/model compatibility merging.

RPC `compact` now lives in `RpcCompactionCommandHandler`, accepts Pi's `customInstructions`, cancels and settles an active run, returns summary/cut-point/token/usage/details data from canonical compaction, persists details for JSONL export, and emits manual lifecycle events. In-process and CLI-process tests cover success and abort. Exact-head CI passed. Token estimates remain approximate: they do not use Pi's provider-usage-aware projected context or include the same system prompt projection; pinned process differential is still open.

`Program.cs` remains 1,144 lines of mixed startup/application logic; `RpcMode.cs` is 517 lines. Existing retry, session, queue, state, settings and projection boundaries help, but the shared model/runtime transition still lives in the composition root and should be extracted before adding active model-switch behavior.

## Resume

Current Pi main `2b0a123de98318c2ff8069661721ce0c3794c34e` permits `get_available_models`, `set_model`, and `cycle_model` during an active run; PiSharp currently rejects them while busy. Trace Pi current-request versus next-turn model use, extract the shared model/runtime transition from `Program.cs`, and add deterministic active-tool-turn process coverage proving the next provider request uses the new model. Then continue with agent-end cancellation evidence and session interoperability. Broader gaps remain in RPC differentials, session recovery/concurrency, settings/resources/extensions, multimodal/provider/auth, coding tools and TUI behavior.
