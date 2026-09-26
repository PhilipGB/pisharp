# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, using Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages remain excluded unless a core capability needs them.

**Stop condition:** current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and no major architecture bottleneck remains.

## Current checkpoint

- Source head `2f0250ee91a500cdb6b08dce2e940b849089ba26` passed exact-head Linux CI run [36217090057](https://github.com/PhilipGB/pisharp/actions/runs/36217090057): format, warnings-as-errors build (0 warnings/errors), and 574 tests (0 failed, 0 skipped).
- Current Pi `main`: `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31`; durable baseline: `b3487650f6378f1b0d1643dd254445ceb4a98035`. The current-main review remains scoped, not a full audit.
- Implemented subsets now include Pi JSONL v1-v3 import/export, cross-project runtime switching for interactive import and RPC `switch_session`, RPC new/fork/clone, low-level-turn lifecycle projection with bounded session retry events, and `session_info_changed` before its response. These have deterministic unit/process/PTY evidence; Pi-wide behavioral equivalence is not established.
- Major gaps remain in Pi-shaped RPC entry/tree data and turn/message/tool/other session event families; session manager import/export, recovery and concurrency differentials; settings/resources/extensions; multimodal and provider/auth breadth; coding-tool residuals; and TUI/layout residuals.
- Architecture: `Program.cs` is 1,132 lines with mixed composition/application behavior; `RpcMode.cs` is 451 lines after command-family extraction; `TerminalScreen.cs` is 631 lines after prior decomposition. Continue bounded capability extraction as new work requires it.

## Priority and exact next action

1. Replace RPC `get_entries` and `get_tree` canonical `pisharp` payloads with current-Pi `SessionEntry` and nested `SessionTreeNode` projections. Inspect the pinned RPC tests, reuse one session-entry projection boundary with Pi JSONL export, and add exact shape, cursor, labels, branch ordering and process tests.
2. Continue remaining RPC event/response families and session interoperability/recovery, using current Pi sources and deterministic differential fixtures.
3. Continue through settings, resources, extensions, multimodal behavior, provider/auth breadth, coding-tool residuals and TUI residuals; keep extracting cohesive application boundaries alongside capability work.
