# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages remain excluded unless needed for a core capability.

**Stop condition:** a current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and no major architecture bottleneck remains.

## Current checkpoint

- Last validated source head: `317a634dc5ff610b5ef5eb11a6b41d2413483a55`; exact-head Linux CI run [36226804227](https://github.com/PhilipGB/pisharp/actions/runs/36226804227) passed format, warnings-as-errors build (0 warnings/errors), and 590 tests (0 failed, 0 skipped).
- The latest RPC/session slice adds Pi-shaped `entry_appended` for retry context edits, retains completed provider/tool turns when MAF loses them after a later failure, and tests real CLI event order. Local format, warnings-as-errors build, and all 590 tests passed before CI.
- Current Pi `main` remains `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31`; durable baseline is `b3487650f6378f1b0d1643dd254445ceb4a98035`. Reviews remain scoped, not a full audit.
- Major gaps remain across RPC response/event differentials, session recovery/concurrency, settings/resources/extensions, multimodal and provider/auth breadth, coding-tool residuals, and TUI/layout behavior. Retry context edits are the only `entry_appended` path covered so far.
- Architecture: `Program.cs` remains a mixed startup/application file above 1,000 lines; `RpcMode.cs` is about 450 lines. The new `ProviderTurnHistoryReconciler` isolates failed-attempt history repair; continue extracting only cohesive boundaries needed by the next capability.

## Priority and exact next action

1. Commit and push the current validated slice, then inspect exact-head Linux CI.
2. Implement Pi-compatible `set_steering_mode` and `set_follow_up_mode`: `all` and `one-at-a-time` delivery semantics, shared session state, validation, and active/queued RPC process tests.
3. Continue session/RPC interoperability and the remaining settings, resources, extensions, multimodal/provider/auth, coding-tool and TUI gaps. Refresh upstream before each material family and before final claims.
