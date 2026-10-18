# Native extension agent settlement — 2026-10-18

This receipt records the pinned-source contract, the bounded native SDK implementation, and deterministic evidence. It does not promote broad extension parity; `feature-extensions-net-api` and `extension-orchestration-api` remain `in_progress`. Final local and exact-head CI results are recorded below.

## Pins and integration checkpoint

- Pi reference: `earendil-works/pi@b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
- Actual PR #13 merge commit and PiSharp milestone base on `main`: `ca47d554bd48887147100ab11195c8086361963d` (parents `eef6f3f83247758d397b4e313d2c26b0088ee9ac` and `1172dc8ce270ba5a16ae9cdc6a52afed57165805`).
- PR #13 head: `1172dc8ce270ba5a16ae9cdc6a52afed57165805`.
- Required exact-head CI runs `39126827078` (push) and `39126829634` (PR) were verified green before merge by the milestone coordinator.
- Before merge, PR #13 was open/draft and mergeable, had no reviews/comments or unexpected commits, and both required CI runs reported success on its expected head. It was marked ready and merged using the repository's normal merge-commit method; the clean milestone branch was created only after fetching and verifying the actual merge.

## Pinned behavior

### `turn_end`: persisted turn boundary, drafts, and next request

Pi's low-level `agent-loop.ts` calls the awaited `finishTurn` hook and then emits `turn_end`; on ordinary completion that is after assistant and tool-result messages have been emitted. `AgentSession` persists regular messages during `message_end`, so the assistant and tool-result entry IDs are available to the boundary. `TurnEndEvent` carries the `turnIndex`, assistant `message`, `toolResults`, `messageEntryId`, `toolResultEntryIds`, boundary state, and activity `outcome` (`packages/agent/src/agent-loop.ts`, `packages/coding-agent/src/core/agent-session.ts`, `packages/coding-agent/src/core/extensions/types.ts`). The suite also proves actionable `turn_end` dispatch for a synthetic request-preparation failure even when no provider request ran (`test/suite/agent-session-boundaries.test.ts`, “dispatches actionable turn_end for synthetic run failures”).

For an assistant error or abort, the loop still awaits `finishTurn`, emits `turn_end`, then emits `agent_end`; for a regular turn it emits `turn_end` after tool execution and ends the low-level run only if `finishTurn` chose `end`. Pi maps assistant stop reasons to `completed`, `aborted`, or `error`; an extension callback being cancelled is not itself the activity outcome (`agent-session.ts` boundary dispatch; `extensions/types.ts`, `AgentActivityOutcome`).

The four draft variants are `custom`, `custom_message`, `context_edit`, and `compaction`. `context_edit` names a prior entry and replaces only its content with `{ content }`, or uses `null` to omit that entry from model context. The content type is derived from user, assistant, tool-result, or custom-message content; message metadata is retained. A null `firstKeptEntryId` compaction is self-retaining and keeps no preceding entries (`extensions/types.ts`; `session-manager.ts`, `ContextEditEntry`, `buildContextEntries`, `projectContextEntry`, and `appendCompaction`). Later edits to a target win in the projected context.

Boundary handlers run sequentially in extension/registration order. Each sees the current effective `entries`, current `continue`, and a freshly rebuilt `context`. Returning `entries` replaces the whole draft array; returning `continue` replaces that boolean; omitted result fields preserve the existing value. In-place mutation is also shared with later handlers. The preview includes projected context entries/messages, converted LLM messages, pending agent messages, and `canContinue`. Each handler's effective drafts are applied to an in-memory session manager before rebuilding the next preview. Once dispatch finishes, valid drafts are committed to the canonical session and finalized agent context before continuation (`extensions/runner.ts`, `emitBoundary`; `agent-session.ts`, `_createBoundaryPreviewManager`, `_buildBoundaryContext`, `_commitBoundaryDrafts`).

`canContinue` is true when projected LLM context has a non-system message and does not end in an assistant message, when custom pending messages exist, or based on queued messages: at `turn_end`, any queued agent message qualifies; at `agent_before_settle`, queued input qualifies only if the projected final role is assistant. An explicit continuation request is separately checked against the committed no-draft preview. Invalid explicit continuation is reported and suppressed. A failed explicit request does not suppress a natural tool continuation (`agent-session.ts`, `_buildBoundaryContext`, `_dispatchTurnEndBoundary`; boundary suite, “does not let an invalid explicit continuation suppress natural tool continuation”).

A continuing turn normally reaches exactly one next provider response. Tool calls retain their natural tool-result continuation. Steering messages and follow-up messages preserve the agent loop's ordering even when a boundary also asks to continue: steering is delivered on the handoff request, and follow-up stays queued for the following request. A retain-none compaction draft can discard the just-finished prompt/response from model context while putting its summary into the next provider request. Tests cover compaction handoff, steering/follow-up/both, and threshold compaction after a boundary replacement (`agent-session-boundaries.test.ts`).

### Invalid proposals and recovery

`emitBoundary` catches handler exceptions, reports them, and continues dispatch. Mutations made before a handler throws remain in the shared draft array. It rebuilds the preview after each handler. A preview-build failure is reported as `Invalid boundary entries`; a later handler still runs and can replace the invalid proposal with valid drafts. If the final proposal is still invalid, dispatch returns empty entries and `continue: false` with `valid: false`, so invalid drafts are not committed (`extensions/runner.ts`; `test/extensions-runner.test.ts`, `boundary chaining` tests “reports invalid boundary previews and lets later handlers repair the proposal” and “keeps shared mutations made before a handler throws”). The failed preview leaves the last successfully built context available for the next handler; the final invalid result is not a usable preview.

### Retry, compaction, queued work, and `agent_before_settle`

The low-level loop's `turn_end` is per assistant turn, including retryable error attempts. After the low-level loop emits `agent_end`, `AgentSession._handlePostAgentRun` handles retry first, then automatic compaction, then queued agent messages. Any of these paths continues the active run without entering `agent_before_settle`. Thus a retry can produce another `turn_end`, and boundary drafts are per-turn durable effects; extensions that want one logical-run effect must account for repeated turns. The implementation checks retry and compaction before pre-settlement (`agent-session.ts`, `_handlePostAgentRun`, `_checkCompaction`); retry event tests establish `turn_end` before `agent_end`, with settlement after recovery (`test/suite/agent-session-retry-events.test.ts`).

The loop drains steering and follow-up queues before its `agent_end`. Work enqueued by `agent_end` handlers is considered afterward; if queued, the run starts another low-level `agent.continue()` cycle before pre-settlement. Automatic compaction runs after `agent_end` and before pre-settlement; overflow recovery can retry, while threshold compaction can continue without retry. The compaction path itself also continues once when queued follow-up/steering/custom work is waiting (`agent-session.ts`, `_handlePostAgentRun`, `_checkCompaction`, `_runAutoCompaction`).

`agent_before_settle` runs only when no retry, compaction continuation, or queued low-level continuation remains. It comes after the low-level `agent_end`. Its drafts are committed, pending custom messages are flushed, and the committed preview is rebuilt before deciding whether to continue. A custom message queued during the callback is included in pending preview and can be persisted/delivered by a requested continuation. Queued follow-up input is deferred if an explicit extension continuation can run first; it is delivered when that continuation naturally stops. If pre-settlement is aborted while a handler is awaiting, returned drafts are still committed, but continuation is suppressed (`agent-session.ts`, `_runBeforeSettleBoundary`; boundary suite tests for custom pending messages, follow-up deferral, and abort during hook).

### `agent_settled` and abort

`agent_settled` is emitted once after the active run's retry/compaction/queued continuations and any `agent_before_settle` continuation have finished. Therefore one settled event can follow multiple `agent_start`/`agent_end` cycles. During settled-handler dispatch Pi marks the run inactive; deferred actions that start another run are drained only after all settled handlers return. The settled event's `aborted` flag reports the run's abort request, including an abort during pre-settlement even if the most recent assistant response completed successfully (`agent-session.ts`, `_emitAgentSettled`; boundary suite, “continues from an agent_before_settle custom message before final settlement”, “defers runs started by agent_settled handlers…”, and “commits pre-settlement drafts but suppresses continuation when aborted during the hook”).

## Contract matrix

| Surface | Pinned Pi behavior | PiSharp behavior | Evidence |
| --- | --- | --- | --- |
| `turn_end` entries | Finalized assistant/tool IDs; drafts commit before continuation. | Existing observer precedes additive mutable handlers; all four drafts use canonical session operations and persistence. | `AgentSettlementCharacterizationTests`; `BoundaryDraftProjectorTests`; `MutableAgentBoundaryTests`; `BoundaryPiJsonlEntryCodecTests`. |
| `turn_end` continue | Ordered optional boolean replacement; runnable context required. | One provider continuation in the same logical pump, with no second start or synthetic user entry. | `MutableAgentBoundaryTests.TurnEndDraftContinuationIsVisibleToLaterHandlersAndUsesOneAgentBracket`; external process theory. |
| Preview rebuild | Rebuild after each handler, preserving effective state. | Detached in-memory session snapshot uses the same draft mapper as commit; returned MEAI values clone on access. | `NativeExtensionBoundaryDispatcherTests` (including cancellation during rebuild); `BoundaryDraftProjectorTests.PreviewAppliesAllDraftKindsWithoutChangingCanonicalEntries`. |
| Invalid drafts | Source-aware errors, later repair allowed, invalid final state rejected. | Later handlers receive invalid drafts and last good preview; invalid final drafts/continuation are discarded. | `NativeExtensionBoundaryDispatcherTests` invalid/recovery cases; stale target/tool-pair projector cases. |
| Retry interaction | Provider-level retries and failed logical activities have different boundaries. | Request attempts do not duplicate successful mutable boundaries; exhausted logical failures expose `Error`; successful logical index remains stable; abort after retry cannot replay a stale failed attempt. | `MutableAgentBoundaryTests` retry case; external `retry` process case; `BoundaryRetryFailureTests` (partial output, exhausted retry, request preparation, and restoration failure). See adaptation below. |
| Tool-loop interaction | Finalized tool result precedes natural continuation; explicit continuation does not duplicate it. | Awaited handoff inside the existing MAF request path; journal retains canonical results once. | `MutableAgentBoundaryTests.ToolLoopBoundaryUsesFinalizedResultIdsAndFeedsOneNaturalContinuationRequest`; external `tools` process case; existing steering regression tests. |
| `agent_before_settle` | After end/retry/compaction/queued work, may continue and be evaluated again. | Existing prompt pump stays active through end callbacks and pre-settlement; valid continuation precedes final settlement. | `MutableAgentBoundaryTests` pre-settle and queued-end cases; `BoundaryCompactionAndRecoveryTests` (including normal final-output threshold compaction with pre-settle and settled-only registration); external settlement continuation case. |
| Pending messages | Steering/custom/follow-up priority participates in continuation preview. | Uses existing prompt/controller/custom queues; pending snapshots are detached; steering is canonical at handoff and explicit continuation precedes follow-up. | `MutableAgentBoundaryTests`; `AgentLifecycleTests`; `ExtensionCustomMessageContinuationTests`; `NativeExtensionHookBridgeTests`; explicit boundary-before-follow-up and pending-custom pre-settlement regressions in `MutableAgentBoundaryTests`. |
| `agent_settled` | Once at true terminal settlement, with abort-request flag. | Public observer and internal lifecycle item share the same terminal transition; error is distinct from abort. | `MutableAgentBoundaryTests`; `BoundaryCancellationTests`; external process trace counts. |
| Abort | Abort during a completed pre-settlement callback suppresses further work; abort flag survives. | Revokes callbacks and releases ignored tasks; no continuation after cancellation. Completed provider activity is not relabeled because its callback was cancelled. | `BoundaryCancellationTests`; `MutableAgentBoundaryTests` abort case; cancellation-during-save recovery test. See adaptation below. |
| Persistence rollback | Pinned inspected code does not establish atomic save/restore rollback. | Shared canonical mapper rolls back every provisional append; prepared MAF execution is adopted only after successful persistence. | `BoundaryDraftProjectorTests.CommitFailureRollsBackEveryEntryAndDoesNotAdoptExecution`; actual-run save failure and recovery/cancellation tests. |

## PiSharp settlement ownership and API adaptations

The baseline characterization commit precedes production refactoring. At that
baseline, `RunEventsCoreAsync` accepted prompts, started one public agent run,
and asked `AgentRunRetryController` to execute streaming attempts. `PiAgent`
owned MAF invocation and normal tool execution. `ObservedChatClient` exposed
provider attempts. `ProviderTurnJournal` reconciled finalized responses and tool
results. The next `model_request_started` synchronously closed the previous
observational turn; attempt completion closed the final turn. Retry restoration,
compaction, pending runtime changes and prompt delivery retained their existing
owners. The prompt controller previously closed before `agent_end`, leaving its
callback unable to queue active-run work; conversation activity became idle only
afterward, immediately before the internal settled item.

The implementation moves turn closure to an awaited request handoff and awaits
the final closure after the MAF stream. Mutable callbacks run outside runtime
locks. The same prompt pump schedules explicit context-only continuation,
drains queues before pre-settlement, closes ownership atomically, and publishes
public/internal settlement. There is no second loop, journal, pending queue,
session projection or compaction coordinator. The shared mapper and concrete
boundary coordinator are cohesive components beside `ConversationRun`.

PiSharp keeps one `agent_start` for a logical pump, as required by this milestone;
pinned Pi can emit multiple low-level start/end cycles inside one settlement.
`agent_end` can recur when its callback or pre-settlement causes another cycle.
Legacy turn delegates are unchanged and run before mutable handlers. External
process assertions compare their turn indices and tool-result counts, require
finalized assistant IDs, and prove one legacy observer call per existing attempt
(including the failed retry attempt) versus one mutable call per completed logical turn. C# draft
lists are read-only; replacement is expressed through optional result fields,
rather than TypeScript's additional in-place array-mutation surface.

Pi's retry suite shows failed logical activities can expose their own boundary
before a session retry. PiSharp's existing request and agent retry controllers
collapse recoverable provider attempts into the requested successful logical
turn, preserving its index and avoiding duplicated durable extension effects.
A successful retry clears its deferred failed-attempt snapshot, including when the
run is later aborted during pre-settlement. The legacy observer still sees its existing failed attempt snapshots; a mutable
`Error` boundary is dispatched when the retry controller has exhausted recovery.
This is the milestone's explicit logical-retry contract, rather than a claim
that retry event counts are identical to Pi's low-level loop.

PiSharp MEAI messages already represent provider-bound conversation content, so
preview exposes `ContextMessages` without an identical `LlmMessages` alias.
Pinned `_buildBoundaryContext` calls `convertToLlm` before the separate request
transforms installed in `agent-session.ts` (around lines 1000 and 1773–1794).
System/tool request instructions, configured context transforms and model image
filtering remain owned by normal provider preparation and are not executed a
second time while previewing. Entry metadata is a detached Pi-compatible JSON
projection with stable canonical ID/type, never a canonical node or MAF object.

For atomic commit, PiSharp validates/applies drafts and prepares a restored MAF
session before saving, then adopts that execution only after save succeeds.
This avoids persisting a boundary whose required execution restoration cannot
succeed. Both preview and commit use the same role/tool-pair/compaction checks.
A retain-none compaction maps internally to a null retained ID, exported as its
own ID in Pi JSONL; native and Pi resume both project the summary correctly.
Boundary usage uses normal session accounting, and automatic custom compaction
continues to report `FromExtension` through PR #13's coordinator.

Cancellation revokes capabilities immediately and detaches unfinished extension
tasks. Pi can await a pre-settlement callback and commit a result returned after
abort; PiSharp cannot wait indefinitely for such a result while satisfying this
milestone's callback-lifetime requirement. The dispatcher waits for the host adapter to finish capability revocation before
releasing the owning run, while the unfinished extension task stays detached.
A cancelled incomplete proposal is not committed. A synchronously returned valid proposal after requesting abort is
persisted once with a bounded commit token; continuation is suppressed. Already persisted valid drafts survive abort; no further provider
request starts. Normally completed/error runs await settled observers without a
fixed deadline, using caller/consumer cancellation for their callback lifetime.
Cancellation during that observer revokes its context and detaches its unfinished
task without rewriting the already captured `Aborted=false` event. Runs already
aborted before settlement deliver `Aborted=true` using a fresh terminal token
bounded to two seconds; aborted provider turn boundaries also use bounded
terminal dispatch. Legacy `agent_end` retains its existing host-cancellation suppression; the new settled observer still reports the abort. These are deliberate lifetime guarantees, not evidence of
identical unbounded JavaScript callback behavior.

## Integration regressions covered

- PR #14 review correction: `BoundaryCancellationTests.NormalSettledObserverRemainsActiveBeyondTwoSecondsUntilReleased` holds normal settlement pending for 2.5 seconds and verifies its token and context remain active until release. `CancellationDuringSettledObserverRevokesContextAndDetachesWithoutRewritingCompletedOutcome` cancels the caller during normal settlement, checks immediate Session/Capabilities revocation, and requires run cleanup before releasing the unfinished handler while the captured outcome remains false. Existing aborted-run terminal delivery coverage is retained.
- Async iterator resumption does not preserve the caller's gate-owner scope. The awaited turn closure establishes its own scope while the existing session gate is held, so callback session operations remain reentrant without reacquiring that gate. Cancellation tests cover first/later handlers, preview rebuild, persistence, pre-settlement, continuation requests and settled observers.
- Steering is canonical before the provider response. MAF history inserts injected steering at the same position, recognizes canonical custom-message IDs, and reconciliation does not append the same occurrence twice. Existing idle RPC queue and before-start custom-trigger regressions remain intact.
- A successful logical retry clears the failed-attempt boundary snapshot. A later abort during pre-settlement cannot replay that failed attempt or duplicate its durable extension state (`BoundaryRetryFailureTests.AbortAfterSuccessfulRetryDoesNotReplayTheFailedAttemptBoundary`).
- Native boundary/settled registrations run the existing threshold-compaction check after `agent_end`, including when ordinary final assistant output crosses the budget without any draft mutation. Both pre-settlement and settled-only regressions require compaction completion before their observers and one provider request.
- Work queued by completed-compaction callbacks is checked again through the existing prompt controller before pre-settlement. The regression requires exactly two provider requests, one delivery of the queued message, one pre-settlement callback after both requests, and one settled callback.
- Continuation served by queued follow-up input consumes that input on the next request, without an extra context-only request. Steering is delivered on the first MAF continuation request; its history insertion uses the saved request-start position, including when the prior final role is assistant. Tests cover follow-up-only turn/pre-settlement continuation and steering plus follow-up with and without a draft, asserting no host errors and one canonical occurrence of each queued message.
- Legacy host-cancellation suppression for `agent_end` remains covered by the unchanged PR #12 regression. The additive settled observer independently reports the true abort flag at the authoritative terminal transition.

## Source and test index

All source paths below are relative to the Pi repository at the pin above. The inspected files and SHA-256 manifest are archived locally under `artifacts/parity/native-extension-agent-settlement-2026-10-18/pi-source/` (ignored raw evidence).

- `packages/coding-agent/src/core/extensions/types.ts`: public event and draft types, around lines 970–1075.
- `packages/coding-agent/src/core/extensions/runner.ts`: sequential boundary dispatch and recovery, `emitBoundary` around lines 1036–1080.
- `packages/agent/src/agent-loop.ts`: awaited finish hook, `turn_end`, and `agent_end` ordering, around lines 245–320.
- `packages/coding-agent/src/core/agent-session.ts`: turn boundary, projection, commit, run settlement, retry/compaction/queue handling, around lines 840–1035 and 1075–1095, 1110–1205, 1850–2005.
- `packages/coding-agent/src/core/session-manager.ts`: context edit schema/projection and compaction projection, around lines 160–180, 470–565, 1260–1395.
- The requested `packages/coding-agent/src/core/session-tree.ts` does not exist at this pin; the inspected tree/context projection implementation is in `session-manager.ts`.
- `packages/coding-agent/test/suite/agent-session-boundaries.test.ts`: deterministic integration coverage for turn handoff, queues, compaction, invalid continuation, pre-settlement, and abort.
- `packages/coding-agent/test/extensions-runner.test.ts`: handler ordering, field replacement, preview rebuild, invalid-draft repair, and handler-error recovery.
- `packages/coding-agent/test/suite/agent-session-retry-events.test.ts`: event ordering for retry, error, and abort.

## Deterministic acceptance and verification

The external `NativeSettlementExtension` package references only
`PiSharp.Extensions.Abstractions`. Its process theory packs and installs the
`.nupkg`, selects its deterministic local MEAI provider, drives real RPC,
records legacy/mutable ordering and updated previews, requests continuation,
checks provider context and exactly one settlement, exits, then starts a fresh
process against the persisted session. Cases cover normal, retry, tool-loop and
pre-settlement continuation. Package configure rollback, V1/V2 replacement,
removal and collectible ALCs are covered separately through the production
native loader/catalog fixtures.

### Previously reviewed source and local gates

- Previously reviewed product source: `ccad28dbfc98b1e94166a86e024b221a262f1077`; reviewed PR head: `fc2f93e7932e28359d0d851a6db514318b9030d6`.
- Branch: `milestone/native-extension-agent-settlement`.
- Local environment: .NET SDK `10.0.112`, MAF `1.21.0`, Node `24.21.0`, Python `3.14.4`.
- Reviewed compact evidence is this receipt; raw command logs and pinned-source checksums are under `artifacts/parity/native-extension-agent-settlement-2026-10-18/` (ignored).

| Gate | Previously reviewed result |
| --- | --- |
| Restore | Passed, `dotnet restore PiSharp.slnx -p:NuGetAudit=false`. |
| Format | Passed, `dotnet format PiSharp.slnx --verify-no-changes --no-restore`. |
| Build | Passed, `dotnet build PiSharp.slnx --no-restore --warnaserror`; zero warnings/errors. |
| Focused boundaries and compatibility | 74 passed, zero skipped. |
| Final repetition wave | 10 consecutive runs of all 74 focused cases: 740 passed, zero failures/skips. Includes external process acceptance on every repetition. |
| Full .NET | 2,400 passed, zero skipped; `TMPDIR=/tmp dotnet test PiSharp.slnx --no-build --no-restore`. |
| Architecture | 7/7 passed separately and in the full suite. |
| Node terminal | 24/24 passed, `npm test --prefix tools/parity/terminal`. |
| Python terminal | 50/50 passed, `python3 -m unittest discover -s tools/parity/terminal -p 'test_*.py'`. |
| Documentation | 143 documents / 553 references, `python3 tools/validate_doc_paths.py`. |
| MCP conformance | 261 assertions passed; the same eight classified baseline failures; no new regressions. Conformance project built with warnings as errors, then `node tools/parity/mcp-conformance/run.mjs`. |

Local .NET build/test commands also used `--disable-build-servers -m:1` to isolate SDK workers. The full run used a 180-second VSTest hang detector without dumps; it exited normally. The final repetition wave supersedes earlier diagnostic failures and the earlier 67-, 69-, and 70-case waves. Existing tests were retained; no skips were added. `test.pdf` was not modified or staged and is absent in this checkout. Broad extension parity remains `in_progress`.

### PR #14 review correction verification

Correction source: `4fd664e82fefcd41161ee405941cf8bad758b2fe`. Only the
settled observer's cancellation lifetime and its focused regression fixture
changed. Both new assertions failed against the previous implementation:
normal settlement was cancelled by elapsed time, and caller cancellation did
not cancel an active settled callback.

- All 75 boundary/compatibility cases pass, zero skipped, including external package/RPC acceptance.
- All nine `BoundaryCancellationTests` pass in ten consecutive repetitions: 90 passes, zero failures/skips. Existing aborted terminal-delivery tests remain.
- Format verification and the warnings-as-errors solution build pass, with zero warnings/errors. Architecture is 7/7, Node 24/24, Python 50/50, documentation 143 documents / 553 references, and MCP 261 passing assertions with the same eight classified failures.
- The initial full run passed 2,400 cases with one `Address already in use` failure in `GoogleAndVertexRedactKeysFromProviderHttpErrors`. Both cases pass in isolation. The unchanged full-suite rerun and final-head CI outcomes are recorded in the publication annex in the [PR description](https://github.com/PhilipGB/pisharp/pull/14).
- Correction command logs are under `artifacts/parity/native-extension-agent-settlement-2026-10-18/review-settled-lifetime/` (ignored). The earlier verification table remains the reviewed baseline, rather than attributing its 740 repetitions to the correction.

### Publication

- Draft PR: [#14](https://github.com/PhilipGB/pisharp/pull/14), base `main`.
- Verified publication checkpoint: `aef82770c66ead1a7a8c5f59a897a79997acbd77`.
- Checkpoint push CI: [39140767181](https://github.com/PhilipGB/pisharp/actions/runs/39140767181), successful on that exact head.
- Checkpoint PR CI: [39140783492](https://github.com/PhilipGB/pisharp/actions/runs/39140783492), successful on that exact head.
- Reviewed head `fc2f93e7932e28359d0d851a6db514318b9030d6`: push [39150099517](https://github.com/PhilipGB/pisharp/actions/runs/39150099517) and PR [39150103654](https://github.com/PhilipGB/pisharp/actions/runs/39150103654), both successful on that exact head.
- The settlement-lifetime correction and this receipt produce a subsequent head. Its final SHA, full-suite rerun result, and exact-head push/PR run IDs are recorded in the [PR description](https://github.com/PhilipGB/pisharp/pull/14), which can be updated without changing the verified commit. Both final-head runs must succeed before declaring completion.

The PR stays draft and is not merged. These checkpoint runs establish the published pre-correction source; the final-head publication annex in the PR description is part of this receipt and establishes the final source and receipt together. Keeping that annex external avoids a commit-SHA/CI-run self-reference when recording a run for the commit that contains this file.

## Deferred scope

Provider wire/header/response hooks; stream interception; input/user-bash
interception; message start/update or mutable message-end; advanced UI,
renderers, Markdown/editor/raw-terminal customization; tree-summary and remaining
fork overrides; extension-initiated session switching; unrestricted dynamic
registration; provider OAuth/custom protocols; TypeScript extensions; and the
final global parity audit remain deferred.
