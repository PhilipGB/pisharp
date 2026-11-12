# Setting: `treeFilterMode`

Verified capability `settings-treefiltermode` against pinned Pi
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Base was `main` at
`d9f7bf338c06947e32d3b669cda8d66f2b532785` (the merge of PR #51); the
implementation head is `8e3fcc2055d7ea7e69f5d60b9c402ab0eaabd94d` on
`milestone/tree-filter-mode-parity`.

## Contract

Pinned Pi owns the setting in these places, which this milestone mirrors:

| Concern | Pinned-Pi source |
| --- | --- |
| Default value | `core/settings-defaults.ts:46` — `treeFilterMode: "default"` |
| Accepted values and description | `core/settings-schema.ts:345-357` — union of `default`, `no-tools`, `user-only`, `labeled-only`, `all`; description `Default filter when opening /tree` |
| Effective value and persistence | `core/settings-manager.ts:1366-1375` — `getTreeFilterMode()` reads the merged settings and falls back to the default for anything outside the union; `setTreeFilterMode()` writes global settings and saves |
| Settings files are not schema-validated on load | `core/settings-manager.ts:377-408` — `JSON.parse` plus migration only, so an unrecognized value loads and resolves to the default |
| `/settings` presentation | `modes/interactive/components/settings-selector.ts:596-601` — label `Tree filter mode`, `values` in the order above; `:955-958` routes the choice to `onTreeFilterModeChange` |
| Persisting the choice | `modes/interactive/interactive-mode.ts:5080-5082` — `settingsManager.setTreeFilterMode(mode)` |
| Initial `/tree` filter | `modes/interactive/interactive-mode.ts:5575-5579` — `showTreeSelector()` reads `getTreeFilterMode()` on every open and passes `initialFilterMode` |
| Filter semantics and status labels | `components/tree-selector.ts:129-142` (state seeded before `applyFilter`), `:331-420` (filter rules), `:648-664` (`[no-tools]`, `[user]`, `[labeled]`, `[all]` suffixes) |
| Enum cycling in the picker | `packages/tui/src/components/settings-list.ts:284-288` — Enter/Space cycles the values array in order; there is no reset-to-unset entry |

Observable consequences frozen by the evidence:

- The setting is user/global configuration; a trusted project file overrides it through the normal settings merge.
- An unrecognized stored value (wrong case, unknown word, non-string) loads without an error and behaves as `default`.
- Settings scopes are merged by property **presence** (`settings-manager.ts` `deepMergeSettings`), and only the merged value is validated. A trusted project entry therefore replaces a valid user entry even when it is not an accepted string, and then resolves to `default`; an untrusted project contributes no entry at all.
- `/tree` reads the setting at each open. Selector filter keys, search, folding and label edits never write settings, so a temporary filter change is discarded when the selector reopens.
- Pi has no settings-UI reset row; PiSharp keeps its existing settings-architecture inherit row and Delete-reset. Both routes end at the same effective default.

## Implementation

- `UserSettings.TreeFilterMode` is a `TreeFilterModeSettingValue?` carrying presence plus the raw stored value (absent, accepted string, or present-but-unusable). `EffectiveTreeFilterMode` resolves it through `TreeFilterModeSetting`, the single ordered value list at the settings boundary; the overlay replaces by presence, so a malformed trusted-project entry wins and resolves to `default`.
- `TerminalTreeFilterModes` maps those strings onto the existing `TerminalTreeFilterMode` enum.
- `TerminalSessionNavigation.SelectTreePointAsync` reads `Settings.EffectiveTreeFilterMode` on every selector open, so `/tree`, `app.session.tree`, double-Escape configured as `tree`, and the branch-summary-cancel reopen all seed the same initial filter.
- `TerminalTreeSelector` passes the configured mode into `TerminalTreeState` before the first render, so no default-mode frame appears.
- `TerminalSettingsPicker` adds the pinned row (label, description, values in pinned order) and writes through the existing settings writer; `UserSettingsWriter` validates the five values and removes only that key when reset.

## Evidence

| Gate | Result |
| --- | --- |
| Fullscreen paired PTY, 100x30 and 140x40 | 0 semantic differences; exact same-Pi calibration |
| Regular-mode paired PTY, 100x30 and 140x40 | 0 semantic differences; exact same-Pi calibration |
| Settings-selector Pi journey (same-Pi calibration) | 0 differences; observed order `default`, `no-tools`, `user-only`, `labeled-only`, `all`; change applies to the next `/tree` open and survives the restart with unrelated settings preserved |
| `tools/parity/verify_settings_tree_filter_mode.py` with mutation controls | pass; 28 negative controls rejected |
| Focused tests | 75 passed, 0 failed, 0 skipped |
| Existing `keys-tree-scoped-picker` regression set | green (included in the 75) |
| `dotnet format` / `dotnet build --warnaserror` | pass |
| Full .NET suite | 4556 passed, 0 failed, 0 skipped (9m36s) |
| Exact-head CI | Linux CI run `43082423591` on `7dc3538c`: all five jobs green |

### Fail-first baseline

At `d9f7bf33` (base artifact `94231bf5…`) PiSharp exited with
`settings.json contains unsupported property 'treeFilterMode'.` as soon as a
configured mode was installed, while its unset `/tree` projection already
matched pinned Pi exactly (`(8/10)`, selection `[bash]`, identical rows). The
tree implementation was therefore complete; only the configured initial mode
was missing.

### Environment notes

- The full suite is measured without the `NativeExtensionUiTests` class, which
  hangs in this development environment. The same hang reproduces on base
  `d9f7bf33` for
  `NativeExtensionUiTests.PackageInputListenersRunBeforeFocusedCustomComponents`,
  so it is pre-existing and unrelated; CI runs the class unchanged.
- Two transient local failures under heavy machine load were re-run and passed
  (`EditorMutationPtyTests.MutationSubmissionReachesTheProviderAndRestoresTheTerminalThroughLinuxPty`,
  `KeysQueueSessionPtyTests.FollowUpDequeueRestoresSteeringFollowUpAndDraftWithoutPrematureExecutionThroughLinuxPty`).
- The paired PTY report keeps the full-cell comparison: whole-screen chrome and
  transcript rendering still differ between the products for reasons outside
  this milestone. The milestone oracle compares tree-panel content, selection,
  counts, status suffixes, settings semantics and persisted state, exactly as
  the `keys-tree-scoped-picker` milestone did.

## Review correction (P2)

The first revision stored a non-string `treeFilterMode` as `null`, so a malformed
trusted-project entry fell through to the valid user value instead of overriding
it. The stored value is now a presence-carrying `TreeFilterModeSettingValue`, and
the layered cases above pin the corrected behaviour: user `user-only` plus a
trusted project `42` opens the default filter, while an untrusted project leaves
`user-only` effective. Regressions cover `42`, `null`, `true`, arrays, objects,
`"bogus"` and `""` project values, an absent project property, and the trust gate
end to end through the CLI.

## Declared projections

- PiSharp draws the tree and settings overlays inside its own bordered panel and
  uses its own key-hint wording; panel content and settings semantics are
  compared instead of the surrounding chrome.
- PiSharp appends its standard inherit/reset row to enum settings and supports
  Delete-reset; pinned Pi only cycles values. Effective default/reset behaviour
  is identical.
- PiSharp-only bookkeeping entries stay hidden in every filter, as in the
  previous tree milestone; pinned Pi has no equivalent entries.
- `/settings` writes honour PiSharp's existing scope toggle; the default scope is
  user settings, matching Pi's global write.
