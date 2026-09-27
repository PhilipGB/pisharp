# Active RPC clone baseline

Reference: Pi `0.87.1`, source `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`.

Paired Pi and PiSharp CLI processes used the same OpenAI-compatible loopback provider. Each process completed a seed turn, started another request, and received `clone` while the provider was blocked after streaming partial text.

## Active clone

Both processes returned `{ "success": true, "data": { "cancelled": false } }` after settling the active turn in this order:

```text
message_update* -> message_end -> turn_end -> agent_end -> agent_settled -> clone response
```

Both cloned the accepted active user prompt and previous completed history, and excluded the partial aborted assistant message from the clone. The outgoing assistant had `stopReason: "aborted"` and `errorMessage: "Request was aborted"`.

## Preflight failure

While a provider request was blocked, both processes had their source session file removed and then received `clone`. Both returned an error response while `get_state.isStreaming` remained `true`; restoring the source file allowed the original provider turn to complete. Pi's exact error is:

```text
This session has not been saved yet. Wait for the first assistant response before cloning or forking it.
```

PiSharp previously returned “Send a message before cloning or forking it.” `InteractiveSessionController.PrepareCloneAsync` now uses Pi's current wording. `RpcActiveCloneProcessTests` and `RpcClonePreflightProcessTests` exercise the matching PiSharp process behavior.

Current Pi RPC source builds the clone at the current leaf before aborting the active turn in [`rpc-mode.ts`](https://github.com/earendil-works/pi/blob/2b0a123de98318c2ff8069661721ce0c3794c34e/packages/coding-agent/src/modes/rpc/rpc-mode.ts) and [`agent-session-runtime.ts`](https://github.com/earendil-works/pi/blob/2b0a123de98318c2ff8069661721ce0c3794c34e/packages/coding-agent/src/core/agent-session-runtime.ts). This is a scoped process differential; extension veto, no-current-entry, shutdown, and other replacement failures remain open.
