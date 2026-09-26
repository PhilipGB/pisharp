# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless needed for a core capability.

**Stop condition:** a full audit against current Pi `main` finds no material in-scope gaps and required validation/differential evidence passes.

## Current checkpoint

- Source head `38b402e53bf4e0e543e2f2493ecf67ad2765f898` passed Linux CI run [36205642095](https://github.com/PhilipGB/pisharp/actions/runs/36205642095): format verification, warnings-as-errors build (0 warnings/errors), and 541 tests (0 failed, 0 skipped). The previous source head `7ae23732cb176d76709cc9fb521b2f12170b5450` also passed run [36205110022](https://github.com/PhilipGB/pisharp/actions/runs/36205110022).
- Current Pi `main` was rechecked 2026-09-26 at `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31`; durable parity baseline remains `b3487650f6378f1b0d1643dd254445ceb4a98035`. This is a scoped refresh, not a full audit.
- RPC projects `agent_start`, `agent_end` with run-local canonical Pi messages and `willRetry: false`, and payload-free `agent_settled`. Retry-attempt semantics and the other event families remain open. Model/thinking RPC commands now have a separate handler.
- Pi JSONL import checks and path selection are in `PiSessionImportService`; imports still require the recorded CWD to match. `Program.cs` remains 1,051 lines, and the listed TUI layout/styling residuals remain open.

## What prevents parity

- RPC/JSON/SDK still have incomplete retry, turn/message/tool/session event shapes, active-run thinking changes, session controls, and process compatibility.
- Sessions still lack cross-project CWD switching, complete crash recovery and concurrent-access semantics, broader manager parity, and current-Pi import/export differentials.
- Settings, resources, extensions, multimodal behavior, provider/auth breadth, coding-tool residuals, TUI residuals, and the final current-main audit remain incomplete.

## Priority and exact next action

1. Inspect Pi's retry loop/tests and PiSharp's `ConversationRun`/`ObservedChatClient` boundaries; implement low-level RPC retry-attempt semantics without conflating provider request retries, with deterministic event-order/process tests.
2. Continue extracting cohesive session/runtime responsibilities from `Program.cs` alongside RPC command/event work; use that boundary to implement cross-project session CWD switching.
3. Continue the parity matrix in order and keep all known TUI residuals explicit through the final audit.
