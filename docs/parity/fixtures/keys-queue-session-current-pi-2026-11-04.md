# keys-queue-session — pinned Pi parity receipt

Pinned Pi: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`

Capability: `keys-queue-session`

## Contract

- `Alt+Enter` queues a follow-up while a run streams; while idle it submits once like Enter.
- `Enter` during a run steers; steering drains before follow-ups in FIFO order.
- `Alt+Up` clears both queues and restores steering, then follow-ups, then the existing draft, separated by blank lines.
- `Escape` restores pending input before aborting; completion boundaries never duplicate or drop input.
- `Ctrl+S` cycles `Threaded → Recent → Fuzzy`; `Ctrl+N` filters non-empty names; `Ctrl+P` toggles paths; `Ctrl+R` renames; `Ctrl+D` confirms deletion; `Ctrl+Backspace` confirms only with an empty query. Threaded ordering is a flat activity list in this capability; parent-session tree grouping remains in `keys-tree-scoped-picker`.
- `Tab` switches current-project and all-project scopes; current-session rows are marked; cancellation leaves the session untouched.
- Deletion validates the catalog snapshot, tries `trash` first and falls back to unlink; the active session is not deletable. Rename preserves session id, tree and history.
- All queue and picker bindings are configurable, disableable, and owned by the picker while it is open; Windows/WSL uses Ctrl+Q / Alt+Q for follow-up / dequeue.

## Pinned-Pi sources

`docs/parity/fixtures/keys-queue-session-pi-oracle-2026-11-04.json` records exact anchors in:

- `packages/coding-agent/src/modes/interactive/interactive-mode.ts`
- `packages/coding-agent/src/modes/interactive/components/session-selector.ts`
- `packages/coding-agent/src/modes/interactive/components/session-selector-search.ts`
- `packages/coding-agent/src/core/agent-session.ts`
- `packages/coding-agent/src/core/keybindings.ts`
- `packages/coding-agent/docs/keybindings.md`

and 16 deterministic Q01–Q16 scenarios replayed by
`KeysQueueSessionParityTests.MatchesPinnedPi`.

## Implementation

- `EditorKeymap` adds `app.session.togglePath`, `toggleNamedFilter`, `delete`, and `deleteNoninvasive`, keeps platform defaults, and treats picker-local actions as context-owned rather than global extension conflicts.
- `TerminalEditor` routes idle Alt+Enter to normal submission, idle Alt+Up through the active-run queue drain, and active Alt+Up/Escape through ordered restoration with pinned status text.
- `ConversationRun` / `PromptDeliveryQueue` continue to own steering and follow-up FIFO delivery, cancellation, compaction handoff and extension-command ordering under Microsoft Agent Framework cancellation.
- `TerminalSessionPickerState` separates picker scope, sorting, named filtering, path visibility, selection, rename and validated deletion from rendering/input dispatch.
- `SessionCatalog.ListAllAsync` discovers isolated current/all-project sessions with source stores; sorting uses last message activity like pinned Pi, so rename does not reorder sessions.
- `SessionFileDeletion` validates the unchanged catalog entry, tries `trash`, then falls back to fingerprint-checked unlink for both native `.session.json` and Pi-native `.jsonl` sessions.
- `TerminalSelectionList` adds recent-order matching, index preservation, action hints and empty messages without changing existing model-picker behavior.

## Evidence

- Oracle: 16 scenarios, all source anchors present in the pinned checkout.
- Deterministic: 17 theory/fact tests in `KeysQueueSessionParityTests`, 16 facts in `KeysQueueSessionTests`, two Linux PTY facts in `KeysQueueSessionPtyTests`, plus existing steering/extension/queue and session-discovery PTY tests.
- Paired PTY: `terminal-keys-queue-session` at 100x30 and 140x40, fullscreen and regular. Four cases, 220 named checkpoints, zero semantic paired differences and zero same-Pi semantic calibration differences. Provider user requests match exactly:
  `prompt one`, `prompt two`, `prompt three`, `prompt four`, `first prompt`, and the restored `steer one\n\nfollow one\n\ndraft text`.
- Session evidence: both products finish with three session files after deleting the beta fixture session; reopening the picker shows `alpha renamed` and no `beta` row.
- Terminal restoration: quit checkpoints are on the normal screen with no temporary private modes and a visible cursor.
- Negative controls: the verifier mutates captured observations through the production validators and rejects missing/duplicate/failed/skipped cases, stale/Pi SHA drift, missing oracle/source/test/checkpoint evidence, wrong steering/follow-up order, lost messages, wrong restored draft, unexpected or reordered provider requests, wrong scope/sort/filter/path state, shortcut context leaks, missing rename persistence, deletion without confirmation or of the wrong session, wrong Ctrl+Backspace query behaviour, missing PTY cases, failed calibration, and unrestored terminal modes.

## Remaining non-required differences

- Full render streams differ in paint/repaint timing, spinner glyphs and transient frames; named semantic checkpoints and provider requests match.
- Fuzzy queries that match the shared working-directory path can order non-selected rows differently; selection validity and filter composition are covered deterministically.
- Pinned Pi resets/clamps picker selection after rename/delete; PiSharp resets to the first row in the PTY path and preserves the selected key on sort/filter toggles.
- Threaded tree grouping by `parentSessionPath` and streamed all-project loading progress remain in `keys-tree-scoped-picker`/later picker work; the flat scope/sort/filter contract is verified here.
- Pinned Pi auto-hides picker status messages after a timeout; PiSharp keeps them until the next action.
- Windows/WSL Ctrl+Q / Alt+Q defaults are verified by deterministic keymap replay; the PTY differential runs on Linux.
- Generated system/developer prompt text remains pinned-Pi-owned; request facts compare user/assistant/tool turns exactly.
