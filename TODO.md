# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless needed for a core capability.

**Stop condition:** a full audit against current Pi `main` finds no material in-scope gaps and required validation/differential evidence passes.

## Current checkpoint

- Source head `3c766cd71fa297c1fa08ee42e371c93b3841e53e` passed Linux CI run [36207133787](https://github.com/PhilipGB/pisharp/actions/runs/36207133787): format verification, warnings-as-errors build, and 543 tests (0 failed, 0 skipped). Local focused RPC/lifecycle tests passed 25/25; full tests passed 543/543.
- Current Pi `main`: `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31`; durable baseline: `b3487650f6378f1b0d1643dd254445ceb4a98035`. Retry sources and tests were inspected at current `main`; this is scoped evidence, not a full audit.
- RPC model/thinking handlers are extracted. Active-run thinking changes reach the next provider request and persist after the current MAF history write. `agent_end` currently has Pi's payload shape but is emitted once for PiSharp's accepted run with `willRetry: false`; Pi emits it per low-level run attempt. Provider-request retries remain separate from session retry semantics.
- Major gaps remain in RPC/events/retry and session controls/recovery/interoperability; cross-project sessions; settings; resources; extensions; multimodal and provider/auth breadth; coding-tool residuals; TUI terminal layout/styling; and current-upstream audit/differentials.
- Architecture concerns: `Program.cs` is 1,064 lines of mixed startup/application behavior; `RpcMode.cs` is 509 lines; `TerminalScreen.cs` is 631 lines after prior extractions. Continue extracting cohesive boundaries as the next capability requires them.

## Priority and exact next action

1. Change RPC event projection to emit one `agent_start`/`agent_end` per accepted low-level turn, with that turn's canonical messages; make queued-turn tests assert segmented events. Then add session-level retry state and `willRetry`/`auto_retry_start`/`auto_retry_end`/`abort_retry`, keeping `ObservedChatClient` provider-request retries distinct.
2. Preserve failed attempts in session history while omitting retryable attempts from the next model context; use deterministic barriers for retry, cancellation, event order, and tool continuation.
3. Extract the next needed application/session boundary from `Program.cs`, then continue RPC and cross-project session parity before moving through the remaining matrix.
