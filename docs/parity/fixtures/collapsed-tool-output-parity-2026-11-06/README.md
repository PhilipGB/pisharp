# Bash tool output collapse and expansion parity — 2026-11-06

This milestone corrects PiSharp's collapsed Bash tool result so it matches the
pinned Pi reference. It follows the merged ENV-PARSER-002 evaluation, which
confirmed the collapsed-output threshold, the missing `ctrl+o to expand` hint and
the resulting row offsets as a P1 terminal discrepancy.

## Prerequisite

- ENV-PARSER-002 evaluation reviewed and merged in PR #44 as
  `7748479cc0d7794464fda43902e5db03a76af642`.
- Branch: `milestone/collapsed-tool-output-parity`, created from that merge.
- Pi reference: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (unchanged).

## Reference behaviour

Pi's Bash renderer (`packages/coding-agent/src/core/tools/renderers/bash.ts`)
uses `BASH_PREVIEW_LINES = 5` through `VisualLinePreview` with `keep: "end"`:

- the preview limit counts **visual rows after width wrapping**, not logical
  lines;
- a collapsed result shows the last five visual rows, preceded by
  `... (N earlier lines, ctrl+o to expand)` where `N` is the number of hidden
  visual rows;
- the hint is its own row: `muted` text, an unstyled separator space, the
  `dim`-styled shortcut and `muted` ` to expand)`;
- there is no second, whole-tool-block collapse with a different hint;
- each tool component is preceded by a spacer row, so consecutive tool blocks
  have an extra blank row between them.

## Root cause

`TerminalToolPresentation.RenderBashResult` collapsed on `\n`-split logical
lines, emitted `... (N earlier lines, to expand)` without the shortcut, and
appended the hint without a trailing newline, merging it with the first visible
output line. The generic transcript collapse then truncated the whole tool block
to ten visual rows with `... (N more visual rows; tool output collapsed)`,
producing a second, non-Pi hint and shifting every following row.

## Implementation

- `TerminalBashResultPreview` (new, `src/PiSharp.Cli/Tui/Rendering/`) owns Pi's
  five-visual-row selection, hint construction and segmented styling.
- `TerminalToolPresentation` builds the Bash result from that collaborator and
  captures the preview inputs before the call arguments are released. Custom
  extension renderers for `bash` keep precedence and are unaffected.
- `TerminalConversationPresentation` supplies a width-aware collapsed preview
  builder for Bash results, so resizing recomputes the preview at the new
  content width instead of reusing a fixed string.
- `TerminalTranscriptBuffer` caches builder previews per width, bypasses the
  generic ten-row truncation for them, and inserts Pi's spacer row between
  consecutive bounded tool results.
- `PiSharpToolTextStyle.Dim` maps to the theme's `dim` colour so the shortcut
  matches Pi's muted/dim hint styling without SGR dim attributes.

No runtime tool-result, session, provider or agent-loop behaviour changed.

## Fail-first regressions

`tests/PiSharp.Tests/Cli/Tui/Rendering/BashToolCollapseTests.cs` contains twelve
deterministic regressions covering the preview limit (below, at and above),
multiple visual rows from a wrapped line, failed and successful results,
consecutive results, expansion and re-collapse, width-dependent collapse,
resizing, hint styling, running/tool-completion transitions and timer cleanup.

Against the original implementation, six failed with the confirmed defect, for
example:

```
Expected: "... (2 earlier lines, ctrl+o to expand)"
Actual:   "... (2 earlier lines, to expand)three"
```

```text
Assert.Equal() Failure: Values differ
Expected: 13
Actual:   11
```

After the correction all twelve pass.

## Deterministic comparison

The `objective-agent-recovery` fixture is now gated on model responses so every
scored frame is an event boundary. It adds Ctrl+O expansion and re-collapse
checkpoints and uses unique output markers so expanded-only content can be
asserted. Fixture checksum changed from
`d66ba909bffbd346a92aaed4b0ec548051f81d993f9e7db4cb3e2fee5232b08d`
(historical ENV-PARSER-002 evidence) to
`2aba18ca274cd6b0e15ccb281e629dc0b2796fb229aaf2854076f036bfa765e2`.
The historical evidence directory is unchanged.

| Checkpoint | 80×24 | 120×40 |
|---|---|---|
| startup, prompt | match | match |
| tool start (`Working`) | match | match |
| failed result, hint and five visual rows | match | match |
| expanded result after Ctrl+O (status row) | match | match |
| re-collapsed result (status row) | match | match |
| successful recovery result | match | match |
| final response and idle state | match | match |
| same-Pi calibration | 0 differences | 0 differences |

Raw render-stream differences are limited to dynamic usage counters
(`0.7%`/`1.8%`), which the rubric normalisation maps to `<usage>`; the scored
text, layout and style comparison has no differences.

## Scores

| Metric | Before | After |
|---|---:|---:|
| Progress-event parity | 10/10 | 10/10 |
| Visual parity | 1/10 | **10/10** |
| Deterministic UI total | 11/20 | **20/20** |
| Pi calibration | 20/20 | 20/20 |

The scoring rubric, weights and normalisation are unchanged.

## Remaining differences

No scored difference remains for collapsed, expanded or re-collapsed Bash
results at either size.

- The ENV-PARSER-001 P2 (`ui-trailing-wrapped-command-style`, 17 trailing
  blank cells of a wrapped Bash command line) is **out of scope** here because
  it concerns command-line style padding rather than result collapse. It is
  unchanged and still tracked.
- Raw comparison counters for usage percentages are dynamic values already
  covered by the documented normalisation, not presentation differences.

## Verification

- `dotnet test PiSharp.slnx --no-restore` — see the PR description for the exact
  total.
- Focused Bash collapse regressions — 12 passed.
- Existing Bash tool presentation, transcript, compositor and timer tests —
  passed.
- `python3 -m unittest discover -s tools/parity/terminal -p 'test_*.py'` — passed.
- `python3 -m unittest tools/parity/objective_agent_parity/test_objective_agent_parity.py` — 8 passed.
- `dotnet build PiSharp.slnx --no-restore --warnaserror` — clean.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore` — clean.
- `python3 tools/validate_doc_paths.py` — see the PR description.
- `objective-agent-progress` still scores 20/20, confirming no regression in the
  ENV-PARSER-001 terminal evidence.

## Evidence

- [UI report](ui-report.json)
- [Terminal comparison summary](terminal-comparison.json)
- [Fixture baseline](fixture-baseline.json)
- Raw artifacts: `artifacts/parity/collapsed-tool-output-ui-20261106/` (Git
  ignored).

## Next single deliverable

Correct the ENV-PARSER-001 P2 trailing styled cells on the wrapped Bash command
line and verify `objective-agent-progress` at 80×24 while keeping 120×40 and this
milestone's recovery checkpoints at full parity.
