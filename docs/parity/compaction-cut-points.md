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

Local `InFlightContextBudgetTests.OversizedActiveToolResultIsSummarizedInsteadOfKeptInTransientRequest` projects the analogous old turn + 8000-character completed read cycle with an explicitly configured 2400-token heuristic request threshold. It summarizes the completed cycle, sends one synthetic summary in the transient provider request and leaves both original call/result messages unchanged in the raw input. This remains a distinct in-flight request projection; it does not append a canonical compaction entry. Persistent split-turn behavior is now compared against current Pi in [`rpc-compact-baseline.md`](fixtures/rpc-compact-baseline.md), including an assistant boundary after the completed tool result. In-flight tool-loop and overflow compaction still need their own current-Pi process comparison.

## Current-main compaction cut points and defaults

Current upstream `main` was verified on 2026-09-28 at `6f7551516b84278eb9da1c340c8e7bc66be1a6ba`. Its defaults are `reserveTokens: 16384` and `keepRecentTokens: 20000` (`packages/coding-agent/src/core/settings-manager.ts`). `findCutPoint` accepts user/assistant boundaries, never tool-result boundaries, and when trailing tool results alone exceed the retention budget falls back to the latest valid boundary before those results. The current upstream regression for issue #9740 returns `{ "firstKeptEntryIndex": 3, "turnStartIndex": 2, "isSplitTurn": true }` for an 8000-character trailing result.

The projected persistent compaction path preserves empty/invisible entry provenance. After the retention budget is exceeded, it can advance across a recovery suffix only when the suffix contains an omitted assistant attempt, has no external replacement, and has no later visible input. The checked-in Pi tests are `packages/coding-agent/test/compaction.test.ts` and `packages/coding-agent/test/suite/agent-session-compaction.test.ts`; both passed together on this checkout (55 passed, 2 skipped). `packages/coding-agent/test/settings-manager-compaction.test.ts` passed 56/56.

PiSharp now applies the same default retention and reserve values to startup, manual compaction, explicit environment windows, and queued model changes. `RecentTokenBudgetFallsBackBeforeOversizedTrailingToolResults`, `CompactionMovesBoundaryPastClosedRecoveryOmissionSuffix`, `RecoveryOmissionFallbackDoesNotAdvanceAcrossVisibleInput`, and `CompactionProjectionDoesNotCountAnInterruptedAssistantTwice` cover those boundaries. `RpcActiveModelSelectionProcessTests.RpcModelSelectionAndCycleDuringBlockedToolTurnChangeTheNextProviderRequest` also switches between model-specific overrides during a blocked tool turn, then verifies manual compaction uses the selected model's one-token retention override. A nonpositive trigger threshold is possible when the fixed reserve is at least the model window; PiSharp avoids rejecting a prompt or creating an impossible transient budget in that case, while still retaining the configured policy for automatic compaction decisions.

A current-main `pi --mode rpc` process used a loopback OpenAI-compatible SSE endpoint with the same old exchange, current user request, completed `read` call/result, and aborted assistant transcript as the PiSharp process regression. Manual `compact` made two summary requests, applied custom focus only to the older-history request, retained the aborted assistant as `firstKeptEntryId`, and returned the expected split-turn summary, file detail, and 30/7/37 usage. It exited with no stderr. Pi reported 25/41 context estimates in this run; the historical paired fixture records different estimates, so exact estimate parity remains open.

Validation for source commit `56f0659d9061c448a1837529804493c2e83d1e3e`: format verification passed; the warnings-as-errors solution build completed with zero warnings/errors; focused compaction/settings/runtime/process coverage passed 91/91; the full local suite passed 692/692 with zero skips. Exact-head Linux CI run [36394452133](https://github.com/PhilipGB/pisharp/actions/runs/36394452133) passed restore, format, warnings-as-errors build, and 692/692 tests (0 skipped). Compaction remains in progress: in-flight multi-cycle and overflow projection, provider-usage parity, and exact token-estimate parity still need separate evidence.
