# RPC thinking-level map baseline

Reference: `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`.

The fixture model advertises reasoning with this map:

```json
{"off":null,"high":null,"xhigh":null,"max":"max"}
```

Current Pi and PiSharp report `minimal`, `low`, `medium`, and `max` as available. Setting `xhigh` clamps upward to `max`, emits `thinking_level_changed` before the success response, and reports `max` in `get_state`. Cycling from `max` wraps to `minimal`.

PiSharp coverage is `ThinkingLevelsTests` plus `RpcThinkingLevelMapProcessTests`. The Pi process was run against the same `models.json` fixture at the reference SHA and emitted the same available levels, effective `max`, event/response ordering, and next cycle level.

Canonical map values (`off`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`) are translated to Microsoft.Extensions.AI `ReasoningEffort` values where available. The OpenAI Chat Completions request test verifies `high: "low"` sends `reasoning_effort: "low"`. Arbitrary provider strings and API-specific map payloads remain open.
