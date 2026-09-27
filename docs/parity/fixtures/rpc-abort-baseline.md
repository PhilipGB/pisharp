# RPC abort wire baseline

Reference: `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`.

With an OpenAI-compatible provider that emits partial assistant text and then stalls, Pi and PiSharp produce the same abort sequence: partial `message_update`, aborted assistant `message_end`, `turn_end`, `agent_end` with `willRetry: false`, `agent_settled`, then the correlated abort response.

The successful Pi response has `id`, `type`, `command`, and `success`; it omits both `data` and `error`. The PiSharp process test found its shared response helper emitted `error: null`; successful responses now omit that property.

PiSharp coverage is `RpcAgentLifecycleProcessTests.RpcProcessAbortEndsPartialTurnBeforeAgentSettlement`. The fixture uses the same blocked-provider sequence and checks the partial content, snapshots, event order and response shape.

The direct current-Pi process comparison also confirms that a request without an `id` receives a response without `id` or `error`. PiSharp's abort process test sends a no-ID `get_state` request and verifies the same response shape.
