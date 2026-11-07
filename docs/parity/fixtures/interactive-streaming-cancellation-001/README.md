# Interactive streaming, reasoning, tool progress and cancellation parity — 2026-11-07

This evaluation scores PiSharp against pinned Pi for partial-text streaming,
reasoning visibility, live tool progress, and two cancellation paths. The
baseline evaluation exposed six visual differences; this bundle records the
repair of all six and a clean re-run through the same artifact-bound capture
pipeline.

- Pi reference: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (unchanged)
- Base main: `03bfa4e87ebcac597fe89f1ac7506abf02aa2385` (PR #47 merge)
- Branch: `milestone/interactive-visual-parity`
- Capture source: `2fe9c89a0f09fb7318701219ff993677a9b7c919`
- Fixture: `interactive-streaming-cancellation-001`
- Dimensions: `80x24-dark-fullscreen`, `120x40-dark-fullscreen`
- Providers: deterministic local HTTP fixture only. No live provider, auth,
  llama.cpp, or external network service is used.

The committed report is **100/100**: functional **40/40**, temporal **20/20**,
visual **30/30** and cleanup **10/10**. Every Pi/PiSharp checkpoint has zero text,
style and layout differences after the permitted normalization. There are no
remaining findings.

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
after that normalization. The final bundle was rerun after two unrelated
harness races, without changing any assertion or timeout:

- an S04 `80x24` second-prompt capture sampled the previous `Working` row before
  the idle repaint;
- an S05 calibration sampled a transient process scheduler state and one
  pre-teardown border frame.

Both reruns reproduce the declared Pi behaviour and the committed bundle
contains the clean reruns. No product difference was hidden by these reruns.

## Capture provenance

Every calibration and comparison capture writes a top-level and per-product
provenance record. The report and fixture manifest bind each case to the exact
Pi and PiSharp `HEAD`, branch, tracked-change list, and `dirty` flag. This bundle
captures both products from a clean tree: Pi at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` and PiSharp at
`2fe9c89a0f09fb7318701219ff993677a9b7c919`; no implicit working-tree changes are
accepted. The verifier rejects a missing SHA, a non-boolean or contradictory
dirty state, a calibration second run that is not the Pi source, or source drift
between cases. Strict worktree status includes non-ignored untracked files, so an
untracked `.cs` file or build props file makes the capture dirty.

The executed PiSharp binary is bound separately. `capture.py` records the clean
source SHA, publishes into the capture-owned `pisharp-artifact/` directory,
rechecks the SHA and worktree, and hashes the DLL, `.deps.json` and
`.runtimeconfig.json`. This bundle records DLL
`b7e9ee318c252afedecb189128e5462a391475a7384c0500c2f28de45073aebc`, deps
`15f802985500dc4441f3b417a50188f35bd3aba8c5463da3b506b4ebc914f8af` and runtime
config `f9bc8e9f0c46247078ed47309f19a7576b4b94e4300e9de0003917d6bd2d667a`.
Every PiSharp case must use the same artifact, and `builtFromSha` must equal the
source SHA.

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
comparison sample and rejects an unknown or mismatched finding id. The final
bundle contains no differences, so no predicate matches and the findings list
is empty. The former predicates remain in place and would classify a regression
on the same paths.

## Results

| Scenario | Functional | Temporal | Visual | Cleanup | Subtotal |
| --- | ---: | ---: | ---: | ---: | ---: |
| S01 text streaming | 8/8 | 4/4 | 6/6 | — | 18/18 |
| S02 reasoning | 8/8 | 4/4 | 6/6 | — | 18/18 |
| S03 tool progress | 8/8 | 4/4 | 6/6 | — | 18/18 |
| S04 text cancellation | 8/8 | 4/4 | 6/6 | 5/5 | 23/23 |
| S05 tool cancellation | 8/8 | 4/4 | 6/6 | 5/5 | 23/23 |
| **Total** | **40/40** | **20/20** | **30/30** | **10/10** | **100/100** |

All five scenarios were assessable at both sizes. Functional and temporal
behavior matched: every checkpoint was reached, required text and exact-once
tokens matched, tool calls/results were sent exactly once, idle had no stale
`Working` indicator, cancellation produced no late output, the S05 process
tree was gone, and both cancellation scenarios recovered for a second prompt.

Difference counts (decoded cell paths, after normalization):

| Scenario | 80x24 text/style/layout | 120x40 text/style/layout |
| --- | ---: | ---: |
| S01 | 0 / 0 / 0 | 0 / 0 / 0 |
| S02 | 0 / 0 / 0 | 0 / 0 / 0 |
| S03 | 0 / 0 / 0 | 0 / 0 / 0 |
| S04 | 0 / 0 / 0 | 0 / 0 / 0 |
| S05 | 0 / 0 / 0 | 0 / 0 / 0 |

## Corrected visual findings

The baseline bundle scored 18/30 visual. The following corrections are now
independently verified.

| Finding | Pi contract | Correction | Evidence |
| --- | --- | --- | --- |
| S02 reasoning layout (P1) | One logical reasoning block, one `Thinking...` placeholder, stable status ordering and the same final transcript/cursor as Pi. | Streamed reasoning chunks stay in one live assistant block. Status rows appended while it is live stay after that block, and committed assistant segments are inserted ahead of those trailing status rows. | S02 matches at every checkpoint; `ReasoningStreamingLayoutTests` covers both sizes, hidden/visible toggles, resize, exact rows and cursor. |
| S02 reasoning italic (P2) | Visible reasoning and the hidden placeholder use `thinkingText` plus italic; the assistant answer is not italic. | The baseline rows were displaced, so the evaluator attributed already-italic cells to the wrong row. The production style was correct; a dedicated SGR regression now pins it. | `VisibleReasoningUsesPiItalicThinkingStyle` covers first line, wrapped continuation, hidden placeholder, restored reasoning and the unstyled answer. |
| S02 Working-rule colour (P2) | The editor border and `Working` indicator use the active thinking level token; a reasoning model at `medium` uses `thinkingMedium` (`rgb(6391244)`). | `TerminalScreen` resolves the live model thinking level, and the compositor selects the matching theme token for both border rows and the working indicator. Non-reasoning/off behaviour stays `thinkingOff`. | `WorkingRuleUsesPiThinkingLevelColor` covers `medium`, `high` and `off`; S02 status rows match at both sizes. |
| S01 Python builtin colour (P2) | Pi inherits highlight.js `built_in` classification, so `print`, `len` and `str` use `syntaxType`; user names and strings keep their own styles. | Added the highlight.js Python builtin set to the existing token table, ahead of generic keyword handling. | `PythonBuiltinsUsePiSyntaxTypeColorWithoutCapturingUserNamesOrStrings` covers multiple builtins, numbers, a string literal and ordinary identifiers; S01 matches at both sizes. |
| S05 cancellation wording (P2) | A cancelled builtin tool ends with the tool result `Command aborted` and then `Error: This operation was aborted`; text-only cancellation keeps `Operation aborted`. | The terminal presenter tracks an aborted builtin tool in the current turn and renders Pi's wording through the existing error transcript path. | `ToolCancellationPresentationTests` covers cancelled Bash, successful Bash, ordinary failure and text-only cancellation; S05 matches at both sizes. |
| S05 cancellation style (P2) | The cancellation error row uses the existing `error` token across the corrected text span. | No separate renderer was added: the corrected wording uses the same error line path, so the text span and style run now match Pi. | S05 has zero style differences at every checkpoint after `s05-05-tool-cancellation-settles`. |

A separate cross-component regression,
`ReasoningPythonBuiltinsAndToolCancellationComposeInOneTranscript`, runs
reasoning, a reasoning toggle, a Python code block with builtins, a cancelled
Bash tool and idle in one transcript at both widths. It checks ordering,
single-instance markers and the reasoning/syntax/error styles together.

## Reproduction

The reproducible path is `capture.py`. It verifies the source tree is clean
(including non-ignored untracked files), records the source SHA, publishes
PiSharp into the capture-owned `pisharp-artifact/` directory, rechecks the SHA
and worktree, hashes the DLL/deps/runtime config, and runs all calibration and
comparison cases against that artifact. The pinned Pi checkout is
`/tmp/pisharp-current-pi`; substitute your paths.

```sh
# Fresh publish plus all calibration/comparison cases
python3 tools/parity/interactive_streaming_cancellation/capture.py \
  --pi /tmp/pisharp-current-pi \
  --output artifacts/parity/interactive-streaming-cancellation-001

# Score all captured evidence and write the committed bundle
python3 tools/parity/interactive_streaming_cancellation/run_evaluation.py \
  --artifacts artifacts/parity/interactive-streaming-cancellation-001 \
  --evidence docs/parity/fixtures/interactive-streaming-cancellation-001

# Verify the committed bundle, including its own checksums
python3 tools/parity/interactive_streaming_cancellation/verify_report.py \
  --evidence docs/parity/fixtures/interactive-streaming-cancellation-001 \
  --require-verification
```

A single scenario/case can be reproduced with `--case`, for example:

```sh
python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi \
  --pisharp artifacts/parity/interactive-streaming-cancellation-001/pisharp-artifact/PiSharp.Cli.dll \
  --fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s05-tool-cancellation.json \
  --case 80x24-dark-fullscreen \
  --output artifacts/parity/repro-s05
```

Raw PTY evidence remains under the Git-ignored
`artifacts/parity/interactive-streaming-cancellation-001/` directory. The
committed evidence is [`report.json`](report.json),
[`comparison.json`](comparison.json), [`ui-report.json`](ui-report.json),
[`verification.json`](verification.json) and
[`fixture-manifest.json`](fixture-manifest.json).
