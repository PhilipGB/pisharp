# Interactive streaming, reasoning, tool progress and cancellation parity — 2026-11-07

This evaluation scores PiSharp against pinned Pi for partial-text streaming,
reasoning visibility, live tool progress, and two cancellation paths. It is an
evaluation record, not a repair milestone: confirmed P0/P1 behavior is
documented here and intentionally left unfixed.

- Pi reference: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (unchanged)
- Base main: `c615fc17232886d76c5092f8d5b6cd5b22f418fe`
- Branch: `milestone/interactive-streaming-cancellation-parity`
- Fixture: `interactive-streaming-cancellation-001`
- Dimensions: `80x24-dark-fullscreen`, `120x40-dark-fullscreen`
- Providers: deterministic local HTTP fixture only. No live provider, auth,
  llama.cpp, or external network service is used.

The evaluation deliberately does not change product behavior. Every confirmed
difference below has a reproduction command, a decoded terminal path, and a
source location for a follow-up milestone.

## Rubric

| Category | Points | What earns the points |
| --- | ---: | --- |
| Functional | 40 | Every named checkpoint reached; required text and exact-once tokens; stable idle state; expected model-request count; recovery prompt or clean exit. |
| Temporal | 20 | Named checkpoints prove the required order: no late output after cancellation, no stale activity at idle, no duplicated tool call, no missing checkpoint. |
| Visual | 30 | Per size and category: visible text matches, cell styles match, and transcript layout/cursor matches. Each category is one point per size. |
| Cleanup | 10 | Only S04/S05: cancellation settles without `Working`, late tokens never appear, the normal screen/cursor/synchronized-output modes restore, the tool process tree is gone (S05), and a follow-up prompt succeeds. |

Functional, temporal, and visual each contribute across all five scenarios
(8, 4, and 6 points per scenario). Cleanup contributes 5 points to each of S04
and S05. The maximum is therefore 100: `5 × 18 + 2 × 5`.

A scenario whose same-Pi calibration is not exact after the declared
normalization is marked not assessable and contributes no points. The report
still records its nominal maximum and the calibration difference.

## Scenarios

| ID | Scenario | Named checkpoints | Key assertions |
| --- | --- | ---: | --- |
| S01 | Partial assistant-text streaming | 6 + exit | A clause, a two-line Python code block and the final paragraph arrive over three gated chunks; the early checkpoint must not contain later text; the final transcript contains each line exactly once. |
| S02 | Reasoning visibility and streaming | 8 + exit | Reasoning begins, continues, hides, the visible answer begins, reasoning is shown again, and the response completes. Hidden checkpoints must not contain reasoning text; the final transcript must contain each marker once. |
| S03 | Tool execution with live progress | 8 + exit | One Bash tool call reports `TOOL_STARTED`, `PROGRESS_01`, `PROGRESS_02`, `TOOL_COMPLETED`; the tool result is sent back exactly once and the assistant continues. |
| S04 | Cancellation during assistant-text streaming | 8 + exit | Escape cancels a gated stream, `CANCEL_LATE`/`CANCEL_FINISH` never appear, the partial transcript remains, the terminal restores, and a second prompt succeeds. |
| S05 | Cancellation while Bash is running | 10 + exit | Escape cancels a running shell; `TOOL_COMPLETED` never appears; the shell and child PIDs are gone; the terminal restores; a second prompt succeeds. |

The exact checkpoint lists and per-scenario assertions live in
[`scenarios.json`](../../../../tools/parity/interactive_streaming_cancellation/scenarios.json)
and are repeated in [`fixture-manifest.json`](fixture-manifest.json).

## Determinism

Checkpoint scoring uses provider and tool gates, not wall-clock sleeps:

- `chatRequestGates`, `chatResponseGates`, `chatResponseChunkGatesByRequest`,
  `chatResponseReasoningChunkGatesByRequest`, `chatResponseChunkReadyGatesByRequest`,
  and their reasoning ready gates hold and release deterministic chunks.
- S05 records the shell and child PIDs through atomic-looking PID files and
  waits for `/proc` liveness to disappear with a bounded 5 s timeout.
- Named checkpoints inside gated windows use `allowStaticFrame`, so an
  unchanged screen is captured deliberately rather than after a delay.
- Gate waits drain the PTY. The PTY pump drains every byte available in one
  read cycle so a checkpoint cannot be sampled mid-synchronized-output frame.
