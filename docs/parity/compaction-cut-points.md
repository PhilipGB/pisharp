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

Local `InFlightContextBudgetTests.OversizedActiveToolResultIsSummarizedInsteadOfKeptInTransientRequest` projects the analogous old turn + 8000-character completed read cycle with an explicitly configured 2400-token heuristic request threshold. It summarizes the completed cycle, sends one synthetic summary in the transient provider request and leaves both original call/result messages unchanged in the raw input. This is **not** cut-point parity: Pi's test selects a persistent `keepRecentTokens` boundary; PiSharp's separate transient request budget must shorten an otherwise unsendable result. Token estimators and summary prompts differ. Do not promote compaction to Verified based on this differential.
