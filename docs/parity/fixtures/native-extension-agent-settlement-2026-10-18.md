# Native extension agent settlement — initial contract receipt — 2026-10-18

This is a pinned-source contract and an initial comparison matrix. It is not an implementation verification or a parity claim. PiSharp behavior remains **pending** until the milestone implementation is inspected and supported by deterministic tests.

## Pins and integration checkpoint

- Pi reference: `earendil-works/pi@b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (`/tmp/pi-events-source`).
- Actual PR #13 merge commit and PiSharp milestone base on `main`: `ca47d554bd48887147100ab11195c8086361963d` (parents `eef6f3f83247758d397b4e313d2c26b0088ee9ac` and `1172dc8ce270ba5a16ae9cdc6a52afed57165805`).
- PR #13 head: `1172dc8ce270ba5a16ae9cdc6a52afed57165805`.
- Required exact-head CI runs `39126827078` (push) and `39126829634` (PR) were verified green before merge by the milestone coordinator.
- This receipt is being prepared on the clean milestone base; it does not attest to implementation or later CI.

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

## Initial contract matrix

| Surface | Pinned Pi behavior | PiSharp behavior | Evidence |
| --- | --- | --- | --- |
| `turn_end` entries | Fires after message/tool-result persistence; supplies persisted entry IDs and outcome. Drafts commit before the next request. | Pending; no implementation equivalence claimed. | Pi `agent-loop.ts`, `agent-session.ts`, `extensions/types.ts`; boundary suite synthetic failure and compaction handoff tests. |
| `turn_end` continue | Effective boolean is replaced by later returned values; explicit continuation needs runnable context. Natural tool continuation remains independent. | Pending. | Pi `extensions/runner.ts`, `agent-session.ts`; boundary suite invalid continuation/tool continuation test. |
| Preview rebuild | Later handlers see earlier effective drafts and rebuilt projected context, LLM messages, pending messages, and `canContinue`. | Pending. | Pi `extensions/runner.ts`, `agent-session.ts`; `extensions-runner.test.ts` boundary chaining. |
| Invalid drafts | Handler errors are reported and dispatch proceeds; later handlers can repair an invalid draft. Unrepaired invalid entries/continuation are rejected. | Pending. | Pi `extensions/runner.ts`; `extensions-runner.test.ts` invalid preview repair test; boundary suite invalid continuation test. |
| Retry interaction | Retry decision follows `turn_end` and `agent_end`; each failed turn can dispatch its own boundary. Pre-settlement waits until retry chain ends. | Pending. | Pi `agent-session.ts`, `_handlePostAgentRun`; `agent-session-retry-events.test.ts`. |
| Tool-loop interaction | Tool results are persisted before `turn_end`; a tool call naturally continues. An invalid explicit continuation does not cancel that natural path. | Pending. | Pi `agent-loop.ts`; boundary suite invalid explicit continuation test. |
| `agent_before_settle` | After `agent_end`, retries/compaction/queued loop work; may persist drafts and request one context-only continuation before settlement. | Pending. | Pi `agent-session.ts`, `_handlePostAgentRun`, `_runBeforeSettleBoundary`; boundary suite pre-settlement tests. |
| Pending messages | Preview separates pending messages from current context; custom pending messages are flushed at the boundary; queued follow-up remains behind an explicit continuation. | Pending. | Pi `agent-session.ts`, `_buildBoundaryContext`, `_runBeforeSettleBoundary`; boundary suite pending custom/follow-up tests. |
| `agent_settled` | Once after true active-run settlement, after requested continuation/retry/compaction; deferred new runs wait until all settled handlers finish. | Pending. | Pi `agent-session.ts`, `_emitAgentSettled`; boundary suite settled handler ordering test. |
| Abort | Aborting during pre-settlement commits returned drafts but suppresses continuation; `agent_settled.aborted` reflects the abort request. | Pending. | Pi `agent-session.ts`; boundary suite abort-during-hook test. |
| Persistence rollback | These pinned sources/tests do not establish an atomic rollback contract for boundary commit/save or agent-context restoration failures. | Pending; must be tested before claiming. | No direct pinned proof identified in the inspected boundary implementation/tests. |

## Source and test index

All paths below are relative to pinned `/tmp/pi-events-source` at the hash above.

- `packages/coding-agent/src/core/extensions/types.ts`: public event and draft types, around lines 970–1075.
- `packages/coding-agent/src/core/extensions/runner.ts`: sequential boundary dispatch and recovery, `emitBoundary` around lines 1036–1080.
- `packages/agent/src/agent-loop.ts`: awaited finish hook, `turn_end`, and `agent_end` ordering, around lines 245–320.
- `packages/coding-agent/src/core/agent-session.ts`: turn boundary, projection, commit, run settlement, retry/compaction/queue handling, around lines 840–1035 and 1075–1095, 1110–1205, 1850–2005.
- `packages/coding-agent/src/core/session-manager.ts`: context edit schema/projection and compaction projection, around lines 160–180, 470–565, 1260–1395.
- The requested `packages/coding-agent/src/core/session-tree.ts` does not exist at this pin; the inspected tree/context projection implementation is in `session-manager.ts`.
- `packages/coding-agent/test/suite/agent-session-boundaries.test.ts`: deterministic integration coverage for turn handoff, queues, compaction, invalid continuation, pre-settlement, and abort.
- `packages/coding-agent/test/extensions-runner.test.ts`: handler ordering, field replacement, preview rebuild, invalid-draft repair, and handler-error recovery.
- `packages/coding-agent/test/suite/agent-session-retry-events.test.ts`: event ordering for retry, error, and abort.

## PiSharp status and evidence still required

This receipt does not verify any PiSharp implementation behavior. Fill the PiSharp column only after inspecting the milestone code and recording deterministic evidence for each row. At minimum, the implementation audit must cover exact event order, draft replacement and context preview, context-edit replacement schema, `canContinue`, invalid handler recovery, retries, compaction, tool continuation, queued user/custom messages, abort during callbacks, exact-once settlement, and rollback/consistency failures. A matching event name alone is not evidence of matching semantics.
