# Milestone Receipt: Core Agent-Loop Active-Queue Parity

## Status

Core differential work complete (41-case corpus, 33 match / 8 frozen
known-differences, acceptance verifier and negative controls passing at
implementation SHA `3cb1d455`). A final layering-alignment cycle is in
progress and the branch is paused at a clean point: failed/aborted
assistant messages now follow the pinned Pi layering (kept in the session
and canonical context, omitted only at the provider wire boundary), the
entry projections no longer double-emit them, and three focused tests are
retargeted to the Pi-aligned behavior. Two focused-test failures remain
open (see "Pause state and resume checklist" below); the committed
acceptance evidence predates the layering work and must be regenerated
before the PR is opened.

## Pins

- Pi repository: `https://github.com/earendil-works/pi`
- Pi reference SHA (behavioral oracle):
  `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`
  (frozen in `tools/parity/pi-reference.txt`; not advanced by this
  milestone).
- PiSharp branch: `milestone/core-loop-active-queue-parity`
- PiSharp branch base (from `origin/main`):
  `fc75ba2ad88fc3044b152fcc647144498064db59`
- PiSharp implementation SHA (source-code parity fixes):
  `3cb1d455f7442088e2fad55ef3ce8e7e95e0a253`

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

## Fail-first regression tests

`tests/PiSharp.Tests/Runtime/Sessions/CoreLoopParityTests.cs` contains the
deterministic, fail-first runtime regression tests for the repaired
differences (8 tests, all passing). The tests use a fake
`IChatClient` and in-memory `ConversationTree`; no live provider or
arbitrary sleep.

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

## Validation (state at pause)

- `dotnet build PiSharp.slnx --warnaserror`: 0 errors, 0 warnings (after
  the layering changes; no temporary debug code in the tree).
- Focused retarget results so far:
  - `RpcModeTests.CompactCommandCancelsAnActiveRunBeforeReturningPiResult`
    passing (aborted assistant retained in the compacted tail, count 3).
  - `RpcInterruptedSessionProcessTests.AbortedPartialAssistantStaysInSessionSnapshotsAndIsDroppedFromNextProviderRequest`
    (renamed from `...IsVisibleInSessionSnapshotsAndNextProviderRequest`):
    wire and entry assertions retargeted to Pi parity. Currently failing:
    with the layering changes in the tree, the RPC child process goes
    silent after `abort` (process stays alive, stdout EOF). Verified by
    stash: at branch HEAD `481880e1` the flow completes and fails only on
    the pre-suppression double-entry assertion, so the hang is caused by
    the layering cycle, not pre-existing. Next step: capture the child's
    stderr at the abort wait and inspect the abort path in
    `ConversationRun` (persist failed assistant + interrupted node +
    `RestoreExecutionAsync`) against the canonical context that now
    includes the failed assistant.
  - `CompactionTests.ToolContinuationSummaryFailureForOversizedSingleTurnDoesNotReplayTool`:
    notice assertions retargeted to Pi parity (a live in-process failure
    emits no recovery notice; crash/restart recovery paths keep it).
    One count assertion still fails: `Assert.Equal(2, client.Requests)`
    observes 1 — the follow-up prompt did not produce a second model
    request; the path that swallows the turn (auto-compaction summary
    failure handling) must be pinned before the test can pass.
- Full differential corpus (41 cases): last full run 33 match, 8
  documented known-differences, 0 unresolved — at implementation SHA
  `3cb1d455`, i.e. before the layering cycle. Must be re-run after the
  focused tests are green.
- Acceptance verifier and negative controls: passing against the
  committed contract/evidence (stale relative to the layering cycle).
- Full repository validation (`dotnet test PiSharp.slnx`): the complete
  run is intermittently blocked by an environmental test-host hang —
  orphaned `testhost` processes keep the output pipe open after
  `dotnet test` exits, and `Internal CLR error (0x80131506)` aborts have
  occurred even in plain `dotnet build`. Focused filters run reliably and
  are used for validation; a clean full-suite pass (or definitive
  attribution of the hang as environmental) is still required.

## Pause state and resume checklist

1. Root-cause and fix the post-`abort` RPC silence in
   `AbortedPartialAssistantStaysInSessionSnapshotsAndIsDroppedFromNextProviderRequest`
   (capture child stderr; inspect the abort/persistence path in
   `ConversationRun` and `RestoreExecutionAsync` with the failed assistant
   present in the canonical context).
2. Root-cause the missing second model request in
   `ToolContinuationSummaryFailureForOversizedSingleTurnDoesNotReplayTool`
   (auto-compaction summary-failure handling on the follow-up prompt).
3. Re-run the focused filter
   (`CoreLoopParityTests|RpcModeTests|DurableExecutionTests|ToolDeclarationHistoryTests|AgentLifecycleTests|PiJsonlSessionInterchangeTests|CompactionTests|RpcInterruptedSessionProcessTests|RpcCompactionProcessTests|BoundaryRetryFailureTests|BoundaryCompactionAndRecoveryTests`)
   to `Failed: 0`.
4. Re-run the 41-case differential corpus; confirm 33 match / 8 differ
   (C4 E2 E5 E6 E7 F5 G3 H1) or update the frozen differences with
   evidence.
5. Regenerate the committed compact evidence and acceptance contract at
   the final implementation SHA (`make_committed_evidence.py`); re-run
   `verify_acceptance.py` and `verify_acceptance_negative_controls.py`.
6. Obtain a clean full `dotnet test PiSharp.slnx` pass or definitively
   attribute the environmental test-host hang (orphaned testhost /
   CLR 0x80131506) with evidence.
7. Update this receipt's validation section, commit, push, and open the
   PR against `main` with the exact implementation SHA, Pi pin
   `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`, verification results, and
   intentional differences. Leave the PR open/unmerged.

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
  RpcSessionMessageProjector) and `src/PiSharp.Runtime/Sessions/`
  (ConversationContextProjection, ConversationRun, ConversationSession,
  ConversationToolState, DurableToolFunction,
  PromptDeliveryController, ProviderTurnJournal, ChatMessageProperties),
  `src/PiSharp.Runtime/Mcp/McpToolFunction.cs`.
