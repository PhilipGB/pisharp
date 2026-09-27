# RPC scoped model order and thinking defaults

Reference: `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`.

With `--models fixture/model-three,fixture/model-one`, current Pi preserves that pattern order when cycling. `get_available_models` still returns all three available models, and `set_model` can select out-of-scope `model-two`. While the first provider request for `model-three` is blocked, `set_model` selects `model-two` and emits `thinking_level_changed: minimal` before its response. `cycle_model` then returns to scoped `model-one`, emits `thinking_level_changed: high` before its response, and returns `isScoped: true`.

The user settings fixture sets `defaultThinkingLevel` to `low`, `fixture/model-two` to `minimal`, and `fixture/model-one` to `high`. The first request carries `reasoning_effort: low`; after the cycle, the next request carries `reasoning_effort: high` for `model-one`.

PiSharp's `RpcActiveModelSelectionProcessTests.RpcModelSelectionAndCycleDuringBlockedToolTurnChangeTheNextProviderRequest` verifies the same busy command sequence, all-model listing, out-of-scope selection, event ordering, selected models, thinking levels, scope flag, persisted model-change order and provider requests. `ProviderModelRuntimeTests.ModelScopePreservesPatternOrderAndRemovesDuplicates` covers stable pattern order and deduplication; `UserSettingsTests.ModelThinkingLevelsLoadValidateAndMergeTrustedProjectOverrides` covers settings validation and user/project precedence.

Scoped model patterns with explicit `:thinking` suffixes and the interactive settings editor for per-model values remain open.
