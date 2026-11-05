# Keys: session tree and scoped model picker

Verified capability `keys-tree-scoped-picker` at implementation source
`12cd366111f1d9eb126102bb0ba372f461545bad` against pinned Pi
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

## Contract

The frozen [pinned-Pi oracle](keys-tree-scoped-picker-pi-oracle-2026-11-05.json)
defines T01-T08 for the `/tree` navigator and S01-S07 for the scoped-model
selector, with source anchors from `tree-selector.ts`, `scoped-models-selector.ts`,
`interactive-mode.ts`, `session-manager.ts`, `core/keybindings.ts` and
`docs/keybindings.md`.

All 15 scenarios are mapped to executable evidence in the
[acceptance manifest](keys-tree-scoped-picker-acceptance-2026-11-05.json):

- 12 deterministic `KeysTreeScopedPickerTests` facts.
- Three tree/scoped Linux PTY journeys and two double-Escape PTY journeys.
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
| Full .NET suite | 4,461/4,461 tests, zero skipped |
| Paired PTY (4 cases) | 0 semantic differences |
| Same-Pi calibration (4 cases) | 0 semantic differences |
| Acceptance verifier + mutation controls | pass (284 captured checkpoints; all mutation negative controls rejected) |
| Exact-head CI | Linux CI run `42059766439` on `601cbc12` (success) |
| Draft PR | #42 (draft, open, unmerged) |

## Scope notes

- `settings.treeFilterMode` remains separately tracked; `/tree` opens with the
  default filter here.
- PiSharp internal bookkeeping entry types are hidden in every filter; pinned Pi
  has no equivalent entries.
- The renderer is a faithful port of Pi's row priority and wrapping, not a
  cell-for-cell renderer match.
- Tree labels and enabled-model scope use each product's native persistence
  path while matching the observed semantic trail.
