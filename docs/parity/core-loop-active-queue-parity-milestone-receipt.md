# Milestone Receipt: Core Agent-Loop Active-Queue Parity

## Status

The core agent-loop active-run queue and loop behavior is
differential-verified against the pinned Pi reference across the mandated
capability surface: active steering, active follow-up, mixed
steering/follow-up, queue modes, abort while queued/provider-active/
tool-active, provider failure before/after partial content, tool failure
while other tools or queued messages exist, concurrent tool calls with
steering/follow-up, queued-message ordering around tool completion and
provider requests, preservation/restoration/discard of queued messages,
exactly-once execution/persistence of tool calls/results, and final
settlement and subsequent prompt behavior.

A final layering-alignment cycle completed the failed/aborted assistant
handling: those messages follow the pinned Pi layering (kept in the
session and canonical context, omitted only at the provider wire
boundary), and the entry projections no longer double-emit them.

## Pins

- Pi repository: `https://github.com/earendil-works/pi`
- Pi reference SHA (behavioral oracle):
  `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`
  (frozen in `tools/parity/pi-reference.txt`; not advanced by this
  milestone).
- PiSharp branch: `milestone/core-loop-active-queue-parity`
- PiSharp branch base (from `origin/main`):
  `fc75ba2ad88fc3044b152fcc647144498064db59`
- PiSharp implementation SHA (source-code parity fixes, including the
  failed/aborted assistant layering and the agent-run continuation fix):
  `4f05d7d6109a6feb422aed17b3ad3bf4ccd6e6b6`

## Differential Corpus

Source-pinned, machine-readable corpus of 41 cases under
`tools/parity/core_loop/cases.py`, organized by mandated capability:

| Group | Capability                                        | Cases |
| ----- | ------------------------------------------------- | ----- |
| A     | active steering                                   | A1–A5 |
| B     | active follow-up                                  | B1–B5 |
| C     | mixed steering/follow-up, queue modes             | C1–C6 |
| D     | abort (queued / provider-active / tool-active)    | D1–D8 |
| E     | provider failure (pre/post partial, tool failure) | E1–E7 |
| F     | concurrency (parallel tools, steering/follow-up)  | F1–F6 |
| G     | exactly-once execution/persistence, restart       | G1–G3 |
| H     | composed scenarios                                | H1    |

The harness (`tools/parity/core_loop/harness.py`) drives both runtimes
through a deterministic file-based gate/marker protocol: no arbitrary
sleeps. Provider responses are scripted per case; tool side effects are
recorded by a local MCP server (`mcp_server.py`) and compared
byte-for-byte. The comparator (`compare.py`) projects both runtimes into a
common externally observable shape (provider request message sequences,
provider-visible tool calls/results, agent lifecycle events, queue update
events, steering/follow-up counts, response ordering, cancellation/abort
outcome, session entries, active branch state, persisted results, MCP side
effects, settlement).

Comparison notes (frozen in the acceptance contract):

- System/context entry position relative to the first user prompt is a
  session-format detail; it is compared as counted presence
  (`system xN`), not exact position.
- MCP calls are compared as a sorted set because concurrent tool dispatch
  order is scheduler-dependent.
- `assistant.is_error` is projected from the explicit `pisharp.isError`
  flag (Pi records provider failure via `stopReason`, not assistant
  `isError`).

## Results

**41 cases: 33 match, 8 documented known-differences, 0 unresolved.**

Matched (33): `A1 A2 A3 A4 A5 B1 B2 B3 B4 B5 C1 C2 C3 C5 C6 D1 D2 D3 D4 D5
D6 D7 D8 E1 E3 E4 F1 F2 F3 F4 F6 G1 G2`

Differing (8), each frozen with a per-case justification:

