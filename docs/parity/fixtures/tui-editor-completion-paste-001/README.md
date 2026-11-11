# TUI editor, history, completion and paste differential — 2026-11-10

Capability: `feature-tui-editor-history-completion-and-paste`.

Completion milestone: slash-command and `@file` completion parity.

- Base `main`: `40f562dbcd47c570300136b330178221019f5758` (PR #50 merged).
- Branch: `milestone/tui-command-file-completion-parity`.
- Pi reference: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`
  (`/home/philip/Projects/pi-pinned`).
- Implementation/evidence SHA: `0e524751de516a1772813b9a1e38898ca6059547`
  (`head.txt`).
- Terminal sizes: `80x24` and `120x40`, dark theme, fullscreen/alternate screen.

## Evidence

- [report.json](report.json): **100/100** across 19 frozen scenarios and both
  sizes (38 entries), with same-Pi calibration, editor-region checkpoints,
  completion overlays, cursor columns, provider-visible text and terminal exit
  state. Zero recorded differences.
- [acceptance.json](acceptance.json): 38 mandatory machine-readable cases, each
  mapped to pinned-Pi source or an observed Pi process result, plus 2 declared
  deferrals.
- [baseline.json](baseline.json): fail-first run of the same frozen scenarios
  against post-#50 PiSharp at `40f562db` before product changes (122 mismatches).
- [source-inventory.md](source-inventory.md): pinned-Pi source contract for
  discovery, ranking, activation, application, trailing spaces, cancellation and
  the upstream tests used as the oracle.
- [head.txt](head.txt): the exact PiSharp head the report was captured from.
- [inventory.py](../../../../tools/parity/tui-editor-completion-paste/inventory.py):
  frozen projections and declared differences.

## Method

`tools/parity/tui-editor-completion-paste/run.py` drives the real Pi and PiSharp
CLIs through the shared Linux PTY harness (`tools/parity/terminal/run.py`). Every
scenario runs first as `Pi -> Pi` calibration and then as `Pi -> PiSharp`. Actions
settle on editor-region text, overlay state or an explicit idle barrier; no
scenario uses a fixed sleep for stepping.

Support added or extended for this milestone:

- `compareCandidateDescriptions`: path and argument overlays compare the
  description column as well as candidate identity and order. Slash command
  descriptions are excluded by the declared `slash-command-description-copy`
  projection because command copy belongs to the interactive catalogue and its
  help/RPC consumers.
- `cursorColumn`: editor text is right-trimmed for stable comparison, so the
  cursor column records applied trailing spaces (file completions insert one
  after files, none after directories or unprefixed Tab completions).
- `$symlink` fixture entries so real `fd --follow` symlink behaviour is compared
  without touching the product.
- New scenarios `path-symlink`, `file-tab-completion` and `login-completion`;
  the existing `slash-completion` and `path-completion` scenarios gained argument
  completion, fuzzy ordering, navigation, no-match, paste-versus-typed, embedded
  completion, provider-visible submission and traversal/display checkpoints.
  `login-completion` compares `/login anthropic` and `/login radius` candidates,
  auth-method descriptions, insertion and no-match, with descriptions compared
  exactly. The original PR #49 checkpoints are unchanged.
- `acceptance_cases.py` / `freeze_acceptance.py` freeze the mandatory cases from
  the Pi column; `verify.py` recomputes every comparison, enforces the manifest
  and evidence head, and runs 21 negative controls with `--self-test`.

## Fail-first baseline

At post-#50 `40f562db` with the frozen scenarios and pre-change product code
(`baseline.json`):

| Scenario | 80x24 | 120x40 | Observed misses |
|---|---:|---:|---|
| `slash-completion` | 33 | 33 | `/thinking` argument picker absent (Tab fell through to file completion); cursor movement into a command name closed instead of re-querying the picker; Backspace after Escape did not reopen the picker |
| `path-completion` | 8 | 8 | Backspace after Escape did not reopen the `@` picker |
| `path-symlink` | 0 | 0 | already matched; regression guard |
| `file-tab-completion` | 20 | 20 | forced Tab picker closed when typing; unprefixed completion inserted a trailing space and cursor sat one column late |
| `login-completion` | not in `baseline.json` | not in `baseline.json` | scenario added during review; the pre-feature baseline had no `/login` argument picker, and the evidence head lost second auth methods (see below) |

Same-Pi calibration passed for every scenario at both sizes.

## Review correction: `/login` auth methods

Review of the first head found that `GetLoginArgumentSuggestions` read one auth
type per provider record, so a runtime provider supporting both methods (the
production shape supplied by `TerminalPresentation`) only contributed `oauth`.
Anthropic lost `API key`, Radius lost `API key` and mislabelled OAuth as a
subscription, and no-auth virtual providers could appear with an empty
description.

Pre-fix evidence at the recorded evidence head `aa7e193b`, same harness and
80x24:

```text
login-anthropic descriptions  pi=['Anthropic · subscription/API key']  pisharp=['Anthropic · subscription']
login-radius    descriptions  pi=['Radius · account/API key']           pisharp=['Radius · subscription']
```

The fix projects each runtime provider into one option per supported method
before merging by id, keeps `oauth` first, labels non-subscription OAuth as
`account`, omits providers with no auth method, and keeps auth labels in the
search text. The frozen `login-completion` case now compares the exact
candidates and descriptions for Anthropic and Radius.

## Corrections

1. **Argument completion follows the live runtime data.** Pi exposes
   `getArgumentCompletions` for `/model`, `/thinking` and `/login`.
   `EditorAutocomplete` now completes thinking levels from the selected model's
   supported set and login providers from the live provider catalogue with Pi's
   per-auth-method projection, dedupe, oauth-before-api-key order, display-name
   ordering, search text and descriptions. A provider supporting both methods
   therefore contributes one candidate with both auth types (for example
   `Anthropic · subscription/API key`); Radius OAuth is labelled `account`
   because it is not a subscription, and providers supporting neither method are
   omitted. `/model` behaviour is unchanged.
2. **Cursor movement re-queries an open picker.** Moving within a visible picker
   recomputes candidates for the new cursor position, matching Pi's `moveCursor`
   update, instead of leaving stale candidates or closing the overlay.
3. **Deletions re-query and can reopen completion.** Backspace/Delete after a
   dismissal reopens the picker for slash and `@` contexts, and any editing key
   clears the paste/Escape suppression so the next edit re-evaluates exactly like
   Pi's `handleBackspace`/`handleForwardDelete`.
4. **Forced Tab completion keeps its force state.** The picker stays open and
   narrows while typing, and unprefixed path completions insert the value with no
   trailing space, matching Pi's `applyCompletion`.
5. **Selection prefers the exact value, then the first prefix match.** This
   matches Pi's `getBestAutocompleteMatchIndex` for overlays where the first row
   is not the best value match.
6. **Deterministic login-flow fixtures.** The provider/radius login PTY fixtures
   now paste `/login <provider> …` commands. Typing them and bursting Enter in
   one write races Pi's own picker-confirm rule; pasting cancels autocomplete in
   both products and keeps those tests focused on the login flow.
7. **Bounded modal shutdown and deterministic PTY exits (suite stability, not
   completion behaviour).** The scoped-models selector no longer awaits a
   cancelled catalogue refresh without a bound, and the affected PTY fixtures
   dismiss any focused modal before exiting, falling back to stdin EOF. Before
   this, load-dependent runs could leave a selector open at quit and stop a
   testhost.

## Results

| Task | Frozen scenarios | Result | Points |
|---|---|---|---:|
| Cursor / Unicode / grapheme movement | `cursor-unicode` | pass, both sizes | 15 |
| Multiline editing and soft wrap | `multiline`, `soft-wrap` | pass, both sizes | 10 |
| Prompt history and draft restoration | `history` | pass, both sizes | 10 |
| Slash-command completion | `slash-completion` | pass, both sizes | 15 |
| `@` path completion | `path-completion`, `path-symlink`, `file-tab-completion` | pass, both sizes | 15 |
| Bracketed paste + markers | `bracketed-paste`, `large-paste-marker`, `large-paste-external-editor` | pass, both sizes | 10 |
| Escape / Ctrl+C / exit semantics | `escape-ctrl-c`, `exit-empty-ctrl-d` | pass, both sizes | 10 |
| External editor | four external-editor scenarios | pass, both sizes | 10 |
| Cross-component journey | `journey` | pass, both sizes | 5 |
| **Total** | | | **100/100** |

Acceptance cases split 20 slash-command cases and 18 `@file` cases. They cover
exact/fuzzy/non-prefix matching, deterministic ordering, keyboard navigation and
wrap, selection, application, trailing spaces, commands with and without
arguments, argument completion for `/model`, `/thinking` and `/login`, auth-method
descriptions, no matches, Escape draft preservation,
editor clear, paste versus typed activation, nested/relative/spaced/symlinked
paths, hidden entries and `.git` exclusion, project-root containment, embedded
completion in prose, and provider-visible submitted text.

## Projections

Applied identically to both products and declared in `inventory.py`:

- the named `exit` checkpoint's absolute cursor row is not compared (post-exit
  scrollback height); exit compares exit code, active screen, cursor visibility
  and private modes;
- one leading `@` is stripped from overlay display labels only;
- the `(n/m)` position row is recorded separately from candidate identities;
- external-editor temporary directory names and file modes are captured but not
  compared;
- slash-command overlay descriptions are not compared; path and argument
  descriptions are.

Editor text is right-trimmed for stable rendering comparison; applied trailing
spaces are compared through the cursor column and, for prompts, through the
provider-visible expanded text. No editor text, cursor cell, candidate
identity/order (or compared description), selection, wrapping, submitted prompt
or history order is normalised.

## Declared deferrals

- **`/login` catalogue breadth.** Pi exposes a 43-entry provider/auth catalogue,
  including providers PiSharp does not register (for example OpenRouter OAuth).
  Frozen login cases use exact provider ids whose auth methods agree; PiSharp
  implements the same per-auth-method projection, dedupe, ordering, labels,
  search and insertion over its narrower registered provider surface. Remaining
  catalogue breadth is provider work outside this milestone.
- **`/bug` and `/changelog`.** Pi registers these built-ins; PiSharp does not
  implement them. Slash completion derives from the live registry, so they are
  absent. Frozen cases use queries where the registered catalogues agree.

Neither deferral affects a frozen case. They are recorded in `acceptance.json`
with their oracle.

## Validation

```sh
dotnet build PiSharp.slnx --warnaserror
dotnet format PiSharp.slnx --verify-no-changes
python3 tools/parity/tui-editor-completion-paste/run.py \
  --pi /home/philip/Projects/pi-pinned \
  --output artifacts/parity/tui-completion-parity-001
python3 tools/parity/tui-editor-completion-paste/verify.py \
  docs/parity/fixtures/tui-editor-completion-paste-001/report.json \
  --head aa7e193b2246af7ad64184aec262ec31a0c3fa6f --require-clean --self-test
dotnet test PiSharp.slnx
```

Results at the implementation head: verifier passes with 21 negative controls
rejected; `EditorCompletionTests` 30/30; full suite 4561/4561 passed
(`dotnet test PiSharp.slnx` with stdin redirected from `/dev/null` so a testhost
in a background process group cannot be stopped by terminal signals). Build and
format checks pass.

Raw PTY artifacts stay under the ignored `artifacts/parity/` tree. Only the
compact report, acceptance manifest, baseline, source inventory and receipt are
committed.
