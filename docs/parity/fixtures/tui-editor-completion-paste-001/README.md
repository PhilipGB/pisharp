# TUI editor, history, completion and paste differential — 2026-11-08

Capability: `feature-tui-editor-history-completion-and-paste` (ledger status:
`verified`).

- Base `main`: `dac380b0c99aae70bc8e62d6efce0f95f982daa3` (PR #48 merged).
- Branch: `milestone/tui-editor-completion-paste-parity`.
- Pi reference: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`
  (`/home/philip/Projects/pi-pinned`).
- Terminal sizes: `80x24` and `120x40`, dark theme, fullscreen/alternate screen.
- Evidence: [report.json](report.json). **100/100** across all frozen scenarios
  and both sizes, with same-Pi calibration, editor-region checkpoints,
  completion overlays, provider-visible text and terminal exit state.

## Method

`tools/parity/tui-editor-completion-paste/run.py` drives the real Pi and
PiSharp CLIs through the shared Linux PTY harness
(`tools/parity/terminal/run.py`). Every scenario is first run as `Pi -> Pi`
calibration and then as `Pi -> PiSharp`. Actions settle on editor-region text,
overlay state or an explicit idle barrier; no scenario uses a fixed sleep for
stepping. Harness support added here:

- `expectEditor` / `notExpectEditor`: settle on the editor region between the
  horizontal rules instead of the whole viewport.
- `quiesce`: drain until the producer is idle, used only as an action barrier.
- fixture `binFiles`, so a deterministic `fd` shim and fake editors can be
  installed without touching the product.
- fixture `captureWorkspaceFiles` for external-editor temporary-file evidence.

`verify.py` recomputes every comparison from the embedded observations, rejects
unexplained failures, stale provenance, hidden mismatches and missing
scenarios/sizes/checkpoints, and runs 14 negative controls with `--self-test`.
The score rubric is frozen in the verifier.

## Corrections

All corrections were reproduced against the real CLI before being fixed:

1. **Bracketed paste no longer leaks into slash completion.** Pi cancels
   autocomplete when a bracketed paste arrives and does not trigger it again
   until a real key is typed. PiSharp rendered slash candidates for pasted
   `/hot` immediately. `TerminalEditor` suppresses automatic completion after a
   paste and clears the suppression on the next typed character or Tab.
   Regression:
   `AppControlPtyTests.BracketedPasteOfSlashTextDoesNotOpenCommandCompletionUntilTyped`.
2. **Escape closes the automatic slash overlay without clearing the draft.**
   Pi cancels its autocomplete state on Escape. PiSharp recomputed the
   automatic slash overlay from the buffer immediately. Escape now suppresses
   the automatic overlay until the next typed character or Tab. Regression:
   `AppControlPtyTests.EscapeClosesTheAutomaticSlashOverlayWithoutClearingTheDraft`.
3. **Slash completion matches Pi's engine and ordering.** `EditorAutocomplete`
   ports Pi's `fuzzyFilter` (token matching, alphanumeric-swap fallback, stable
   score ordering), uses Pi's builtin completion order, preserves dynamic
   skill/prompt/extension commands, applies the trailing space and closes the
   picker, exposes `/model` argument completion, and renders Pi's candidate
   window plus the `(n/m)` position row. PiSharp-only commands remain
   discoverable when Pi's catalogue has no match. Regressions:
   `EditorCompletionTests.PiFuzzySlashOrderingMatchesThePinnedCatalogue`,
   `EditorCompletionTests.SlashTabAppliesPiTrailingSpaceAndClosesThePicker`.
4. **`@` path completion matches Pi's automatic fd walk and application.**
   PiSharp now auto-triggers `@` completion, runs `fd` with Pi's arguments and
   scoring/order, quotes paths that need it, preserves directory
   continuation, and applies files with the trailing space after the closing
   quote. Readdir prefix completion remains for forced non-`@` paths.

## Results

| Task | Frozen scenarios | Result | Points |
|---|---|---|---:|
| Cursor / Unicode / grapheme movement | `cursor-unicode` | pass, both sizes | 15 |
| Multiline editing and soft wrap | `multiline`, `soft-wrap` | pass, both sizes | 10 |
| Prompt history and draft restoration | `history` | pass, both sizes | 10 |
| Slash-command completion | `slash-completion` | pass, both sizes | 15 |
| `@` path completion | `path-completion` | pass, both sizes | 15 |
| Bracketed paste | `bracketed-paste` | pass, both sizes | 10 |
| Escape / Ctrl+C / exit semantics | `escape-ctrl-c`, `exit-empty-ctrl-d` | pass, both sizes | 10 |
| External editor | four external-editor scenarios | pass, both sizes | 10 |
| Cross-component journey | `journey` | pass, both sizes | 5 |
| **Total** | | | **100/100** |

The cursor scenario covers ASCII, CJK wide cells, emoji, a combining sequence,
grapheme-safe Left/Right, Home/End, Ctrl+A/E, word movement, Backspace/Delete
and exact submitted text. Soft wrap is exercised at both terminal widths.
History covers submits, multiline recall, draft restoration, cursor placement
and consecutive-duplicate suppression. Paste covers single-line, multiline,
padded, tabs, CJK/emoji/combining, slash-like and `@`-like text, and a bounded
ten-line paste followed by an edit and exact provider submission.

## Normalisations

Applied identically to both products and declared in `inventory.py`:

- the named `exit` checkpoint's absolute cursor row is not compared (post-exit
  scrollback height); exit compares exit code, active screen, cursor visibility
  and private modes;
- one leading `@` is stripped from overlay display labels only;
- the `(n/m)` overlay position row is recorded separately from candidate
  identities;
- external-editor temporary directory names and file modes are captured but not
  compared. Both create a 0700 directory; Pi writes the prompt 0644 and PiSharp
  0600.

No editor text, cursor cell, candidate identity/order, selection, wrapping,
submitted prompt or history order is normalised.

## Remaining out-of-vector finding

Pi replaces pastes over 10 lines or 1000 characters with `[paste #N …]` markers
and expands them on submit. The frozen paste vector is exactly ten lines, so
inserted text, editing, cursor placement and submission match. Marker insertion
and atomic marker movement/deletion are not implemented in PiSharp and are not
claimed by this evidence; this is recorded as a separate finding rather than
part of the accepted required matrix.

## Reproduction

```sh
dotnet build PiSharp.slnx --warnaserror
python3 tools/parity/tui-editor-completion-paste/run.py \
  --pi /home/philip/Projects/pi-pinned \
  --output artifacts/parity/tui-editor-completion-paste-001
python3 tools/parity/tui-editor-completion-paste/verify.py \
  artifacts/parity/tui-editor-completion-paste-001/report.json --self-test
dotnet test tests/PiSharp.Tests/PiSharp.Tests.csproj \
  --filter "FullyQualifiedName~AppControlPtyTests|FullyQualifiedName~EditorBufferTests|FullyQualifiedName~EditorCompletionTests|FullyQualifiedName~EditorKeymapTests|FullyQualifiedName~ExternalEditorTests"
```

Raw PTY artifacts stay under the ignored `artifacts/parity/` tree. Only the
compact report, inventory, harness and receipt are committed.
