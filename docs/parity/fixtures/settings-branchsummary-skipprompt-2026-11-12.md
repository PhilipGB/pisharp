# Setting: `branchSummary.skipPrompt`

Verified capability `settings-branchsummary-skipprompt` against pinned Pi
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

Provenance:

| Item | Value |
| --- | --- |
| PR #53 reviewed head | `94312f5a918ebdbda36cf18a63e59c115cba5712` |
| PR #53 merge commit | `0911db693078d0bb5e52ad7a44aa268902abf22b` |
| Milestone base (`main`) | `0911db693078d0bb5e52ad7a44aa268902abf22b` |
| Branch | `milestone/branch-summary-skip-prompt-parity` |
| Implementation heads | `2e3ca31a49603e5e9fc7282c4726ef378d2bdc41` (initial), `aef1209305e445176092556d79f775109f8386d1` (review correction) |
| Pinned Pi | `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` |

## Contract

| Concern | Pinned-Pi source |
| --- | --- |
| Default | `core/settings-defaults.ts:14-17` — `branchSummary: { reserveTokens: 16384, skipPrompt: false }` |
| Declared schema | `core/settings-schema.ts:31-42` — optional boolean, described as skipping the `Summarize branch?` prompt and defaulting to no summary |
| Scope merge | `core/settings-manager.ts:58-82` — `deepMergeObjects` merges nested objects by property presence and skips only JavaScript `undefined`; arrays and other non-objects are not mergeable and replace the lower value |
| Loading | `core/settings-manager.ts:366-409` — `JSON.parse` plus migration only; stored values are never schema-validated |
| Trust gate | `core/settings-manager.ts:425-450` — an untrusted project contributes `{}` |
| Getter | `core/settings-manager.ts:882-891` — `this.settings.branchSummary?.skipPrompt ?? SETTINGS_DEFAULTS.branchSummary.skipPrompt`; the stored value is returned unchanged |
| Prompt gate | `modes/interactive/interactive-mode.ts:5602-5622` — the selector is skipped when the getter is falsey; `wantsSummary` stays `false` and `navigateTree` receives `summarize: false` |
| No `/settings` row | `grep -n skipPrompt` in `modes/interactive/components/settings-selector.ts` finds nothing |
| Write path | `core/settings-manager.ts:629-651` — writes enqueue the global scope only, so a file-authored value is never rewritten by tree navigation |

The tree flow therefore asks the question only when the merged value is falsey, and
JavaScript truthiness is what decides. Verified executable results:

| Stored effective `skipPrompt` | Prompt shown |
| --- | --- |
| absent | yes |
| `false` | yes |
| `null` | yes |
| `true` | no |
| `"false"` | no (non-empty string) |
| `""` | yes (empty string) |
| `1` | no |
| `0` | yes |
| `{}` | no |
| `[]` | no |
| `{"nested":false}` | no |

A non-object `branchSummary` (`null`, `"bad"`, `42`, `[]`) replaces the lower scope by
presence, after which `branchSummary?.skipPrompt` reads as absent and the prompt appears.

The loaded object is never schema-validated, so the boundary also freezes:

- an unrelated sibling (for example `futureSetting`) never affects `skipPrompt`, in user or
  trusted-project scope;
- a duplicated `skipPrompt` key resolves like `JSON.parse`: the later value wins
  (`false, true` skips; `true, false` prompts).

## Implementation

- `UserSettings.BranchSummary` is a `BranchSummarySettingValue?` carrying presence plus the
  parsed object (or `null` when the stored value is not an object), so an absent key and a
  present non-object key stay distinguishable.
- `BranchSummarySettings.SkipPrompt` is a `BranchSummarySkipPromptSetting?` carrying the
  verbatim JSON token and its `JsonValueKind`; `SkipsPrompt` evaluates JavaScript truthiness.
  `SkipsSummaryPrompt` is the merged gate, and `EffectiveReserveTokens` is unchanged.
- `MergeBranchSummary` reproduces `deepMergeObjects` for this object: two stored objects merge
  property by property (presence, including a present `null`), and any other stored shape
  replaces the lower scope.
- `BranchSummarySettings.Parse` reads only `reserveTokens` and `skipPrompt`: unrelated siblings
  are ignored, a duplicated `skipPrompt` keeps the later value, and `reserveTokens` stays
  validated (invalid or duplicated values still fail closed, out of scope).