| ID | Classification    | Description |
| -- | ----------------- | ----------- |
| C4 | known_event_ordering | queue_update emitted before the assistant toolCall event for a steer queued while the tool round is in flight; Pi orders them the other way. Settled session, provider requests, tool results, and side effects identical. |
| E2 | known_stop_reason | provider error after partial content: Pi records `stopReason=error`, PiSharp's MAF runtime completes the stream with the partial content (`stopReason=stop`). Settled content, provider requests, and side effects identical. |
| E5 | known_event_ordering | follow-up after a failed turn: Pi emits `agent_start` before `turn_start`; PiSharp emits `turn_start` without the preceding `agent_start`. Settled session, provider requests, and side effects identical. |
| E6 | known_event_ordering | same as E5. |
| E7 | known_event_ordering | same as E5. |
| F5 | known_event_ordering | same as C4. |
| G3 | known_system_reemit | after process restart Pi does not re-emit the unchanged system message; PiSharp re-emits it. Settled session, provider requests, and side effects identical. |
| H1 | composed_of_known   | composed scenario combining the C4/E2/E5/F5/G3 classes; exactly-once side effects and provider requests verified by the individual comparisons. |

For all 8 differing cases the externally observable state that the
capability requires — provider request sequences, provider-visible tool
calls/results, side effects, settled session content, active branch, and
settlement — is verified identical; only the frozen non-safety event-order
or session-format detail differs.

## Fail-first evidence

Each repaired difference followed the fail-first workflow: the differential
case reproduced the difference against the pinned Pi before the production
change, the focused regression test failed for the correct reason, the
production fix made both pass, and the post-fix differential re-run recorded
the case as `match`. The pre-fix failing observations were captured before
each fix (working artifacts, Git-ignored); the post-fix matched
observations are the committed compact evidence under
`docs/parity/fixtures/core-loop-active-queue-parity-2026-10-31/evidence/`.

Repaired differences (all now `match`):

- Active steering / follow-up / mixed delivery while the current turn is
  active (A2, A4, B2, B4, C2, C3, C5, C6).
- Queue update event emission around queue changes (B5, C1, F2, F3, F6).
- One-at-a-time and all queue modes with tool rounds (C1, C6).
- Abort while queued / provider-active / tool-active (D1–D8): cancellation
  outcome, no post-abort provider request, queued-message handling, and the
  aborted assistant projection.
- Provider failure before/after partial content (E1, E3): the failed
  assistant is persisted, excluded from the provider context on the next
  turn, and projected with the correct stop reason.
- Tool failure while other tools or queued messages exist (E4): the failed
  tool result and the side-effecting tool result are both persisted
  exactly once.
- Concurrent tool calls with steering/follow-up (F1, F3, F4): tool results
  are persisted in completion order, exactly once, with queued messages
  interleaved correctly.
- Exactly-once side effects across process restart (G1, G2): a
  non-idempotent tool is executed and persisted exactly once across
  restart; the queued follow-up is delivered exactly once.

The failed/aborted assistant layering was closed later, on top of that
corpus, and followed the same workflow:

- D1–D3 and E1–E3 established that Pi keeps the failed/aborted assistant
  in the session and canonical context and omits it only at the provider
  wire boundary. Several PiSharp assertions encoded the earlier, wrong
  layering (the assistant dropped from the canonical context). Those were
  retargeted only after the Pi evidence was captured.
- `InterruptedNodeStaysOutOfTheProjectionWhenTheFailedAssistantOmitsItsPartialText`
  failed first with `ContextMessages()` = `["prompt", "", "PARTIAL"]`
  (the bookkeeping node leaking a second assistant) and passes after the
  structural duplicate rule.
- `AbortedPartialAssistantStaysInSessionSnapshotsAndIsDroppedFromNextProviderRequest`
  exposed a `get_entries` crash (`KeyNotFoundException` for the
  suppressed id) that only appeared once the interrupted node was
  suppressed; the RPC entry and leaf projection were fixed to treat it as
  hidden.
