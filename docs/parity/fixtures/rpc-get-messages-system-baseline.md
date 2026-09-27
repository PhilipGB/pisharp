# RPC `get_messages` generated system message differential

Reference: Pi `0.87.1`, source `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`.

The active-clone process comparison also queried `get_messages` after replacement. Pi's RPC returned a leading generated `system` message in `data.messages` (with empty `content`, `sections`, `cwd`, `timestamp`, and tool metadata). PiSharp returned only the persisted conversation messages. Pi's `get_state.messageCount` included that system message; PiSharp's did not.

This is an observable RPC parity gap. Pi's `get_messages` handler returns `session.messages` directly in [`rpc-mode.ts`](https://github.com/earendil-works/pi/blob/2b0a123de98318c2ff8069661721ce0c3794c34e/packages/coding-agent/src/modes/rpc/rpc-mode.ts). PiSharp's `RpcSessionMessageProjector` currently projects canonical session history and the active assistant, while MAF receives the generated instructions separately. The generated message is not persisted in Pi JSONL. Build a Pi-shaped RPC projection at the compatibility boundary without changing canonical C# history or the provider instruction source.

The previous note that Pi's generated prompt is separate from `session.messages` conflated persisted session history with the live RPC property. The process result and current handler source establish that `get_messages` includes it.