- Cleanup runs in `finally`: orphan process trees are killed and the PTY is
  closed even when a scenario fails.

Only wall-clock durations and partial-stream usage percentages are masked
before comparison: `Elapsed N.Ns`, `Took N.Ns`, and the numeric percentage
before `/` in the footer. Token counts stay exact, and the mask uses a fixed
cell width so `0.6s` and `0.98s` align without hiding styles or layout.

The same-Pi calibration is exact for S01, S02, S03, S04 and S05 at both sizes
after that normalization. For transparency, the raw harness calibration
reported dynamic-only differences at S03 (`18` at `80x24`, `32` at `120x40`) and
S05 (`28` at `80x24`, `50` at `120x40`) across checkpoint and render frames;
S01, S02 and S04 reported none. The differences were `Elapsed`/`Took` values and
their render duplicates, not product behavior, and all normalize away.

## Capture provenance

Every calibration and comparison capture writes a top-level and per-product
provenance record. The report and fixture manifest bind each case to the exact
Pi and PiSharp `HEAD`, branch, tracked-change list, and `dirty` flag. This bundle
captures both products from a clean tree: Pi at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` and PiSharp at
`8d30011952c3017d210f9a41c8d743ffd5adb3bf`; no implicit working-tree changes are
accepted. The verifier rejects a missing SHA, a non-boolean or contradictory
dirty state, a calibration second run that is not the Pi source, or source drift
between cases.

## Independent product contracts

The frozen functional, temporal and cleanup contract is evaluated separately
for Pi and PiSharp at every case. Pi must pass its own contract before a case is
assessable; PiSharp is then scored against the same assertions. The report
records both contracts, and the verifier recomputes both from checkpoint,
request, process and render-stream evidence. A Pi contract failure makes the
case not assessable rather than awarding parity points.

## Post-cancellation render stream

S04 and S05 declare forbidden tokens and a cancellation action. The runner scans
every completed render frame after that action, including viewport and
alternate/normal buffers, and records frame-level occurrence counts. These
tokens are not inferred from sparse named checkpoints: `CANCEL_LATE` /
`CANCEL_FINISH` for S04 and `TOOL_COMPLETED` for S05 must have zero render-frame
occurrences. The verifier rejects missing scan evidence or a stream occurrence
that is scored as clean.

## Finding predicates

Every decoded difference must match an explicit scenario/checkpoint/path/value
predicate. There is no scenario-wide catch-all mapping. The predicates are
listed in `evaluate.py`; unmatched differences are retained in the report and
make validation fail. The verifier also re-runs the predicate on every
comparison sample and rejects an unknown or mismatched finding id.

## Results

| Scenario | Functional | Temporal | Visual | Cleanup | Subtotal |
| --- | ---: | ---: | ---: | ---: | ---: |
| S01 text streaming | 8/8 | 4/4 | 4/6 | — | 16/18 |
| S02 reasoning | 8/8 | 4/4 | 0/6 | — | 12/18 |
| S03 tool progress | 8/8 | 4/4 | 6/6 | — | 18/18 |
| S04 text cancellation | 8/8 | 4/4 | 6/6 | 5/5 | 23/23 |
| S05 tool cancellation | 8/8 | 4/4 | 2/6 | 5/5 | 19/23 |
| **Total** | **40/40** | **20/20** | **18/30** | **10/10** | **88/100** |

All five scenarios were assessable at both sizes. Functional and temporal
behavior matched: every checkpoint was reached, required text and exact-once
tokens matched, tool calls/results were sent exactly once, idle had no stale
`Working` indicator, cancellation produced no late output, the S05 process
tree was gone, and both cancellation scenarios recovered for a second prompt.
The lost visual points are the documented differences below.

Difference counts (decoded cell paths, after normalization):

| Scenario | 80x24 text/style/layout | 120x40 text/style/layout |
| --- | ---: | ---: |
| S01 | 0 / 140 / 0 | 0 / 140 / 0 |
| S02 | 3780 / 9180 / 2 | 4286 / 10622 / 2 |
| S03 | 0 / 0 / 0 | 0 / 0 / 0 |
| S04 | 0 / 0 / 0 | 0 / 0 / 0 |
| S05 | 420 / 448 / 0 | 420 / 448 / 0 |

S02's large counts are expected: a three-row transcript offset shifts the
characters and styles of most populated rows at every checkpoint after the
reasoning block appears. The finding is one layout defect, not 25,000
independent defects.

## Confirmed differences

No P0 difference was observed. Every difference is classified below; there are
no `Inconclusive` differences in this run.

### S02-reasoning-block-layout — P1

- **Scenario/checkpoints:** S02, `s02-02-reasoning-begins` through `exit`, both sizes.
- **Pi observation:** Pi keeps one reasoning section, renders one `Thinking...`
  placeholder while hidden, and restores the joined reasoning block without
  extra rows.
- **PiSharp observation:** PiSharp splits streamed reasoning around toggle
  status lines, shows two `Thinking...` placeholders, and leaves extra
  blank/indented rows. The final transcript is shifted by three rows; the exit
  cursor is three rows lower at both sizes.
- **Source:** `src/PiSharp.Cli/Tui/Rendering/TerminalConversationPresentation.cs`.
- **Correction:** Collapse streamed reasoning into one block, keep a single
  hidden placeholder, and re-render the block in place when visibility changes.
- **Evidence:** `comparison.json` S02 text/layout differences from
  `s02-02-reasoning-begins` through `exit`; [`ui-report.json`](ui-report.json)
  records the visible checkpoint text for both products.

### S01-code-syntax-builtin-highlight — P2

- **Scenario/checkpoints:** S01, `s01-04-incomplete-code-block` through `exit`, both sizes.
- **Pi observation:** Pi colours the Python builtin name `print` with the syntax
  keyword colour `rgb(10983639)`.
- **PiSharp observation:** PiSharp leaves `print` at the default foreground;
  string literals and the rest of the code block match.
- **Source:** `src/PiSharp.Cli/Tui/Rendering/TerminalSyntaxHighlighter.cs`.
- **Correction:** Add Python builtin-name highlighting to the syntax token table
  and cover it with a cell-style regression.
- **Evidence:** `comparison.json` S01 style cells; 140 differences per size.

### S02-reasoning-italic-style — P2

- **Scenario/checkpoints:** S02, `s02-02-reasoning-begins` through `exit`, both sizes.
- **Pi observation:** Pi renders reasoning text with the italic attribute.
- **PiSharp observation:** PiSharp renders reasoning text without italic.
- **Source:** `src/PiSharp.Cli/Tui/Rendering/TerminalConversationPresentation.cs`.
- **Correction:** Apply the reasoning/thinking italic style to every reasoning
  cell and add a styled-frame regression.
- **Evidence:** `comparison.json` S02 `.attributes.italic` differences at every
  visible reasoning checkpoint.

### S02-reasoning-status-style — P2

- **Scenario/checkpoint:** S02, `s02-01-before-reasoning`, both sizes.
- **Pi observation:** For a reasoning-capable model Pi draws the `Working` rule
  with `rgb(6391244)`.
- **PiSharp observation:** PiSharp keeps the non-reasoning `Working` rule colour
  `rgb(7108219)`.
- **Source:** `src/PiSharp.Cli/Tui/Rendering/TerminalScreen.cs`.
- **Correction:** Select the reasoning-model `Working` status colour and pin it
  in a status-style test.
- **Evidence:** `comparison.json` S02 Working-rule foreground cells at the first
  checkpoint.

### S05-tool-cancel-error-wording — P2

- **Scenario/checkpoints:** S05, `s05-05-tool-cancellation-settles` through
  `exit`, both sizes.
- **Pi observation:** Pi prints the tool-level `Command aborted` row and then
  `Error: This operation was aborted`.
- **PiSharp observation:** PiSharp prints the same `Command aborted` row but the
  following error line is `Operation aborted`.
- **Source:** `src/PiSharp.Cli/Tui/Rendering/TerminalConversationPresentation.cs`.
- **Correction:** Render the cancelled tool error with Pi's `Error:` prefix and
  abort wording; add a checkpoint assertion.
- **Evidence:** `comparison.json` S05 text differences from
  `s05-05-tool-cancellation-settles` through `exit`.

### S05-tool-cancel-error-style — P2

- **Scenario/checkpoints:** S05, `s05-05-tool-cancellation-settles` through
  `exit`, both sizes.
- **Pi observation:** Pi applies the tool error styling to the
  `Error: This operation was aborted` row.
- **PiSharp observation:** PiSharp applies error styling to `Operation aborted`,
  but the text span and colour runs differ with the wording.
- **Source:** `src/PiSharp.Cli/Tui/Rendering/TerminalConversationPresentation.cs`.
- **Correction:** Render the corrected cancellation error through the same
  tool-error style path as Pi.
- **Evidence:** `comparison.json` S05 style differences from
  `s05-05-tool-cancellation-settles` through `exit`.

## Reproduction

Build PiSharp from `src/PiSharp.Cli/PiSharp.Cli.csproj` and check out the
pinned Pi reference first. The harness defaults to the built PiSharp DLL under
the project's `bin/Debug/net10.0` output and the pinned Pi checkout at
`/tmp/pisharp-current-pi`; substitute your paths.

```sh
# Same-Pi calibration, one scenario (repeat for S01..S05)
python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi \
  --fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s01-text-streaming.json \
  --calibrate \
  --output artifacts/parity/interactive-streaming-cancellation-001/calibration/s01-text-streaming