- `TerminalSessionNavigation.ResolveBranchSummaryChoiceAsync` returns
  `(Resume: false, Summarize: false, CustomInstructions: null)` when the gate is truthy. The
  active-run interruption and `ConversationTreeSelectionOptions` construction are untouched, so
  the skipped-prompt path reaches the same navigation code as choosing `No summary`.
- No `/settings` row, value list entry or interactive writer was added; the setting is
  file-only exactly as in pinned Pi.

## Fail-first baseline

At base `0911db69` the settings corpus in
`tools/parity/branch_summary_skip_prompt_cases.json` produced 24 divergences and
7 matches:

- Matching already: unset, explicit `true`, explicit `false`, the project
  `reserveTokens`-only merge, a trusted project boolean override, the untrusted exclusion, and
  the normal nested merge.
- Diverging: every `null` and malformed case, because `BranchSummarySettings.Parse` required
  `skipPrompt` to be a JSON boolean and `branchSummary` to be an object. PiSharp threw
  `InvalidDataException: settings.json branchSummary.skipPrompt is invalid or unsupported.` or
  `settings.json branchSummary must be an object.` where pinned Pi loaded the file and then
  either prompted or skipped.

The baseline run is preserved as `artifacts/parity/.../pisharp-fail-first.json` during
development; the committed oracle and settings-boundary evidence freezes both sides after the
change.

A PR review then found one remaining boundary gap: unknown `branchSummary` siblings and a
duplicated `skipPrompt` key were still rejected by `BranchSummarySettings.Parse`, so those
files failed to load where pinned Pi ignored the sibling or kept the later value. The parser
correction (implementation `aef12093`) closed it; BS32-BS40 and the `tolerant` PTY fixture
freeze the behaviour, and the verifier rejects mutations of both.

## Evidence

| Gate | Result |
| --- | --- |
| Settings boundary differential (40 cases, executable pinned Pi) | 0 differences in load result, merged token, key presence, prompt decision and reserve |
| Fullscreen paired PTY, 100x30 and 140x40 | 0 semantic differences per case; exact same-Pi calibration |
| Regular-mode paired PTY, 100x30 and 140x40 | 0 semantic differences per case; exact same-Pi calibration |
| Malformed stored values and non-object parents | 0 semantic differences; all 11 cases prompt/skip exactly as pinned Pi |
| Unknown siblings and duplicated keys | User sibling (`futureSetting`), duplicated `skipPrompt` (`false,true` and `true,false`), trusted-project sibling-only merge and untrusted exclusion: 0 semantic differences |
| Layered project fixtures (false, null, reserve-only, malformed `0`, malformed `"false"`) | 0 semantic differences; trusted override, restart persistence and untrusted exclusion confirmed |
| Skipped-prompt navigation | Same target and session entries as `No summary`; no provider request; no `branch_summary` entry |
| `tools/parity/verify_settings_branch_summary_skip_prompt.py` with mutation controls | pass; 32 negative controls rejected |
| Focused tests | 82 passed, 0 failed, 0 skipped (35 distinct test methods) |
| `dotnet format` / `dotnet build --warnaserror` | pass |
| Full .NET suite | 4656 passed, 0 failed, 0 skipped (11m22s); `NativeExtensionUiTests` excluded locally because it hangs on this host (same hang at base), CI runs it unchanged |
| Exact-head CI | Linux CI pull_request run `43271103672` on implementation head `aef12093`: all five jobs green (the parallel push run `43271089322` hit an unrelated flaky builtin-tool workflow test) |

## Declared projections

- `branchSummary.reserveTokens` validation is unchanged: PiSharp still rejects a negative,
  non-integer or duplicated reserve, and a duplicated top-level settings key still fails closed,
  while pinned Pi's `JSON.parse` would keep the later value. Reserve handling is out of scope; the
  ledger item `settings-branchsummary-reservetokens` stays `not_started` and no required case
  exercises an invalid reserve. The milestone freezes `skipPrompt` and the enclosing object's
  unknown siblings and duplicated `skipPrompt` key.
- The PTY gate compares the semantic projection used by the earlier settings milestones rather
  than unrelated transcript/Markdown chrome.
- No `/settings` row exists in pinned Pi, so PiSharp adds none.

## Ledger

`settings-branchsummary-skipprompt` moves from `not_started` to `verified`.

`settings-branchsummary-reservetokens` is unchanged and remains `not_started`.
