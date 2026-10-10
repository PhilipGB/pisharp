# Incremental current-Pi source audit — 2026-10-10

Pi `origin/main` is `ea448f454af474f3e2fe9733b331e17b0662f5d6`. The prior audit ended at `42a3497d03ad17e308a2299fa824727894f2c0ec`; this refresh classifies all eight commits after that pin. The aggregate audit now covers 206 commits from `955cc6665ee3986c6a033db52200779310d10dfd`: 74 `OUT_OF_SCOPE`, 37 `NO_BEHAVIOR_CHANGE`, 87 `NEEDS_WORK`, and 8 `MATCHED`.

## Current-head delta

| Pi commit | Classification | Changed behavior and evidence | PiSharp ledger mapping |
|---|---|---|---|
| `ea448f454af474f3e2fe9733b331e17b0662f5d6` `fix(durable): bound the scheduler's ownership indexes by live work` | `OUT_OF_SCOPE` | Bounds durable scheduler indexes by live work under packages/durable only. The alternate durable runtime is outside the normal coding-agent CLI product graph. | None; separate durable package. |
| `e37cdc420669e48b4df4bbce0fb2617fc3c71328` `fix(durable): drop redundant nested-call parent check; test ownedTasks() with a held child` | `OUT_OF_SCOPE` | Changes nested-call scheduler bookkeeping and tests under packages/durable only. | None; separate durable package. |
| `b9f2b3e827daf6677aa835921a7b9c060454889e` `docs(durable): read only through tx inside a commit callback` | `OUT_OF_SCOPE` | Changes the durable package specification and types only. | None; separate durable package. |
| `4130bd5b32dac1d2d84697af1c4b587e133d488c` `fix(codemode): ignore Node --watch notifications from the worker` | `NO_BEHAVIOR_CHANGE` | Filters Node --watch dependency notifications from worker_threads message events. PiSharp Codemode launches a standalone node worker process and uses newline-delimited stdio, so that internal Worker message is not part of its runtime protocol. | codemode-runtime; no behavior change on the PiSharp CLI process boundary. |
| `4ac0bd8c7b96d72cb6a73226edc7c9ecaae1d14e` `feat(durable): one document per nested call; TaskRuntime.ownedTasks()` | `OUT_OF_SCOPE` | Adds nested-call durable documents and task ownership APIs under packages/durable only. | None; separate durable package. |
| `cb437549318e06de1c3b00786f84236a0a4c896e` `fix(durable): scheduler no longer visits every live task on each pass` | `OUT_OF_SCOPE` | Changes durable scheduler ownership indexes and benchmarks under packages/durable only. | None; separate durable package. |
| `c5f5b3282d5e4203c085e59837ba17aeaf2829b5` `fix(mcp): reject iss when no authorization server metadata was discovered` | `MATCHED` | Pi's `packages/mcp/test/oauth.test.ts` passes 12/12 at `ea448f4`, including rejection of a present `iss` when metadata is absent before token exchange. PiSharp uses ModelContextProtocol.Core 2.2.0 with issuer-response validation enabled for its callback handler: a non-empty `iss` with no metadata issuer is rejected, and an `iss` mismatch is rejected even when RFC 9207 support is false. The real HTTP login regression in `McpOAuthMetadataTests` now covers that unadvertised-mismatch path and asserts zero token requests. | `mcp-runtime`; exact SDK callback validation plus local OAuth exchange counter. |
| `748b351fe2e7be5be32bd8ffa56b7b23654e6f73` `feat(codemode): run sandboxes on Cloudflare Workers via a remote Durable Object` | `OUT_OF_SCOPE` | Adds a Cloudflare Workers/remote Durable Object deployment adapter and examples for the separate Codemode package. The normal PiSharp CLI Codemode runtime is local and exposes no Cloudflare host/deployment surface. | None for the current CLI surface; retain local Codemode ledger scope. |

The PiSharp llama.cpp family was rerun against this oracle: current Pi llama extension tests pass 15/15; all three process comparisons match; twelve paired PTY flows match all 155 synchronized render frames and HTTP traces; and all twelve same-Pi calibration runs match. Exact evidence is in [the current llama differential](llama-terminal-current-pi-2026-10-10/current-ea448f45-dab73a74/README.md).

## Audit state

The `c5f5b328` MCP OAuth issuer check is matched by the pinned SDK callback validator and its local HTTP exchange-count regression. Resources / instructions and trust remains the active implementation family. The full source audit, remaining ledger capabilities, official MCP conformance and final audit remain open.