# Pi vs PiSharp comparison, same scenario
python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi \
  --pisharp src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll \
  --fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s01-text-streaming.json \
  --output artifacts/parity/interactive-streaming-cancellation-001/comparison/s01-text-streaming

# Score all captured evidence and write the committed bundle
python3 tools/parity/interactive_streaming_cancellation/run_evaluation.py \
  --artifacts artifacts/parity/interactive-streaming-cancellation-001 \
  --evidence docs/parity/fixtures/interactive-streaming-cancellation-001

# Verify the committed bundle, including its own checksums
python3 tools/parity/interactive_streaming_cancellation/verify_report.py \
  --evidence docs/parity/fixtures/interactive-streaming-cancellation-001 \
  --require-verification
```

One finding can be reproduced at a single size with `--case`, for example:

```sh
python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi \
  --pisharp src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll \
  --fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s05-tool-cancellation.json \
  --case 80x24-dark-fullscreen \
  --output artifacts/parity/repro-s05
```

## Verification

- Evaluator and verifier unit tests — 27 passed:
  `python3 -m unittest discover -s tools/parity/interactive_streaming_cancellation -p 'test_*.py'`
- Report verifier — 586 checks, 0 failed, including fixture checksums,
  capture provenance for every case, independently recomputed Pi and PiSharp
  contracts, post-cancellation render-stream evidence, recomputed scores and
  totals, cancellation cleanup evidence and finding coverage.
- Report generation is deterministic: repeated runs over the same assertions
  produce byte-identical `report.json`, `comparison.json` and `ui-report.json`.
- Terminal Python suite and the focused .NET terminal/lifecycle suites are
  recorded on the draft PR.
- `dotnet build PiSharp.slnx --warnaserror` and
  `dotnet format PiSharp.slnx --verify-no-changes` are recorded on the draft PR.

The verifier rejects a report with a missing scenario or checkpoint, a bad
fixture or evidence checksum, an inflated scenario score or total, an
unassessable/invalid calibration treated as assessable, a failed cancellation
marked successful, missing cleanup evidence, or a difference omitted from the
finding set. It also rejects a missing or dirty/mismatched capture SHA, a Pi
contract failure marked assessable, missing post-cancellation render-stream scan
evidence or a forbidden token marked clean, and an unmatched difference with no
explicit finding predicate. Negative controls for each case live in
`test_evaluation.py`.

## Evidence files

- [`report.json`](report.json) — the scored report: capture provenance,
  independent Pi/PiSharp contracts, post-cancellation stream scans, calibration,
  checkpoint assertions, findings and fixture checksums.
- [`comparison.json`](comparison.json) — per-scenario, per-size decoded
  difference counts, category totals and bounded samples.
- [`ui-report.json`](ui-report.json) — normalized visible checkpoint text for
  Pi and PiSharp, plus the per-size visual scores.
- [`verification.json`](verification.json) — verifier results and checksums of
  the three evidence files.
- [`fixture-manifest.json`](fixture-manifest.json) — pinned revisions, fixture
  checksums, dimensions, capture commands and normalization policy.

Raw PTY streams, render streams and full assertions remain under
`artifacts/parity/interactive-streaming-cancellation-001/`, which is Git
ignored. They are needed to regenerate the report but are not part of the
committed evidence.

## Limitations

- Only two sizes, one theme and fullscreen mode are covered.
- No live model, authentication, llama.cpp or external network path is used.
- Images, wide glyphs and terminal-profile-specific rendering are out of scope.
- The evaluation records differences; it does not fix them.
- S02's layout difference is scored at P1 because it changes reasoning
  visibility, transcript position and exit cursor placement across the run.
- The historical ENV-PARSER-001/002 fixtures and evidence are unchanged.
