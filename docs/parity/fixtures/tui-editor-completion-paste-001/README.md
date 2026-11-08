# TUI editor, history, completion and paste differential — 2026-11-08

Capability: `feature-tui-editor-history-completion-and-paste` (ledger status:
`in_progress`).

- Base `main`: `dac380b0c99aae70bc8e62d6efce0f95f982daa3` (PR #48 merged).
- Branch: `milestone/tui-editor-completion-paste-parity`.
- Pi reference: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`
  (`/home/philip/Projects/pi-pinned`).
- Terminal sizes: `80x24` and `120x40`, dark theme, fullscreen/alternate screen.
- Evidence: [report.json](report.json). It embeds every checkpoint for both
  products, same-Pi calibration, provider-visible text, completion overlays and
  terminal exit state.

## Method

`tools/parity/tui-editor-completion-paste/run.py` drives the real Pi and
PiSharp CLIs through the shared Linux PTY harness
(`tools/parity/terminal/run.py`). Every scenario is first run as `Pi -> Pi`
calibration and then as `Pi -> PiSharp`. Actions settle on editor-region text,
overlay state or an explicit idle barrier; no scenario uses a fixed sleep for
stepping. The harness now supports:

- `expectEditor` / `notExpectEditor`: settle on the editor region between the
  horizontal rules instead of the whole viewport (transcript text can no longer
  satisfy an editor expectation).
- `quiesce`: drain until the producer is idle, used only as an action barrier.
- fixture `binFiles`, so a deterministic `fd` shim and fake editors can be
  installed without touching the product.

`verify.py` recomputes every comparison from the embedded observations, rejects
unexplained failures, stale provenance, hidden mismatches and missing
scenarios/sizes/checkpoints, and runs 14 negative controls with `--self-test`.
The score rubric is frozen in the verifier and was declared before corrections.

## Corrections

Two genuine PiSharp defects were reproduced fail-first through the real CLI:

1. **Bracketed paste no longer leaks into slash completion.** Pi cancels
   autocomplete when a bracketed paste arrives and does not trigger it again
   until a real key is typed. PiSharp rendered slash candidates for pasted
   `/hot` immediately. `TerminalEditor` now suppresses automatic slash
   suggestions after a paste and clears the suppression on the next typed
   character or Tab. Regression:
   `AppControlPtyTests.BracketedPasteOfSlashTextDoesNotOpenCommandCompletionUntilTyped`
   (fails against the pre-correction source).
2. **Escape closes the automatic slash overlay without clearing the draft.**
   Pi cancels its autocomplete state on Escape. PiSharp cleared its cycle (if
   any) but immediately recomputed the automatic slash overlay from the buffer.
   Escape now suppresses the automatic overlay until the next typed character
   or Tab. Regression:
   `AppControlPtyTests.EscapeClosesTheAutomaticSlashOverlayWithoutClearingTheDraft`
   (fails against the pre-correction source).

Both regressions assert termios restoration, exit code and the provider path.

## Results

| Task | Frozen scenarios | Result | Points |
|---|---|---|---:|
| Cursor / Unicode / grapheme movement | `cursor-unicode` | pass, both sizes | 15 |
| Multiline editing and soft wrap | `multiline`, `soft-wrap` | pass, both sizes | 10 |
| Prompt history and draft restoration | `history` | pass, both sizes | 10 |
| Slash-command completion | `slash-completion` | **blocked** | 0 |
| `@` path completion | `path-completion` | **blocked** | 0 |
| Bracketed paste | `bracketed-paste` | pass, both sizes | 10 |
| Escape / Ctrl+C / exit semantics | `escape-ctrl-c`, `exit-empty-ctrl-d` | pass, both sizes | 10 |
| External editor | four external-editor scenarios | pass, both sizes | 10 |
| Cross-component journey | `journey` | **blocked** by `@` completion | 0 |
| **Total** | | | **65/100** |

The cursor scenario covers ASCII, CJK wide cells, emoji, a combining sequence,
grapheme-safe Left/Right, Home/End, Ctrl+A/E, word movement, Backspace/Delete
and exact submitted text. Soft wrap is exercised at both terminal widths.
History covers submits, multiline recall, draft restoration, cursor placement
and consecutive-duplicate suppression. Paste covers single-line, multiline,
padded, tabs, CJK/emoji/combining, slash-like and `@`-like text, and a bounded
ten-line paste followed by an edit and exact provider submission.

## Remaining blockers

The capability stays `in_progress`. The exact observable gaps are declared in
`tools/parity/tui-editor-completion-paste/inventory.py` and evidenced by
`report.json`:

- **Slash completion:** Pi filters its builtin command list with `fuzzyFilter`;
  PiSharp uses the shared ordered catalogue with prefix matching and registers
  additional implemented commands. Pi appends a trailing space and closes the
  picker when an explicit Tab resolves one candidate, and exposes argument
  completion for `/model`. PiSharp applies without the trailing space and has no
  argument completion. Candidate identity, order, selection, editor text and
  cursor therefore differ at both sizes.
- **`@` path completion:** Pi auto-triggers an fd-based fuzzy walk, scores and
  orders entries, and applies a file with a trailing space after the closing
  quote. PiSharp only computes path candidates on Tab through readdir prefix
  matching, so the automatic overlay is absent and application puts the cursor
  before the closing quote. The cross-component journey fails only on this
  checkpoint; its provider text, history, paste and external-editor steps match
  exactly.

Neither gap is a bounded editor correction: they require a fuzzy command
catalogue / argument completion and an fd-backed path provider. They were not
started because this milestone is bounded and the instruction is not to begin
another parity goal.

A further known gap sits beyond the frozen paste vector: Pi replaces pastes
over 10 lines or 1000 characters with `[paste #N …]` markers and expands them on
submit. The required vector stays at the boundary (10 lines), so inserted text,
editing and submission match exactly; marker insertion/atomic movement is not
implemented in PiSharp and is not claimed here.

## Normalisations

Applied identically to both products and declared in `inventory.py`:

- the named `exit` checkpoint's absolute cursor row is not compared (post-exit
  scrollback height); exit compares exit code, active screen, cursor visibility
  and private modes;
- one leading `@` is stripped from overlay display labels only;
- the `(n/m)` overlay position row is recorded but excluded from candidate
  identities;
- external-editor temporary directory names and file modes are captured but not
  compared. Both create a 0700 directory; Pi writes the prompt 0644 and PiSharp
  0600.

No editor text, cursor cell, candidate identity/order, selection, wrapping,
submitted prompt or history order is normalised.

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
