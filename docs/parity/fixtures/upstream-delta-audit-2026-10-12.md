# Upstream Pi delta audit — 2026-10-12

Current Pi `origin/main` is `4270522473e677ddba7b093caa0064a3df51d459`. This incremental refresh extends the prior audit head `04566631c2b51fb688486bc1f040f60d1518c126` by two commits.

| Commit | Classification | Finding |
| --- | --- | --- |
| `8bb180be` — `feat(durable): code mode listing, tool search, output, models.*, grammar; tool error rule` | `OUT_OF_SCOPE` | Changes only `packages/durable` and its tests. The separate experimental durable harness does not alter the normal coding-agent CLI Codemode path. |
| `42705224` — `fix(coding-agent): code mode description says schema tools resolve with their value on failure` | `MATCHED` | PiSharp's built-in Codemode description now documents structured object results on failure. A current-Pi runtime-generated description comparison and PiSharp process/worker regression confirm the script receives the object while the nested call remains marked as an error. See [the Codemode differential](codemode-schema-error-current-pi-2026-10-12.md). |

The cumulative incremental audit covers 231 commits after baseline `955cc6665ee3986c6a033db52200779310d10dfd`: 86 `OUT_OF_SCOPE`, 44 `NO_BEHAVIOR_CHANGE`, 92 `NEEDS_WORK`, and 9 `MATCHED`. The durable-only changes remain outside the normal CLI path; the current coding-agent schema-tool failure description is matched.

All 194 required ledger capabilities remain in scope. Current-head read/Bash/indirect skill-prompt terminal differentials, official MCP conformance, other open `NEEDS_WORK` deltas, a fresh final source audit, and architecture review remain outstanding; this refresh does not satisfy the parity completion gate.
