# Oversized tool-result cut-point differential (scoped)

Pinned upstream: `earendil-works/pi@d5629e20489ccf770ed90b5a33941cb3b7ef24d0`. Run from that checkout with Node 24 and its installed `tsx` dependency:

```sh
node --import tsx --input-type=module - <<'EOF'
import { findCutPoint } from './packages/coding-agent/src/core/compaction/compaction.ts';
let id = 0;
const entry = message => ({ type: 'message', id: `id-${id++}`, parentId: null,
  timestamp: '2026-09-24T00:00:00.000Z', message });
const usage = { input: 100, output: 50, cacheRead: 0, cacheWrite: 0, totalTokens: 150,
  cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } };
const assistant = content => ({ role: 'assistant', content, usage, stopReason: 'stop',
  timestamp: 1, api: 'anthropic-messages', provider: 'anthropic', model: 'fixture' });
const entries = [
  entry({ role: 'user', content: 'old history', timestamp: 1 }),
  entry(assistant([{ type: 'text', text: 'old answer' }])),
  entry({ role: 'user', content: 'read the large file', timestamp: 1 }),
  entry({ ...assistant([{ type: 'toolCall', id: 'call-1', name: 'read',
    arguments: { path: 'big.txt' } }]), stopReason: 'toolUse' }),
  entry({ role: 'toolResult', toolCallId: 'call-1', toolName: 'read',
    content: [{ type: 'text', text: 'x'.repeat(8000) }], isError: false, timestamp: 1 }),
];
console.log(JSON.stringify(findCutPoint(entries, 0, entries.length, 1000)));
EOF
```

Observed output: `{"firstKeptEntryIndex":3,"turnStartIndex":2,"isSplitTurn":true}`. This also agrees with pinned `packages/coding-agent/test/compaction.test.ts` regression #9740: persistent compaction keeps the tool call and its oversized trailing result together.

Local `InFlightContextBudgetTests.OversizedActiveToolResultIsSummarizedInsteadOfKeptInTransientRequest` projects the analogous old turn + 8000-character completed read cycle with an explicitly configured 2400-token heuristic request threshold. It summarizes the completed cycle, sends one synthetic summary in the transient provider request and leaves both original call/result messages unchanged in the raw input. This remains a distinct in-flight request projection; it does not append a canonical compaction entry. Persistent split-turn behavior is compared against current Pi in [`rpc-compact-baseline.md`](fixtures/rpc-compact-baseline.md), including an assistant boundary after the completed tool result.

## Pinned in-flight tool continuation comparison

The C6 adapter runs against pinned Pi `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` through its actual `AgentSession` harness and provider stream hook. The Pi cases execute single, sequential, parallel and nested tool calls, then capture the continuation request and durable session projection. Pi sends the full 8000-character result and allows the seeded incomplete and duplicate graphs to reach the provider hook. PiSharp's `InFlightContextBudget` leaves raw canonical history unchanged, summarizes the large result, closes sequential/parallel/nested groups before the cut, preserves steering, and rejects malformed transient continuations before their projected send.

At PiSharp source `1ff1d67d85d4ef76995700e0e345632c9c6ec16f`, the C6 checkpoint passes 10/10 cases with zero unresolved mismatches. Four case-scoped safety differences cover bounded large results, summary retention of the latest outcome, and pre-send rejection of incomplete or duplicate graphs. The compact case record is [compaction-context-management-c6-case-results.json](fixtures/compaction-context-management-c6-case-results.json); raw Pi and PiSharp captures and their hashes are under the Git-ignored directory `artifacts/parity/20261028-c6-1ff1d67d/runs/C6/differential/`.

This closes the current-Pi process evidence gap for the C6 tool-continuation scenarios. It does not establish equivalence for the intentional safety differences or complete the full compaction contract; provider-wire overflow cases and the other frozen checkpoints remain open.

## C7 summary generation

