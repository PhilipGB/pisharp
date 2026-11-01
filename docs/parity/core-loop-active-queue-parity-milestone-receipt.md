# Milestone Receipt: Core Agent-Loop Active-Queue Parity

## Status

Complete. The full 41-case differential matches pinned Pi: **41 match, 0 differ, 0 one-sided,
0 error**, with no unresolved and no skipped mandatory case and no known-difference allowance.

## Pins

| Item | Value |
| --- | --- |
| Pinned Pi oracle | `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (`tools/parity/pi-reference.txt`) |
| PiSharp implementation | `4e77f7c2` (branch `milestone/core-loop-active-queue-parity`) |
| Branch base | `fc75ba2ad88fc3044b152fcc647144498064db59` |
| Acceptance contract | `docs/parity/fixtures/core-loop-active-queue-parity-2026-10-31.json` |
| Evidence | `docs/parity/fixtures/core-loop-active-queue-parity-2026-10-31/evidence/` |

The Pi checkout is resolved by `--pi`, then `PISHARP_PI_CHECKOUT`, otherwise a clear configuration
error; no developer-specific path is embedded in the tooling.

## Scope

Active steering and follow-up (one-at-a-time and `all` delivery), abort while queued, provider
active, or tool active, provider failure before and after partial content, tool failure beside
concurrent work, concurrent tool calls with steering/follow-up, queued-message ordering around tool
completion and provider requests, preservation/restoration/discard of queued messages,
exactly-once execution and persistence of tool calls and results, and settlement plus the following
prompt.

Out of scope by instruction: provider parity, TUI rendering, session-manager parity, compaction
parity, extension parity, general refactoring.

## Root cause closed by this milestone

The remaining class (`D2 F2 F3 F5 H1`, and under load `D4`) was one defect: **completion-observation
ordering**. Pinned Pi runs its agent loop and its input handling on a single event loop, so once a
tool call returns, its completion is published before any accepted steering or follow-up can be
observed. PiSharp runs the run pipeline and the RPC command loop on different tasks, and the
completion path awaits durable persistence, so an accepted steer could be written before the
`tool_end` and tool result of a tool whose side effect had already happened.

Two cooperating changes in the runtime, not per-case patches (every event handed to the run's
stream goes through one counted publication helper, so the producer watermark and the consumer count
describe exactly the same events — `_emittedEvents` can no longer exceed `_producedEvents`):

- `ToolCompletionGate` (`src/PiSharp.Runtime/Sessions/ToolCompletionGate.cs`) holds a tool from the
  moment its work returns until its completion event is published. `DurableToolFunction` opens the
  scope after the tool body returns and closes it when the method exits, so it covers the
  persistence and publication path and is exception-safe.
- `ConversationRun` records how many events it has produced and how many the consumer has written;
  accepting steering or follow-up waits for both — no tool may be mid-publication, and every
  produced event must be written before the queue update is. A stream end releases the wait so
  acceptance never depends on records nobody will write, and tools that have not returned never
  delay steering.

`RpcMode` now accepts queued input through `ConversationRun.QueueRpcInputAsync` for both the
`steer`/`follow_up` commands and the `prompt` + `streamingBehavior` form, so there is one queue and
one session mutation path.

## Fail-first evidence

- With both waits removed, `F3` reproduces the defect directly (`events[8] tool_end != queue`,
  `queue != resp follow_up`, `resp follow_up != tool_end`), and the corpus reports the whole
  class.
- `ToolCompletionGateTests` fails if the gate is not held between a tool's return and its
  completion publication, and passes for a blocked tool that has not returned (a blocked sibling
  must not delay steering).
- The G3 baseline race recorded earlier (`RestoredSystemRevisionsAreSnapshottedAtRestoreAndNotRewrittenByARun`)
  fails with the baseline becoming `[original prompt, ]` when it reads live revisions.

## Deterministic stimulus corrections

Three cases raced something other than product behavior; the stimulus was fixed rather than the
comparison relaxed.

- `F5`/`H1` steered on a fixture marker that is written *before* the MCP response reaches the
  client. A 400 ms head-start probe showed the client's steer was accepted 6 ms before the tool
  body returned, then 39 ms of persistence before publication, so the case was measuring transport
  latency. The script now steers only after the product itself has published the completed tool
  (`wait tool_execution_end 1`), which is exactly the invariant under test.
- `F4`/`D4` released their aborted gates from teardown in one loop, so the two aborted tools raced
  each other for the effect log. The releases are now ordered and observed, so the effect order is
  defined by the stimulus rather than by scheduling.

## Calibration

`tools/parity/core_loop/calibrate_order.py` is diagnostic only and never defines the contract. It
spreads runs across scheduling conditions and reports independent flags — `pi-varies`,
`pisharp-varies`, `pisharp-outside-pi-observed`, `gap` — so Pi varying can never hide additional
PiSharp variation. `test_order_calibration.py` freezes that property with synthetic orders.

Calibration measured pinned Pi producing **both** arrival orders for MCP calls dispatched from one
assistant turn. Because both implementations dispatch those calls concurrently (pinned Pi maps over
the call array and awaits `Promise.all`), that arrival order is not part of either contract.

The relaxation is scoped to a **single concurrent batch**, not applied to the case-level list: a
case whose observation declares more than one assistant turn with tool calls is compared strictly,
so an arrival order that crosses turns can never compare equal. `test_compare_strictness.py` proves
both directions — cross-batch reordering is a difference, single-batch arrival order is not — plus
that effect, event and declared-tool reordering and a different call set are still reported.

Nothing else is permitted to differ, and no alternative ordering is frozen as an allowance. The
contract states this as `orderingPolicy: strict-projected-equality` with an `orderingNote` naming
the two disclosed normalisations (single-batch MCP arrival order, and the persisted
system/context entry position compared as counted presence).

## Acceptance contract and verification

The contract is generated from the run summary by `make_committed_evidence.py`, so the totals,
per-case expectations and both revisions come from the evidence instead of being hand-written. It
declares `orderingPolicy: strict-equal` and `allowDocumentedDifferences: false`.

`verify_acceptance.py` re-derives every claim from the per-case evidence and fails on a missing,
skipped or unresolved case, a wrong Pi SHA, a wrong PiSharp SHA, a mismatched total, or a case that
differs while the contract permits no difference.

`verify_acceptance_negative_controls.py` proves rejection for: dropped case, unresolved status,
wrong Pi SHA, wrong PiSharp SHA, total mismatch, differ-as-match, missing justification, and a
documented difference when differences are not allowed.

## Validation

| Check | Result |
| --- | --- |
| Full differential corpus (41 cases, fresh artifacts) | 41 match / 0 differ |
| Focused parity suites | 257 passed |
| Gate and writer regression suites | 6 passed |
| Python comparator/calibration controls | 13 passed |
| `dotnet format --verify-no-changes` | clean |
| `dotnet build --warnaserror` | clean |
| Full `dotnet test PiSharp.slnx` | CI is the authoritative gate; local full runs are limited by missing `rg` and testhost instability on this machine |

## Files

Added: `src/PiSharp.Runtime/Sessions/ToolCompletionGate.cs`,
`tests/PiSharp.Tests/Runtime/Sessions/ToolCompletionGateTests.cs`,
`tools/parity/core_loop/pi_checkout.py`, `tools/parity/core_loop/test_compare_strictness.py`,
`tools/parity/core_loop/test_order_calibration.py`.

Changed: `DurableToolFunction`, `ConversationRun`, `ConversationRun.InputQueueOverloads`, `PiAgent`,
`RpcMode`, `RpcEventWriter`, `cases.py`, `compare.py`, `calibrate_order.py`,
`make_committed_evidence.py`, `verify_acceptance.py`, `verify_acceptance_negative_controls.py`.
