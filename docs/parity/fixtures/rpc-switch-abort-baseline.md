# Active RPC session-switch abort baseline

Reference: Pi `0.87.1`, source `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`.

A paired CLI-process comparison held an OpenAI-compatible provider response after it had streamed partial assistant text, then sent `switch_session` for a missing `.jsonl` target. Pi and PiSharp both settled the outgoing run before the correlated switch response, with this event order:

```text
message_update* -> message_end -> turn_end -> agent_end -> agent_settled -> switch_session response
```

Pi's aborted assistant message carries `stopReason: "aborted"` and `errorMessage: "Request was aborted"`. The `message_end` message, `turn_end.message`, and assistant element in `agent_end.messages` are identical. Before this slice, PiSharp preserved the event order and partial text but omitted `errorMessage` from the assistant snapshots.

PiSharp now applies that fallback in both the provider-message and canonical session-history projections. `RpcMissingSessionTargetProcessTests.MissingTargetCreatesLazyPiJsonlSessionAndSettlesCurrentRunBeforeResponse` compares all three snapshots and their ordering. `PiJsonlSessionInterchangeTests.InterruptedRunProjectionUsesThePiAbortErrorForExistingAndSynthesizedAssistantMessages` covers both a checkpointed partial assistant and a synthesized aborted assistant.

The Pi provider adapter surfaces `Request was aborted` for an aborted request in [`openai-completions.ts`](https://github.com/earendil-works/pi/blob/2b0a123de98318c2ff8069661721ce0c3794c34e/packages/ai/src/api/openai-completions.ts). Current upstream was checked at the pinned SHA above. This is a scoped differential, not full `switch_session` parity; clone/session-replacement differentials and wider failure, cancellation, persistence, and shutdown cases remain open.
