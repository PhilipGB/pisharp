# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages remain excluded unless needed for a core capability.

**Stop condition:** a current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and no major architecture bottleneck remains.

## Current checkpoint

- Last validated and pushed source head: `654382a2c7f9483ff8cc3e16b4c847dd1ab4c2fe`; exact-head Linux CI run [36230095495](https://github.com/PhilipGB/pisharp/actions/runs/36230095495) passed format, warnings-as-errors build, and tests. Local exact-source validation passed format, build with 0 warnings/errors, and 596 tests (0 failed, 0 skipped).
- The latest RPC slice extracts `RpcStateCommandHandler`, projects the documented `get_state` state fields and a model metadata snapshot, and tests idle/named/ephemeral sessions plus in-flight compaction. Optional/unknown model metadata and current-Pi process differential remain open. Queue delivery supports independently configured `all`/`one-at-a-time` modes and persists RPC changes.
- Current Pi `main` was refreshed on 2026-09-26 to `2b0a123de98318c2ff8069661721ce0c3794c34e`; durable parity baseline is `b3487650f6378f1b0d1643dd254445ceb4a98035`. The audit remains scoped, not complete.
- Major gaps remain across RPC shapes/events and differential evidence, session recovery/concurrency, settings/resources/extensions, multimodal/provider/auth breadth, coding-tool residuals, and TUI/layout behavior. `get_state` still differs from Pi's model and remaining state fields.
- Architecture: `Program.cs` remains a mixed startup/application file above 1,000 lines; `RpcMode.cs` is about 460 lines. `PromptDeliveryQueue`, queue-mode, state-query and user-settings RPC handling now own cohesive boundaries; keep extracting around the next capability.

## Priority and exact next action

1. Implement Pi RPC `set_auto_compaction`: validate `enabled`, persist the global setting, update the active canonical run immediately, and make `get_state.autoCompactionEnabled` reflect it. Add deterministic process/state and automatic-compaction behavior coverage.
2. Validate, push, inspect exact-head Linux CI, and record the checkpoint.
3. Continue current-upstream RPC/session work, including remaining full-model projection metadata and response/event differentials, then settings, resources, extensions, multimodal/provider/auth, coding-tool and TUI gaps.
