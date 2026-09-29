# Current-Pi `defaultTools` modifiers, 2026-09-29

Reference: current Pi `5257d0d5f3ab7d42550804f32c67a77b49f485d4`, `packages/coding-agent/src/core/settings-manager.ts` and `test/settings-manager.test.ts`. The change arrived in upstream commit `30a1d18` after the prior PiSharp pin.

A setting with plain names replaces the inherited selection. A project setting containing only `+name` and `-name` entries appends to the inherited list. Resolution starts with plain names, or `read,bash,edit,write` when the list contains only modifiers, then applies additions and removals in order. An empty list selects no tools. CLI `--tools` remains explicit precedence over settings, and `--no-tools` suppresses the defaults.

The new PiSharp `UserSettingsTests.DefaultToolModifiersLayerOverBuiltinsGlobalAndProjectSelections` failed first because PiSharp passed modifier strings through as literal tool names. After implementation, focused `UserSettingsTests` passed 44/44, including a file-backed global/trusted-project overlay. The sequential local full suite passed 849/849 with zero skipped; exact-head CI run [36577388693](https://github.com/PhilipGB/pisharp/actions/runs/36577388693) passed 849/849 with zero skipped. This is source-matched local behavior; a paired current-Pi process differential is still open.