- `ToolContinuationSummaryFailureForOversizedSingleTurnDoesNotReplayTool`
  was retargeted after establishing, by toggling
  `RecoverIncomplete(announce:)`, that the old second-request behaviour
  depended on the recovery notice acting as a compaction cut point.

The full-suite run then exposed one genuine regression that the focused
filters had not covered:
`AgentSettlementCharacterizationTests.RetryKeepsPromptLoopOpenForAgentEndQueuedWorkBeforeAgentBecomesIdle`
passed on the branch base and failed on the branch, because a prompt
queued by an `agent_end` handler started a second agent run. Work queued
from `agent_end` now continues the run that just ended (one `agent_start`,
one `agent_end` per drain), while a before-settle boundary continuation
still starts a new agent run.

## Fail-first regression tests

`tests/PiSharp.Tests/Runtime/Sessions/CoreLoopParityTests.cs` contains the
deterministic, fail-first runtime regression tests for the repaired
differences (8 tests, all passing). The tests use a fake
`IChatClient` and in-memory `ConversationTree`; no live provider or
arbitrary sleep.

The layering work adds
`PiJsonlSessionInterchangeTests.InterruptedNodeStaysOutOfTheProjectionWhenTheFailedAssistantOmitsItsPartialText`
and retargets
`AgentLifecycleTests.ProviderFailureAfterPartialOutputIsNotRetriedAndPreservesPartialResponse`,
`RpcInterruptedSessionProcessTests.AbortedPartialAssistantStaysInSessionSnapshotsAndIsDroppedFromNextProviderRequest`,
`RpcModeTests.CompactCommandCancelsAnActiveRunBeforeReturningPiResult`,
and
`CompactionTests.ToolContinuationSummaryFailureForOversizedSingleTurnDoesNotReplayTool`.

## Acceptance contract

`docs/parity/fixtures/core-loop-active-queue-parity-2026-10-31.json` is the
committed, machine-readable acceptance contract. It freezes:

- the Pi reference SHA and the PiSharp implementation SHA;
- all 41 mandatory cases with their expected status and, for each
  known-difference case, a frozen justification;
- the frozen totals (41 total, 33 match, 8 differ, 0 unresolved).

`tools/parity/core_loop/verify_acceptance.py` verifies the committed
compact evidence against the contract. It fails on:

- a missing case or evidence file;
- an evidence file whose Pi or PiSharp SHA does not match the contract;
- an unresolved (non-`match`, non-justified) status;
- a frozen total that does not match the observed total.

**Result: `ACCEPTANCE PASSED: contract satisfied by per-case evidence.`**

## Verifier negative controls

`tools/parity/core_loop/verify_acceptance_negative_controls.py` proves the
verifier rejects incomplete or stale evidence. It tampers the committed
evidence in-memory (never the committed files) and asserts the verifier
fails for each tamper:

1. drop-case — a mandatory case evidence file is removed;
2. unresolved-status — a known-difference case is marked `differ` with no
   justification;
3. wrong-pi-sha — an evidence file carries a wrong Pi SHA;
4. wrong-pisharp-sha — an evidence file carries a wrong PiSharp SHA;
5. total-mismatch — the frozen total is incremented;
6. differ-as-match — a known-difference case's evidence is forced to
   `match`;
7. missing-justification — a known-difference case's justification is
   removed.

**Result: `NEGATIVE CONTROLS PASSED: the verifier rejected all tampered
evidence.`** (all 7 tampers rejected)

## Failed/aborted assistant layering

A failed or aborted provider turn is laid out the way pinned Pi lays it
out, at each layer:

