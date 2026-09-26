# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, using Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages remain excluded unless a core capability needs them.

**Stop condition:** current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and no major architecture bottleneck remains.

## Current checkpoint

- Source head `6f9b0f910b655d45e57a8f887730107498d2b068` passed exact-head Linux CI run [36219218706](https://github.com/PhilipGB/pisharp/actions/runs/36219218706): format, warnings-as-errors build (0 warnings/errors), and 580 tests (0 failed, 0 skipped).
- Current Pi `main` is `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31`; durable baseline is `b3487650f6378f1b0d1643dd254445ceb4a98035`. The current-main review remains scoped, not a full audit.
- This worktree, based on `6f9b0f910b655d45e57a8f887730107498d2b068`, adds Pi-shaped RPC agent/turn/message/tool events and consistent message snapshots. Consumed steering prompts now get user message boundaries inside the next turn, and failures after a tool continuation do not repeat prior-turn tool results. Local validation passed format, warnings-as-errors build (0 warnings/errors), and 586 tests (0 failed, 0 skipped); this slice is not yet committed or covered by exact-head CI. Implemented larger areas include Pi JSONL v1-v3 import/export, cross-project runtime switching, RPC session commands, Pi-shaped entry/tree results, retries and session-name persistence.
- Major gaps remain in `entry_appended` and other RPC response/event differentials, session recovery/concurrency, settings/resources/extensions, multimodal and provider/auth breadth, coding-tool residuals, and TUI/layout behavior.
- Architecture: `Program.cs` remains a mixed startup/application file above 1,000 lines; `RpcMode.cs` is about 450 lines; `RpcAssistantMessageProjector` now owns assistant message lifecycle and wire projection. Continue cohesive extraction alongside parity work.

## Priority and exact next action

1. Commit and push the current RPC lifecycle/message/tool slice, then verify exact-head Linux CI.
2. Next implementation action: add Pi-shaped `entry_appended` events at the canonical session append boundary and verify event-before-response ordering through the actual RPC process.
3. Continue RPC response/event differentials and session recovery/interchange, then settings, resources, extensions, multimodal/provider/auth, coding-tool and TUI parity. Refresh upstream before each material family and before final claims.
