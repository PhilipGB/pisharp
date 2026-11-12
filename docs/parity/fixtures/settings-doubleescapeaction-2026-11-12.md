# `settings.doubleEscapeAction` parity milestone

Bounded milestone on top of the post-#52 `main`. Reference Pi is pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`; the post-merge base is
`0c383d547994f7e431f9fe9796bbec702d99a716` and the implementation head is
`0e29d5d40170bbe65f584aeeed0a17576ed4d90a` on
`milestone/double-escape-action-parity`.

## Contract frozen from pinned Pi

- `SETTINGS_DEFAULTS.doubleEscapeAction = "tree"`.
- The declared union is `tree`, `fork`, `none` (`settings-schema.ts`), shown by
  the settings selector as **Double-escape action** — *Action when pressing
  Escape twice with empty editor* — with the values in that order.
- `getDoubleEscapeAction()` returns `merged.doubleEscapeAction ?? "tree"`. It
  does **not** validate the value, unlike `getTreeFilterMode()`.
- `interactive-mode.ts` `defaultEditor.onEscape` handles streaming, running
  bash, Bash-entry mode and a non-empty (trimmed) editor before the double-press
  action. Only the exact string `none` skips the timer, only the exact string
  `tree` opens the tree, and every other value takes the fork path. A pair fires
  when the second press is *strictly* less than 500 ms after the first, then
  resets the timer to 0.
- Autocomplete cancels Escape in the editor without invoking `onEscape`, so it
  never touches the double-press timer.

Because scopes merge by property presence (`deepMergeObjects`), a trusted project
entry replaces a user entry even when the value is one the schema would reject;
`null` is nullish and therefore resolves to `tree`.

## Pinned behaviour of malformed stored values

| Stored `doubleEscapeAction` | Pinned result |
| --- | --- |
| absent | tree |
| `"tree"` / `"fork"` / `"none"` | tree / fork / none |
| `"bogus"`, `""`, `42`, `false`, `{}`, `[]` | fork (returned unchanged, not `"tree"`) |
| `null` | tree |

`/settings` never writes these; only the read path accepts them. The writer
therefore keeps the three interactive values and rejects everything else.

### What `/settings` does with them (frozen from the executable pinned Pi)

`getDoubleEscapeAction()` returns the merged JS value unchanged and the settings
selector passes it in as `currentValue`. The list renderer then calls
`truncateToWidth(item.currentValue, …)`, which assumes a string:

| Stored | Pinned `/settings` | Activating the row |
| --- | --- | --- |
| `"bogus"` | renders `Double-escape action  bogus` | cycles to `tree` (index −1 → next value) |
| `null` | renders `tree` (getter applied `?? "tree"`) | cycles to `fork` |
| `42` | list opens; the row render crashes with `TypeError: text.slice is not a function` once visible | — |
| `false`, `{}`, `[]` | the list itself crashes with the same `TypeError` | — |

That is an upstream crash inside the renderer, not a settings error: the process
prints `pi exiting due to uncaughtException` and the session dies.

PiSharp renders the stored value exactly as stored (`42`, `false`, `{}`, `[]`,
`bogus`, and `inherit/default` for `null`), keeps `/settings` usable, and offers
its value list. This is a **declared safety projection**: PiSharp does not
reproduce the upstream crash, and the verifier freezes both the pinned crash
observations and the PiSharp render, rejecting any change to either side.

## Fail-first baseline at `0c383d54`

Already matching pinned Pi: unset/tree/fork/none, the inside-window pair, the
out-of-window pair, the empty-editor guard, Bash-entry Escape, a trusted project
`fork` over user `none`, untrusted exclusion and the `treeFilterMode`
composition.

Diverging: PiSharp refused to start for `"bogus"` (`settings.json
doubleEscapeAction must be 'fork', 'tree' or 'none'`) and for any non-string
(`settings.json property 'doubleEscapeAction' must be a string`), where pinned Pi
loads both; its invalid-value fallback would also have opened the tree instead of
the fork. `/settings` had no row and the writer no key.

## Implementation

- `DoubleEscapeActionSetting` owns the ordered value list; `DoubleEscapeActionSettingValue`
  carries presence plus the raw stored value and resolves `Action` as
  `raw ?? "tree"` — the pinned getter's rule, deliberately different from
  `treeFilterMode`'s validating resolver.