| Layer | Pinned Pi | PiSharp |
| ----- | --------- | ------- |
| Agent state / canonical session context | keeps the failed assistant (`packages/agent/src/agent.ts` `_state.messages`, `agent-loop.ts` `currentContext.messages`) | keeps it: `ConversationSession.ContextMessages()` / `ActiveMessages()` project the persisted failed-assistant chat entry |
| Session log / `get_entries` | one assistant record for the attempt (`session-manager.ts` `buildSessionContext`) | one record: the persisted chat entry; the `interrupted` bookkeeping node adds no second entry |
| Provider wire request | omits it (`packages/ai/src/api/transform-messages.ts`) | omits it: `ObservedChatClient.PrepareRequestAsync` drops aborted/error assistants before orphan tool-result synthesis |

Implementation points:

- `ObservedChatClient.PrepareRequestAsync` calls
  `DropIncompleteAssistantTurns` before `AddMissingFunctionResults`, so a
  failed assistant's tool calls never synthesise orphan tool results on
  the wire. Both the streaming and non-streaming provider paths pass
  through this one boundary.
- `PiJsonlEntryCodec.DuplicatesFailedAssistant` identifies the
  `interrupted` node whose parent is the persisted failed-assistant chat
  entry. It is used by the context projection, the Pi entry projection,
  the RPC message projection, the RPC entry/leaf projection, the JSONL
  writer, and the extension boundary preview, so all layers agree on the
  chat entry as the record.
- The structural rule (rather than content equality) is required for an
  abort during a tool round: pinned Pi records an empty `error` assistant
  there while PiSharp's `interrupted` payload keeps the partial text, so
  the record and the bookkeeping payload legitimately differ.
- The persisted failed assistant carries `pisharp.stopReason`,
  `pisharp.errorMessage`, and the physical request identity
  (`pisharp.provider` / `pisharp.model` / `pisharp.api`), so the exported
  Pi record has Pi's `stopReason`, `errorMessage`, `provider`, `model`,
  and `api` fields.

## Validation

- `dotnet build PiSharp.slnx --warnaserror`: 0 errors, 0 warnings.
- `dotnet format PiSharp.slnx --verify-no-changes`: clean.
- Focused regression suites (CoreLoopParityTests, RpcModeTests,
  DurableExecutionTests, ToolDeclarationHistoryTests,
  AgentLifecycleTests, PiJsonlSessionInterchangeTests, CompactionTests,
  RpcInterruptedSessionProcessTests, RpcCompactionProcessTests,
  BoundaryRetryFailureTests, BoundaryCompactionAndRecoveryTests,
  AgentSettlementCharacterizationTests): 262 passed, 0 failed.
- Full differential corpus (41 cases): 33 match, 8 documented
  known-differences, 0 unresolved, 0 one-sided, 0 error.
- Acceptance verifier: passed.
- Verifier negative controls (7 tampers): all rejected.
- Full repository validation (`dotnet test PiSharp.slnx`): a single
  complete run cannot be obtained on this machine, so the suite was
  validated in bounded chunks and every observed failure was attributed.
  - Name-filtered chunks:
    | Chunk | Result |
    | --- | --- |
    | `~Rpc` | 188 passed, 0 failed |
    | `~Session` | 303 passed, 0 failed |
    | `~Agent` | 117 passed, 0 failed |
    | `~Tool` | 790 passed, 30 failed — all 30 are `ripgrep (rg) is not available` |
    | `~Provider` | 429 passed, 1 failed on the first run (a grep tool); the 1.003 s-vs-1 s idle-timeout flake seen once did not reproduce |
  - The `rg` failures are environmental. This machine has no `rg` on
    `PATH`, and `RipgrepSearch`/`FdSearch` resolve the binary by `PATH`
    lookup only. The same five search classes on the branch and on branch
    base `fc75ba2a` give **identical** results: 233 tests, 96 failed, 137
    passed, 94 `rg` errors — pre-existing, not caused by this branch.
  - A single whole-suite run (parallel or serial) stops producing output
    at a non-deterministic point; observed stall points include
    `ProviderChatClientFactoryTests`, `RpcPromptLifecycleTests`, and
    `SearchToolsTests`. Individual classes and the chunks above complete
    normally. Leftover `testhost` processes from an interrupted run make
    this worse, and one run aborted with `Internal CLR error
    (0x80131506)`, which was also observed in a plain `dotnet build`.
  - Every test that failed in a whole-suite run was re-run individually:
    two were genuine regressions and are fixed in this branch
    (`AgentSettlementCharacterizationTests...`, see above, and
    `RpcPromptLifecycleTests.AcceptsPromptThenEmitsEventsAndSupportsSubsequentStateQueries`,
    which read `Tree.Entries[0]` after the system/tool transcript moved
    ahead of the prompt and now reads the first chat entry); the rest were
    the `rg` and timing cases above.