The paired C7 runner at PiSharp source
`d274475f25512c9568112985896a17136586a2a6` passes 10/10 summary-generation
cases. It compares the pinned system prompt and serialized transcript, prior
summary updates, focus handling across split turns, tool-call/result content,
file details, retry behavior, usage presence, and routed model/reasoning
options. Pinned Pi accepts a whitespace-only first summary and appends a
compaction; PiSharp rejects the empty result and retains canonical history.
This is recorded as an intentional safety difference in the [C7 case
record](fixtures/compaction-context-management-c7-case-results.json). Raw
captures are under `artifacts/parity/20261028-c7-d274475f/runs/C7/differential/`.

## Current-main compaction cut points and defaults

Current upstream `main` was verified on 2026-09-28 at `6f7551516b84278eb9da1c340c8e7bc66be1a6ba`. Its defaults are `reserveTokens: 16384` and `keepRecentTokens: 20000` (`packages/coding-agent/src/core/settings-manager.ts`). `findCutPoint` accepts user/assistant boundaries, never tool-result boundaries, and when trailing tool results alone exceed the retention budget falls back to the latest valid boundary before those results. The current upstream regression for issue #9740 returns `{ "firstKeptEntryIndex": 3, "turnStartIndex": 2, "isSplitTurn": true }` for an 8000-character trailing result.

The projected persistent compaction path preserves empty/invisible entry provenance. After the retention budget is exceeded, it can advance across a recovery suffix only when the suffix contains an omitted assistant attempt, has no external replacement, and has no later visible input. The checked-in Pi tests are `packages/coding-agent/test/compaction.test.ts` and `packages/coding-agent/test/suite/agent-session-compaction.test.ts`; both passed together on this checkout (55 passed, 2 skipped). `packages/coding-agent/test/settings-manager-compaction.test.ts` passed 56/56.

PiSharp now applies the same default retention and reserve values to startup, manual compaction, explicit environment windows, and queued model changes. `RecentTokenBudgetFallsBackBeforeOversizedTrailingToolResults`, `CompactionMovesBoundaryPastClosedRecoveryOmissionSuffix`, `RecoveryOmissionFallbackDoesNotAdvanceAcrossVisibleInput`, and `CompactionProjectionDoesNotCountAnInterruptedAssistantTwice` cover those boundaries. `RpcActiveModelSelectionProcessTests.RpcModelSelectionAndCycleDuringBlockedToolTurnChangeTheNextProviderRequest` also switches between model-specific overrides during a blocked tool turn, then verifies manual compaction uses the selected model's one-token retention override. A nonpositive trigger threshold is possible when the fixed reserve is at least the model window; PiSharp avoids rejecting a prompt or creating an impossible transient budget in that case, while still retaining the configured policy for automatic compaction decisions.

A current-main `pi --mode rpc` process used a loopback OpenAI-compatible SSE endpoint with the same old exchange, current user request, completed `read` call/result, and aborted assistant transcript as the PiSharp process regression. Manual `compact` made two summary requests, applied custom focus only to the older-history request, retained the aborted assistant as `firstKeptEntryId`, and returned the expected split-turn summary, file detail, and 30/7/37 usage. It exited with no stderr. Pi reported 25/41 context estimates in this run; the historical paired fixture records different estimates, so exact estimate parity remains open.

Validation for source commit `56f0659d9061c448a1837529804493c2e83d1e3e`: format verification passed; the warnings-as-errors solution build completed with zero warnings/errors; focused compaction/settings/runtime/process coverage passed 91/91; the full local suite passed 692/692 with zero skips. Exact-head Linux CI run [36394452133](https://github.com/PhilipGB/pisharp/actions/runs/36394452133) passed restore, format, warnings-as-errors build, and 692/692 tests (0 skipped). Compaction remains in progress: in-flight multi-cycle and overflow projection, provider-usage parity, and exact token-estimate parity still need separate evidence.
