# RPC OpenAI tool-call delta baseline

Reference: `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`.

The fixture provider returns an OpenAI Chat Completions SSE tool call named `read` with ID `call-read`. It sends the JSON arguments in two distinct content fragments:

1. `{"path":`
2. `"fixture.txt"}`

Current Pi and PiSharp both project this sequence before executing the tool:

1. `toolcall_start` with `contentIndex: 0`, `id: "call-read"`, and `toolName: "read"`.
2. `toolcall_delta` with `delta: "{\"path\":"`.
3. `toolcall_delta` with `delta: "\"fixture.txt\"}"`.
4. `toolcall_end` with the completed call arguments `{ "path": "fixture.txt" }`.

The provider then receives a second request containing the tool result and returns a final assistant response. `RpcToolCallDeltaProcessTests` blocks the fixture after the first fragment and verifies PiSharp emits that delta before the second fragment is released.

This fixture covers OpenAI Chat Completions only. It does not establish parity for OpenAI Responses, Anthropic, snapshots of incomplete arguments, or other provider APIs.