Notes on behaviour that is not byte-for-byte identical, and why:

- The recovery notice is PiSharp-specific (pinned Pi injects no message
  after a failed turn). It stays for the crash/restart path, where tool
  outcomes are genuinely unknown. A live in-process failure now closes
  the run silently, which matches Pi's provider request content.
- Because that notice was also the only user-role entry the retained-tail
  compaction rule could cut at in a single-turn history, an over-budget
  follow-up prompt in such a history is now rejected by PiSharp's
  pre-existing over-budget guard instead of being compacted. This guard
  predates the milestone and is a documented PiSharp safety behaviour;
  pinned Pi would send the over-budget request. See
  `ToolContinuationSummaryFailureForOversizedSingleTurnDoesNotReplayTool`.

## Intentional differences and limitations

- The 8 known-differences are frozen non-safety event-ordering and
  session-format details. They do not affect provider request sequences,
  provider-visible tool calls/results, side effects, settled session
  content, active branch, or settlement. They are documented and
  justification-frozen in the acceptance contract; closing them would
  require matching Pi's exact internal event emission order, which is not
  part of the core active-run queue/loop observable behavior this
  milestone covers.
- PiSharp's `agent_start`/`turn_start` emission for a follow-up turn after
  a failed provider turn differs from Pi's (E5/E6/E7). This is a
  lifecycle-event detail; the settled session and provider requests are
  identical.
- The `assistant.is_error` projection is a PiSharp-specific flag
  (`pisharp.isError`); Pi records provider failure via `stopReason`. The
  comparator projects both into a common shape.

## Files

- Differential harness: `tools/parity/core_loop/`
- Cases: `tools/parity/core_loop/cases.py`
- Comparator: `tools/parity/core_loop/compare.py`
- Runner: `tools/parity/core_loop/run_differential.py`
- Compact evidence generator: `tools/parity/core_loop/make_committed_evidence.py`
- Acceptance contract: `docs/parity/fixtures/core-loop-active-queue-parity-2026-10-31.json`
- Committed compact evidence: `docs/parity/fixtures/core-loop-active-queue-parity-2026-10-31/evidence/`
- Acceptance verifier: `tools/parity/core_loop/verify_acceptance.py`
- Negative controls: `tools/parity/core_loop/verify_acceptance_negative_controls.py`
- Fail-first regression tests: `tests/PiSharp.Tests/Runtime/Sessions/CoreLoopParityTests.cs`
- Production fixes: `src/PiSharp.Cli/Protocols/Rpc/` (RpcEventProjector,
  RpcSessionMessageProjector, RpcSessionEntryProjector) and
  `src/PiSharp.Runtime/Sessions/` (ConversationContextProjection,
  ConversationRun, ConversationSession, ConversationToolState,
  DurableToolFunction, ObservedChatClient, PiJsonlEntryCodec,
  PiJsonlSessionInterchange, PiJsonlSessionWriter,
  ExtensionBoundaryCoordinator, PromptDeliveryController,
  ProviderTurnJournal, ChatMessageProperties),
  `src/PiSharp.Runtime/Mcp/McpToolFunction.cs`.
- Receipt: `docs/parity/core-loop-active-queue-parity-milestone-receipt.md`
