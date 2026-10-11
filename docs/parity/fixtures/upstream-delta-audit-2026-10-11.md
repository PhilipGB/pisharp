# Upstream Pi delta audit — 2026-10-11

Pi `main` was fetched from `ea448f454af474f3e2fe9733b331e17b0662f5d6` to current `cdaf6c227d4718a72b59ccd02152c9614df44cfc`. The two intervening commits were inspected by changed path and source diff. Both are outside the normal coding-agent product surface.

| Commit | Classification | Changed behavior | Reason |
| --- | --- | --- | --- |
| `f44707e7` — `feat(durable): bound the Session's document cache by use (documentRetentionMs)` | `OUT_OF_SCOPE` | Adds use-based retention to the separate `packages/durable` session document cache, plus its tests, benchmark and documentation. | No coding-agent CLI, provider, classifier, resource, session, RPC or TUI implementation path changed. |
| `cdaf6c22` — `feat(durable): prepare images in a worker thread in /images/node` | `OUT_OF_SCOPE` | Moves the separate durable package's Node image preparation to a worker thread and updates its tests and documentation. | The diff is confined to `packages/durable`; no normal coding-agent product behavior changed. |

The audit now covers 208 commits after baseline `955cc6665ee3986c6a033db52200779310d10dfd`: 76 `OUT_OF_SCOPE`, 37 `NO_BEHAVIOR_CHANGE`, 87 `NEEDS_WORK`, and 8 `MATCHED`. The current llama oracle includes the latest Pi `main`; its coding-agent source and tests are unchanged by these two commits. Current Pi's llama extension suite passes 15/15. Current process and terminal comparisons are pinned in [the 2026-10-11 llama evidence](llama-terminal-current-pi-2026-10-11.md).
