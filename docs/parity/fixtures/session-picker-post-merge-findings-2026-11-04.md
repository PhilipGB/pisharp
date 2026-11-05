# Session-picker post-merge findings — pinned Pi receipt

Pinned Pi: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`

Baseline: `a4b51240c0426508ac4fe5c5861b7e914dd7307f` (PR #40 merge)

Capability: `session-picker-post-merge-findings`

Tracking: [PR #41](https://github.com/PhilipGB/pisharp/pull/41)

## Contract

Exactly three findings are required. No other capability is marked verified here.

- **F01 — path identity.** Picker selection, rename and deletion use the canonical
  session-file path. Persisted session IDs remain unchanged. Same-ID sessions in one
  project or across all-project scope keep distinct rows; deleting or renaming one
  leaves its sibling untouched. Current-session protection, fingerprint validation
  and trash-first deletion remain in force.
- **F02 — Threaded tree.** Threaded mode without a search query mirrors pinned Pi's
  `buildSessionTree`/`flattenSessionTree`: canonical parent links, roots and children
  sorted by latest subtree activity (stable ties), missing parents treated as roots,
  and pre-order flattening with `│  `, `├─ ` and `└─ ` prefixes. Recent and Fuzzy stay
  flat; Threaded with a query falls back to score ordering. Cross-project parents link
  in all scope. With the Named filter, the tree is built from the filtered set, so a
  named child whose unnamed parent is filtered out becomes a root rather than being
  dropped. Selection is preserved by path when ordering changes.
- **F03 — disabled confirmation keys.** A configured-but-disabled confirm/cancel
  binding renders as an empty key, exactly like pinned Pi's `keyHint`. Disabled keys
  cannot confirm or cancel. Custom bindings are still advertised and dispatched.
  Enter/Escape fallback is used only when the caller has no keymap at all. Shared list
  footers omit disabled key/action pairs instead of leaving blank slots.

## Pinned-Pi anchors

`docs/parity/fixtures/session-picker-post-merge-oracle-2026-11-04.json` records exact
anchors in:

- `packages/coding-agent/src/modes/interactive/components/session-selector.ts`
- `packages/coding-agent/src/core/session-manager.ts`
- `packages/coding-agent/src/modes/interactive/components/keybinding-hints.ts`
- `packages/coding-agent/src/modes/interactive/components/session-selector-search.ts`

The baseline reproduction is executable with
`python3 tools/parity/session_picker_findings_baseline.py --baseline <a4b51240-worktree>`.
All three reproductions failed before the fix: duplicate IDs collapsed to one picker
key, Threaded rendered the child before its parent, and a disabled confirmation
binding still advertised `Enter`.

## Implementation

- `SessionListing.CanonicalPath` and `SessionCatalog.CanonicalPath`/`PathsEqual`
  provide the picker identity and path comparison. `SessionPickerEntry.Key`,
  initial selection, `FindEntry`, deletion confirmation, `Remove` and rename all use
  that key; `SessionListing.Id` remains the persisted identity.
- `SessionCatalog` exposes `ParentSessionPath`; `SessionPickerTree` is the pinned-Pi
  tree builder/flattener used only by Threaded mode. It receives the already filtered
  entry set, matching Pi's `nameFiltered` input, so Named mode cannot orphan or drop a
  named child. `SessionPickerEntry` carries depth and prefix for rendering while the
  existing list owns filtering, search and selection restoration.
- `TerminalSessionPicker.BuildActionHint` distinguishes a null display-key delegate
  (no keymap; defaults allowed) from a configured delegate returning null (disabled;
  render empty). Confirmation dispatch still uses the live keymap.

## Evidence

- Acceptance manifest:
  [session-picker-post-merge-acceptance-2026-11-04.json](session-picker-post-merge-acceptance-2026-11-04.json).
- Baseline reproduction: 3 failed / 0 passed at `a4b51240`.
- Deterministic regressions: canonical path keys in current/all scope; parent-cycle
  back-edge breaking; disabled footer slots omitted; same-ID
  rename/delete isolation; pinned tree order, prefixes and missing-parent handling;
  Named-filtered Threaded roots; Threaded-with-search flattening and modified-time
  tie-breaks; Recent/Fuzzy flat order; path-based selection after sorting;
  cross-project parent links; disabled/custom confirmation hints and dispatch.
- Real Linux PTY: same-ID session deletion, Threaded parent/child rendering, a
  nonempty Threaded search that flattens to relevance order without prefixes, and a
  disabled confirm binding that ignores Enter while Escape still cancels.
- Paired PTY differential:
  - `terminal-session-picker-findings` at 140x40 fullscreen. Five rows in the expected
    tree order with `kin child` prefixed `└─`; searching `kin` flattens the list to
    `kin child` then `kin parent` with no prefixes and selects the newer child;
    clearing restores the tree; deleting `duplicate two` leaves `duplicate one` and
    the tree intact; final session files match on both products.
  - `terminal-session-picker-disabled-confirm` at 140x40 regular. Both products render
    `Delete ...?  confirm · Escape/Ctrl+C cancel`; Enter leaves confirmation open;
    Escape cancels without deleting.
  - Zero semantic paired differences and zero same-Pi semantic calibration differences
    in both cases.
- Terminal restoration: quit checkpoints are on the normal screen with no temporary
  private modes and a visible cursor for both products.
- Negative controls: the verifier mutates captured evidence and rejects missing,
  duplicate, failed or skipped findings; wrong Pi/baseline SHA; unresolved
  comparisons; missing oracle/source/test/checkpoint evidence; missing tree prefix,
  wrong tree order or search order/prefix retention; wrong same-ID deletion;
  disabled-key advertising or dispatch;
  failed calibration; session-file identity drift; missing paired PTY evidence; and
  failed/skipped full-suite results.

## Remaining non-required differences

- Full render streams differ in paint timing and PiSharp overlay chrome; named semantic
  checkpoints match.
- PiSharp keeps detailed row descriptions; pinned Pi renders compact rows.
- When the active session is inside the catalogue, PiSharp preselects it while pinned Pi
  starts at the first row. The paired fixture keeps the active session outside the
  scanned directory so F01-F03 are isolated.
- Pinned Pi's confirmation header is generic (`Delete session?`); PiSharp includes the
  selected session name. Confirmation keys and dispatch are compared.
- Pinned Pi auto-hides picker status after a timeout; PiSharp keeps it until the next
  action.