- `UserSettings.LoadAsync` keeps any present entry (strings as-is, JSON null as
  null, other JSON as its raw text) and `Overlay` replaces by presence.
- `TerminalEditor.HandleIdleEscapeAsync` reads the provider on every press,
  returns early only for `none`, and sends `tree` to `app.session.tree` and
  everything else to `app.session.fork`; the injectable clock keeps the strict
  `< 500 ms` window.
- `TerminalSettingsPicker` adds the row at Pi's shared position with the pinned
  label, description and value order; `UserSettingsWriter` accepts only
  `tree`/`fork`/`none`, preserves unrelated JSON and removes the key on reset.

Live updates work because the provider closes over the active project runtime,
which `/settings` rewrites on save.

## Evidence

| Gate | Result |
| --- | --- |
| Paired PTY, fullscreen (100x30, 140x40) | 0 semantic differences |
| Paired PTY, regular (100x30, 140x40) | 0 semantic differences |
| Paired PTY, malformed values | 0 semantic differences (7 stored shapes) |
| Malformed `/settings` (pinned freeze + safety projection) | pinned crash and PiSharp render frozen; both sides verified |
| Regression: unrelated settings preserved when storing a malformed value | `quietStartup`, `shellCommandPrefix`, `theme`, `tuiMode` kept |
| Paired PTY, trusted/untrusted project | 0 semantic differences (`fork`, `42`) |
| Paired PTY, treeFilterMode composition | 0 semantic differences (`[user]`) |
| Same-Pi calibration, every fixture | 0 semantic differences |
| Settings journey (pinned Pi) | row, description, order, live change and restart persistence match |
| Focused tests | 157 passed, 0 failed, 0 skipped |
| Full suite | 4616 passed, 0 failed, 0 skipped (9m44s) |
| `verify_double_escape_action.py` with mutation controls | pass; 42 negative controls rejected |
| Exact-head CI | Linux CI run `43205204817` on `b9ea7596` (implementation `84ac4fc6`): all five jobs green |

The pinned observations behind the oracle: the default and `"tree"` open the
session tree at `(8/10)`, `"fork"` opens **Fork from** with three user messages
and the newest selected, `"none"` and an out-of-window second Escape open
nothing, `"   "` counts as empty, and a non-empty draft keeps its text.

## Declared projections

- PiSharp keeps its standard **Inherit / default** row in the value list; pinned
  Pi cycles only the three declared values.
- PiSharp does not reproduce the pinned renderer crash for malformed non-string
  values; the stored text is shown and `/settings` stays usable.
- The fork panel is compared as message count, selected index and selected
  preview text, because the two products lay the rows out differently.
- The pinned settings journey runs in **regular** mode: a long fullscreen
  settings session overflows the emulator snapshot. The row, description, value
  order, live application and persistence are mode-independent, and PiSharp's own
  `/settings` is covered by `DoubleEscapeActionPtyTests` and
  `TerminalSettingsPickerTests`.

## Evidence files

- `docs/parity/fixtures/settings-doubleescapeaction-pi-oracle-2026-11-12.json`
- `docs/parity/fixtures/settings-doubleescapeaction-acceptance-2026-11-12.json`
- `docs/parity/fixtures/settings-doubleescapeaction-checkpoints-fullscreen-2026-11-12.json`
- `docs/parity/fixtures/settings-doubleescapeaction-checkpoints-regular-2026-11-12.json`
- `docs/parity/fixtures/settings-doubleescapeaction-checkpoints-settings-2026-11-12.json`
- `docs/parity/fixtures/settings-doubleescapeaction-checkpoints-malformed-2026-11-12.json`
- `docs/parity/fixtures/settings-doubleescapeaction-checkpoints-project-2026-11-12.json`
- `docs/parity/fixtures/settings-doubleescapeaction-checkpoints-project-malformed-2026-11-12.json`
- `docs/parity/fixtures/settings-doubleescapeaction-checkpoints-composition-2026-11-12.json`
- `docs/parity/fixtures/settings-doubleescapeaction-checkpoints-malformed-settings-2026-11-12.json`
