# Provider capacity retry

Oracle: freshly fetched Pi `3874b3e98983c70fa05fa193b675d42cfcb8b9f8`. This commit adds “model is at capacity” to transient assistant-error classification used by the normal coding-agent session.

PiSharp previously ended the turn for a status-free `Selected model is at capacity` failure. Two classifier cases and the shared-agent recovery case failed first; disabled/budget/terminal-account cases already passed. The fix adds only the phrase to `AgentRunRetryPolicy`. Microsoft Agent Framework retains session and function execution; the existing Pi retry controller retains budget, context recovery, backoff and cancellation.

`CapacityRetryTests`, `AgentRunRetryPolicyTests` and `ProviderRetryChatClientTests` pass 17/17, zero skips. Current Pi `packages/ai/test/retry.test.ts` passes 25/25, zero skips.

Six paired scenarios execute real Pi `AgentSession` and PiSharp `ConversationRun`: recovery, exhausted retries, disabled retries, zero retry budget, billing exclusion and cancellation during backoff. They exactly match request counts, ordered retry event payloads, user/assistant role-text request contexts, completion and cleared retry state. Failed attempts do not reappear in retry request context. Delay values are deterministic (zero, or a cancelled 100ms backoff).

The fixture maps equivalent public retry fields to the same names. It excludes system prompt content and unrelated lifecycle/provider metadata, whose parity remains separately tracked. It does not claim the full request body or complete RPC event sequence is verified.

Reproduce from the repository root:

```sh
python3 tools/parity/capacity-retry-differential.py --pi /path/to/current/pi --output /tmp/capacity-retry.json
```

The Pi checkout needs dependencies and generated provider metadata hydrated. [The JSON fixture](provider-capacity-retry-2026-10-02.json) preserves inputs, both matching results and the upstream SHA.

Final format verification passes. The warnings-as-errors build has zero warnings/errors. The serial full suite passes 1098/1098 with zero skips in 3m46s. Exact-head Linux CI 36994405262 passes on source 095c893a15f5fcdbea356d6c1cfbc57c8c4c5329, including all 1098 tests with zero skips. Other retry-classifier cases, including current ChatGPT subscription availability/usage-limit wording, remain open in the ledger.
