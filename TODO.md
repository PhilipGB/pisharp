# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages remain excluded unless needed for a core capability.

**Stop condition:** a current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and no major architecture bottleneck remains.

## Current checkpoint

- Last validated and pushed source head: `4a56c8224bf37e7471492b00ba02ea97a4a9a7a8`; exact-head Linux CI run [36228794249](https://github.com/PhilipGB/pisharp/actions/runs/36228794249) passed format, warnings-as-errors build (0 warnings/errors), and 595 tests (0 failed, 0 skipped).
- The latest slice adds independent `all`/`one-at-a-time` steering and follow-up delivery, keeps each queued message distinct in canonical history, persists RPC mode changes, and reports modes and pending count in `get_state`. This has local process/lifecycle evidence, not a current-Pi differential.
- Current Pi `main` is pinned at `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31`; durable parity baseline is `b3487650f6378f1b0d1643dd254445ceb4a98035`. The audit remains scoped, not complete.
- Major gaps remain across RPC shapes/events and differential evidence, session recovery/concurrency, settings/resources/extensions, multimodal/provider/auth breadth, coding-tool residuals, and TUI/layout behavior. `get_state` still differs from Pi's model and remaining state fields.
- Architecture: `Program.cs` remains a mixed startup/application file above 1,000 lines; `RpcMode.cs` is about 450 lines. `PromptDeliveryQueue`, queue-mode RPC handling and user-settings RPC control now own cohesive boundaries; keep extracting around the next capability.

## Priority and exact next action

1. Inspect the pinned Pi `get_state` implementation and tests; extract a cohesive RPC state-query handler from session command handling and implement the current Pi payload, including model and lifecycle/session fields.
2. Add deterministic CLI RPC process tests for the returned shape and state transitions; validate format, warnings-as-errors build and the full suite, then push and verify exact-head Linux CI.
3. Continue current-upstream RPC/session work, then settings, resources, extensions, multimodal/provider/auth, coding-tool and TUI gaps. Keep the ledger and this handoff exact as work advances.
