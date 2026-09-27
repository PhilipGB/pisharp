# Busy RPC model-cycle baseline

Reference: `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`.

With a fake OpenAI-compatible provider and the first `bash` call blocked on a FIFO, current Pi accepts `cycle_model` while the tool is still running. Starting from `fixture/model-one`, the response is successful and contains `model.id: "model-two"`, `thinkingLevel: "off"`, and `isScoped: false`. After releasing the tool, the next provider request uses `model-two`, then `agent_settled` is emitted.

PiSharp's `RpcActiveModelSelectionProcessTests.RpcModelSelectionAndCycleDuringBlockedToolTurnChangeTheNextProviderRequest` exercises both `set_model` and `cycle_model` before releasing the blocked tool, checks each response and persisted model-change order, and verifies the next provider request uses the cycled-to model.

The scoped-model ordering and per-model thinking behavior remain separate open cases.
