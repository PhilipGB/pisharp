# Wrapped Bash command style parity — 2026-11-06

This milestone addresses the `ui-trailing-wrapped-command-style` P2 recorded by
the ENV-PARSER-001 evaluation in draft PR #46. It also corrects the
exact-boundary variant found while establishing the Pi contract: a command that
exactly fills the transcript width must not carry the command foreground into
the right output-padding cell.

## Prerequisite

- PR #45 reviewed and merged at
  `0984bfc922714862e2af244133313828617ca730`.
- Branch: `milestone/wrapped-bash-command-style-parity`, based on that merge;
  draft PR #46 remains open and unmerged.
- Implementation revision: `95704af040c2e8043457383ea31f88204eba7a72`.
- Initial review head: `26ee415c9141ba6c01a3ebc8bad19bf06048b330`.
- Pi reference: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (unchanged).

## Reference behaviour

Pi's Bash call renderer formats the command as
`theme.fg("toolTitle", theme.bold("$ " + command))` in a full-width text line
(`packages/coding-agent/src/core/tools/renderers/bash.ts`). When the line is
wrapped by the terminal layout:

- every **soft-wrapped continuation row** keeps the active title foreground,
  bold attribute and tool background through the row's trailing padding,
  including the one-cell right output padding;
- the **final row** of the command resets the foreground before its trailing
  padding; only the tool background remains;
- a command that exactly fills the transcript width likewise leaves the
  right output-padding cell at the default foreground with the tool background;
- the following tool output keeps its original row position.

The same-Pi capture pinned these values at 80x24 for the wrapped fixture command:
row 5, columns 63–79, foreground `rgb(222,224,225)`, background
`rgb(52,56,58)`, bold `true`. The final command row and the exact-boundary output
padding use the default foreground and keep `rgb(52,56,58)` as the background.

## Root cause

Two distinct paths were involved.

1. **Soft-wrapped padding (fixed by PR #45).** The pre-#45 collapsed preview
   pre-wrapped the Bash block and then re-wrapped the joined rows. The inserted
   hard newline made the compositor treat the first command row as a completed
   logical line, so it reset the foreground before the 17 padding cells. PR #45's
   `TerminalBashResultPreview` builder avoids that pre-wrap for built-in Bash
   results, allowing the compositor to preserve the active style while the line
   continues.
2. **Exact-boundary output padding (fixed here).**
   `TerminalScreenCompositor.PadTranscriptRow` also preserved the active style
   whenever `rowWidth >= transcriptWidth`, even when the next row was not a
   continuation. Pi resets the foreground in that case. The condition now applies
   only when the caller explicitly marks the row as a soft-wrap continuation.

## Implementation

- `src/PiSharp.Cli/Tui/Rendering/TerminalScreenCompositor.cs`: preserve the
  active cell style across trailing padding only for a wrapped-line
  continuation; otherwise keep the panel background and default foreground.
- `tests/PiSharp.Tests/Cli/Tui/Rendering/BashToolCollapseTests.cs`: eight
  cell-level regressions (one a three-width theory) covering a one-row command,
  the 17-cell two-row case, multiple widths, exact-boundary completion, a long
  command followed by output, failed-then-recovered commands, resize, and
  expanded/re-collapsed results. The assertions decode foreground, background
  and bold attributes at named cells.
- `tests/PiSharp.Tests/TestInfrastructure/TerminalStyledFrameReader.cs`: a test
  frame decoder that retains SGR cell styles and uses terminal cell widths for
  wide CJK, emoji and combining graphemes.
- `tests/PiSharp.Tests/Cli/Tui/Rendering/TerminalStyledFrameReaderTests.cs`:
  focused decoder tests for wide, emoji and combining characters.
- `tests/PiSharp.Tests/Cli/Tui/Rendering/ToolCallRenderingTests.cs`: a focused
  compositor regression for the exact-width output-padding cell.

No tool-result, session, provider, extension or agent-loop behaviour changed.

## Fail-first evidence

The new regressions were run against the pre-milestone binary (PR #44 merge
`7748479c`, before PR #45). The key 17-cell regression failed with:

```text
Continuation padding cell 63 of "$ x=objective-line; printf '%s-one\n' "$x"; sleep 0.3; printf "
has foreground , expected Rgb { R = 222, G = 224, B = 225 }.
```

The exact-boundary regression failed with:

```text
Assert.Null() Failure: Value of type 'Nullable<Rgb>' has a value
Expected: null
Actual:   Rgb { R = 222, G = 224, B = 225 }
```

Both pass on the milestone tree. The pre-milestone run also reproduced the
previously recorded six Bash collapse failures; those remain covered by the
merged PR #45 regressions.

## Comparisons

`objective-agent-progress` at 80x24:

- before: 19/20 (progress 10/10, visual 9/10), 17 style cells at
  `tool-complete`, `assistant-resume` and `final-idle`;
- after: 20/20 (progress 10/10, visual 10/10), zero differences.

`objective-agent-progress` at 120x40 remains 20/20 with zero differences.
Same-Pi calibration matches at both sizes. The exact-boundary 80x24 probe now
matches Pi at the right output-padding cell, with only the documented dynamic
render/cursor normalisation observed. The ENV-PARSER-002 recovery fixture remains
20/20 (progress 10/10, visual 10/10) at both sizes, including tool start, failure,
expand, re-collapse, recovery and final idle.

See [terminal-comparison.json](terminal-comparison.json) and
[ui-report.json](ui-report.json). Raw captures remain under
`artifacts/parity/` (Git ignored).

## Verification

- `dotnet test tests/PiSharp.Tests/PiSharp.Tests.csproj --no-restore
  --filter 'FullyQualifiedName~BashToolCollapseTests|FullyQualifiedName~ToolCallRenderingTests|FullyQualifiedName~TerminalStyledFrameReaderTests'`
  — 36 passed.
- Full .NET suite — 4,494 passed, 2 failed on pre-existing process/PTY timing
  flakes (`SessionShareTests.GistCancellationKillsTheChildAndCleansTheExport`
  timeout and `KeysQueueSessionPtyTests.FollowUpDequeue...`
  `TaskCanceledException`); both isolated reruns passed. The flakes are recorded
  as flakes, not as resolved.
- Terminal Python suite — 66 passed.
- Objective-agent Python suite — 8 passed.
- `dotnet build PiSharp.slnx --no-restore --warnaserror` — clean.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore` — clean.
- `python3 tools/validate_doc_paths.py` — 173 documents, 784 references.
- Exact-head CI is recorded on the draft PR.

## Remaining differences

No scored difference remains for the wrapped Bash command line at 80x24 or
120x40. Dynamic usage counters and the animated spinner/exit-cursor frame remain
covered by the previously documented normalisation. A wrapped `read` probe did
not reproduce the header-style defect; broader non-Bash tool-header wrapping was
not part of this milestone. No live Luna run was repeated; the historical 80/80
agent-functional evidence is unchanged.

The next single deliverable is a broader evaluation of PiSharp's interactive
terminal UI, particularly editing, navigation, session interaction and streaming
behaviour. It has not been started.
