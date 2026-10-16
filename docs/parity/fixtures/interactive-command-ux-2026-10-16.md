# Interactive Command UX consistency correction — 2026-10-16

This compact receipt covers the bounded settings, completion, command discovery,
and thinking-selector correction on `milestone/interactive-command-ux`. It does
not promote a global parity capability or claim full TUI parity. The global Pi
reference remains `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

## Source and order

Pi's settings selector and thinking selector were inspected at
[`698a2d23b86a5045dd54554354a5e0cb7e466795`](https://github.com/earendil-works/pi/tree/698a2d23b86a5045dd54554354a5e0cb7e466795):
[settings selector](https://github.com/earendil-works/pi/blob/698a2d23b86a5045dd54554354a5e0cb7e466795/packages/coding-agent/src/modes/interactive/components/settings-selector.ts)
and
[thinking selector](https://github.com/earendil-works/pi/blob/698a2d23b86a5045dd54554354a5e0cb7e466795/packages/coding-agent/src/modes/interactive/components/thinking-selector.ts).
The task's explicit first-ten order is the acceptance contract for this
correction and is protected by `SettingsKeepPiSharedOrderAndAppendPiSharpOptions`:

1. Auto-compact
2. Auto-resize images
3. Block images
4. Skill commands
5. Show hardware cursor
6. Editor padding
7. Output padding
8. Autocomplete max items
9. Clear on shrink
10. Terminal progress

This requested initial order differs from the inspected upstream selector,
which currently places Show images and Image width before Auto-resize images.
The bounded user-observed order above was retained as authoritative; no global
upstream pin was advanced.

## Settings classification

| Classification | Settings / behavior |
| --- | --- |
| **SUPPORTED** | Auto-compact, Block images, Skill commands, steering and follow-up modes, session sharing, HTTP idle timeout, Hide thinking, quiet startup, default project trust, theme, and model-specific thinking defaults have corresponding PiSharp behavior and remain available. |
| **SMALL-UX-GAP** | Auto-resize images, Show hardware cursor, Editor padding, Output padding, Autocomplete max items, Clear on shrink, and Terminal progress are exposed in the compact list and persisted through their existing settings/runtime paths. |
| **OUT-OF-SCOPE** | Show images and Image width lack matching persisted interactive controls; transport selection, cache warming, Mermaid rendering, cache notices, changelog collapse, install telemetry, double-escape actions, tree-filter mode, warning controls, and Pi-specific TUI/fullscreen controls require separate capabilities or subsystems. |

PiSharp-only settings follow the shared rows. Mutable rows use the existing
user/project scope behavior; the settings PTY checks filtering, value changes,
screen resize at 80 and 52 columns, persistence, and reload.

## Interaction evidence

- The settings picker is a compact in-flow list with a bounded row window,
  visible position/count, aligned values, selected-row description, filtering,
  Enter/Space changes, and Escape close.
- Editor completion draws from the canonical built-in command catalog, merges
  dynamic skill/prompt/extension commands, deduplicates, and cycles in-buffer
  with Tab / Shift+Tab. The PTY completes `/mod` through `/model` and `/models`,
  cycles backward, then completes `/thi` to `/thinking` and invokes it with one
  Enter. Path completion remains covered by a PTY.
- Bare `/thinking` opens a selector with active-model-supported levels and Pi
  descriptions. PTY evidence covers Enter, Escape, Ctrl+C, model-specific
  Ctrl+S persistence/restart, invalid direct input, and a Shift+Tab update used
  by the next provider request.
- The canonical command regression test asserts every registered built-in,
  explicitly including `/thinking`, `/login`, `/logout`, `/copy`,
  `/scoped-models`, `/settings`, `/model`, `/models`, and `/mcp`, plus dynamic
  command merge and duplicate removal.

Focused process evidence is in `TerminalPtyTests`; settings ordering and input
behavior are in `TerminalSettingsPickerTests`; catalog coverage is in
`EditorCompletionTests`. No execution-ledger or feature-matrix capability
status changed because this receipt records a bounded interaction correction,
not a promoted global capability.
