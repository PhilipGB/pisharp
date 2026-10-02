# Deferred MCP tool restoration

Oracle: current Pi `0495646a8322ff99ce40ac2f9e15f1f49f56bb11`, including upstream change `c662ec7e374563bd549dc35f47bac52dcc4bed88`.

Restored tool names stay pending until registration. A selection that removes an active tool drops all pending names; additive selections retain them. Starting the next prompt drops tools that have not registered. Reload preserves the previous selection while extensions and MCP reconnect. Tool restrictions still apply. Provider declarations retain transcript order when tools are added.

Four original PiSharp scenarios failed before implementation. The paired oracle subsequently exposed a separate declaration-order failure, captured by `AdditiveSelectionPreservesTranscriptDeclarationOrder`. The final focused tool/session/search suite passes 34/34, zero skips. Current Pi's five MCP resume/reload scenarios pass; 39 unrelated cases were deselected by the explicit test filter.

The paired probe covers resume, additive selection, replacement, prompt expiry, cancellation, excluded tools, allowed tools and reload. It compares ordered active names before and after delayed registration, ordered provider declarations and persisted loadout names. All eight match. No sorting or layout normalization is applied. Unrelated messages, schemas, timestamps and identifiers are outside this fixture.

Pi's real `AgentSession`, extension registration, transcript replay and reload run with its deterministic faux provider. Resume restores the agent history as the Pi SDK does; reload re-registers the faux API after Pi resets provider registrations. PiSharp runs `ConversationRun` and the shared MAF pipeline. The probe accesses the existing execution session to make the same explicit loadout selections; it does not copy the pending-tool implementation. Existing MCP integration and the current-Pi transport tests cover the registration boundary separately.

`ToolLoadout` now owns mutable selection state; `ConversationToolState` extracts branch-local restoration and persistence from `ConversationRun`. MAF still owns sessions and function execution. These additions implement Pi-specific exposure/transcript semantics rather than a second agent loop.

Reproduce from the repository root:

```sh
python3 tools/parity/deferred-tool-differential.py --pi /path/to/current/pi --output /tmp/deferred-tools.json
```

The Pi checkout must have dependencies and generated provider metadata hydrated. Exact comparison data is in [the JSON fixture](deferred-tool-restore-2026-10-02.json).

Final format verification passes. The warnings-as-errors build has zero warnings/errors. The serial full suite passes 1087/1087 with zero skips in 4m32s. Publication and exact-head CI are pending; their exact source/run IDs will accompany the next implementation checkpoint.

Broader MCP, exposure and visual parity remain open; this fixture verifies the restored deferred-loadout slice only.
