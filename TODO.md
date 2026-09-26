# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages remain excluded unless required for a core capability.

**Stop condition:** a current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and no major architecture bottleneck remains.

## Current state

- Latest pushed, CI-validated source head: `04d66e2793c7fff1ef9d27d9463de53e9a7c5024`; exact-head Linux CI run [36268510576](https://github.com/PhilipGB/pisharp/actions/runs/36268510576) passed format, warnings-as-errors build, and 601 tests (0 failed, 0 skipped).
- Current Pi `main` was refreshed on 2026-09-26 to `2b0a123de98318c2ff8069661721ce0c3794c34e`; durable parity baseline remains `b3487650f6378f1b0d1643dd254445ceb4a98035`.
- RPC `compact` now accepts Pi's `customInstructions`, aborts and settles an active run, returns Pi-shaped result/usage/details, persists compaction metadata for JSONL, and emits manual lifecycle events. Process tests cover success and abort; local source validation and exact-head CI passed.
- Material gaps remain across current-Pi RPC/event differentials and model metadata, session recovery/concurrency, settings/resources/extensions, multimodal/provider/auth breadth, coding tools, and TUI behavior. No overall parity claim is made.
- `Program.cs` remains a mixed startup/application file above 1,000 lines; `RpcMode.cs` is about 500 lines. Continue extracting cohesive boundaries alongside parity work.

## Exact next action

Implement one explicit model projector for `get_available_models`, `set_model`, `cycle_model`, and `get_state`. Compare current Pi model fields and response shapes, preserve available metadata without exposing credentials, add sparse/rich process tests, then continue through the remaining RPC/session gaps.
