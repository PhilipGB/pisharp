# Keys: session tree and scoped model picker

Verified capability `keys-tree-scoped-picker` at implementation source
`329bcb0ca5323ad0aa1261dc3828e409293c8726` against pinned Pi
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

## Contract

The frozen [pinned-Pi oracle](keys-tree-scoped-picker-pi-oracle-2026-11-05.json)
defines T01-T08 for the `/tree` navigator and S01-S07 for the scoped-model
selector, with source anchors from `tree-selector.ts`, `scoped-models-selector.ts`,
`interactive-mode.ts`, `session-manager.ts`, `core/keybindings.ts` and
`docs/keybindings.md`.

All 15 scenarios are mapped to executable evidence in the
[acceptance manifest](keys-tree-scoped-picker-acceptance-2026-11-05.json):

- 18 deterministic `KeysTreeScopedPickerTests` facts.
- Seven tree/scoped Linux PTY journeys and two double-Escape PTY journeys.
- A paired PTY differential, `terminal-keys-tree-scoped-picker`, at
  100x30 and 140x40, dark, fullscreen and regular.

## Evidence

The [checkpoint evidence](keys-tree-scoped-picker-checkpoints-2026-11-05.json)
contains 71 named checkpoints per paired case. Tree rows, selection, fold state,
filters, search, labels, clipboard writes, scoped enabled order, counts,
session-only/unsaved state, saved `enabledModels`, and the restart scope all
match pinned Pi exactly. Four paired cases have zero scenario errors, zero
semantic differences, and matching provider traces (no prompts were sent).
Same-Pi calibration reports zero differences for all four cases.

Persisted observations match: `shellCommandPrefix` is preserved,
`enabledModels` becomes `["fixture/beta", "fixture/alpha"]`, and both products
record the label trail `first label`, `second answer label`,
`second answer label updated` (Pi appends to the imported JSONL; PiSharp
commits the same trail to its native session document).

## Verification

| Gate | Result |
| --- | --- |
| `dotnet format PiSharp.slnx --verify-no-changes` | pass |
| `dotnet build PiSharp.slnx --warnaserror` | 0 warnings, 0 errors |
| Full .NET suite | 4,471/4,471 tests, zero skipped |
| Paired PTY (4 cases) | 0 semantic differences |
| Same-Pi calibration (4 cases) | 0 semantic differences |
| Acceptance verifier + mutation controls | pass (284 captured checkpoints; all mutation negative controls rejected) |
| Exact-head CI | pending second review-correction CI |
| Draft PR | #42 (draft, open, unmerged) |


## Review corrections (2026-11-05)

- Session-controlled tree text, labels, tool summaries, search and label-edit
  buffers now pass through `TerminalSafeText` before styling. A seeded PTY case
  with embedded `ESC[2J` and OSC title/clipboard sequences verifies only visible
  text reaches the terminal.
- `/scoped-models` opens immediately from `GetAvailableModelSnapshot()` and
  performs one bounded background refresh instead of awaiting discovery twice.
  The selector re-derives order and selection once the snapshot lands, unless the
  user has already edited or navigated it.
- Refreshed snapshots mark models that disappeared as unavailable while keeping
  them addressable as placeholder rows.
- The tree selector requests a render when terminal dimensions change.
- Ctrl+S saves only when the configured `app.models.save` binding matches; the
  unconditional fallback is removed.

## Review corrections (round 2)

- Tree navigation no longer overwrites an existing editor draft; the navigated
  user prompt is restored only when the draft is empty/whitespace. A configured
  `app.session.tree` PTY shortcut keeps `draft to keep` intact.
- Branch summarisation runs behind an Escape-capable progress path. Escape
  cancels the summary and reopens `/tree` at the same selection; the aborted
  navigation leaves the active branch unchanged.
- `/scoped-models` retains every configured pattern that resolves to no model,
  including globs and unqualified names, preserving diagnostic order and
  deduplicating.
- The selected tree row now uses Pi's horizontal viewport: the two-cell gutter
  stays fixed while the body pans to keep the selected anchor and its content
  visible on narrow terminals.
- `app.session.tree` dispatches during streaming. Committing a navigation
  restores queued input to the editor, aborts and settles the active response,
  then changes the branch. The M14 active-action oracle now records
  `app.session.tree` alongside the model/thinking actions.
- Read tool summaries include Pi's `formatReadLineRange` suffix, including
  numeric strings and malformed-value omission. The paired fixture now exercises
  `[read: README.md:10-29]`.

## Scope notes

- `settings.treeFilterMode` remains separately tracked; `/tree` opens with the
  default filter here.
- PiSharp internal bookkeeping entry types are hidden in every filter; pinned Pi
  has no equivalent entries.
- The renderer is a faithful port of Pi's row priority, wrapping and selected-row
  horizontal viewport, not a cell-for-cell renderer match.
- Tree labels and enabled-model scope use each product's native persistence
  path while matching the observed semantic trail.
