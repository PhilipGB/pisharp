# Upstream Pi delta audit — 2026-10-11

Pi `main` was refreshed from `cdaf6c227d4718a72b59ccd02152c9614df44cfc` to `96abe9a5ad45005cae34ed1c52d42f71fc88bedc`. Both intervening commits were inspected by changed path and source diff.

| Commit | Classification | Changed behavior | Reason |
| --- | --- | --- | --- |
| `e6b9a6af` — `feat(durable): inputs placed as application entries, display-only writes appended at once` | `OUT_OF_SCOPE` | Adds typed input entries and immediate display-only writes to the separate durable harness; updates experimental durable/vacation hosts. | The diff does not change normal coding-agent CLI, provider, classifier, resource, trust, session, RPC or TUI behavior. |
| `96abe9a5` — `feat(coding-agent): use pi.dev configuration schemas` | `NO_BEHAVIOR_CHANGE` | Changes published `$id`/`$schema` URLs and their documentation; generated schema constraints and runtime config validation are unchanged. | This is schema-host metadata and documentation only; it does not change normal CLI behavior or the resource/trust contracts under audit. |

The incremental audit now covers 210 commits after baseline `955cc6665ee3986c6a033db52200779310d10dfd`: 77 `OUT_OF_SCOPE`, 38 `NO_BEHAVIOR_CHANGE`, 87 `NEEDS_WORK`, and 8 `MATCHED`. The latest Pi repository head is `96abe9a5`; `cdaf6c22` remains the latest applicable coding-agent behavior source for the active skill/resource comparisons because the intervening changes do not alter those contracts. The current llama oracle includes the latest Pi `main`; its relevant source and tests are unchanged by these two commits. Current Pi's llama extension suite passes 15/15. Process and terminal evidence remains indexed in [the 2026-10-11 llama evidence](llama-terminal-current-pi-2026-10-11.md).
