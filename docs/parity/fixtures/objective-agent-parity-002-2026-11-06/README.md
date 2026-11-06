# Objective agent parity — ENV-PARSER-002 — 2026-11-06

This milestone extends the merged ENV-PARSER-001 evaluation (PR #43) to a
multi-step repair with two sequential production defects. It reuses the same
runner, evaluator, report verifier, terminal harness and scoring rubric.

## Prerequisite and configuration

| Fact | Value |
|---|---|
| Prerequisite | ENV-PARSER-001 evaluation merged via PR #43 |
| Fixture | `ENV-PARSER-002` |
| Fixture checksum | `173f5b1318d650cee247138f44117821b3e2556c4b9ca2c0121226ac7a92cd8b` |
| Initial baseline | build succeeds; one visible integration test fails at the parsing assertion |
| After Defect A only | the same test fails at the interpolation assertion |
| After both defects | the visible test passes |
| Pi revision | `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (`/home/philip/Projects/pi-pinned`, clean) |
| PiSharp revision | `39abb9accc143312f60333b7b0df19c7977bc108` plus this milestone tree |
| Pi provider/model/effort | `openai` / `gpt-6-luna` / `xhigh` |
| PiSharp provider/model/effort | `openai-codex` / `gpt-6-luna` / `xhigh` |
| Tools | `read`, `bash`, `edit`, `write`, `grep`, `find`, `ls` |
| Workspace | `/home/philip/.cache/tmp/opencode/objective-agent-parity/ENV-PARSER-002` |
| Limits | 600 s monotonic wall clock, 605 s CPU, 4096 open files, no artificial address-space ceiling |

The environment correction removes the 8 GiB virtual-address cap. No run
reported out-of-memory errors. Observed peak process-tree RSS was 674 MiB for Pi
and 609 MiB for PiSharp across the ten runs. Peak values are recorded per run in
`report.json`.

## Sequential-defect validation

`baseline-002.json` records the verified sequence:

| State | Visible test | Failure location |
|---|---|---|
| Initial | failed | `KeyNotFoundException` for the embedded-equals assignment |
| Defect A corrected | failed | `Assert.Equal` mismatch: unknown `${MISSING}` became empty |
| Both corrected | passed | 1 passed, 0 failed |

## Real-model results

| Run | Status | Duration | Functional | Agent/tool | Total | Complete | Peak RSS |
|---|---|---:|---:|---:|---:|---|---:|
| Pi 01 | completed | 45.7 s | 50 | 30 | 80 | yes | 638 MiB |
| Pi 02 | completed | 36.0 s | 50 | 30 | 80 | yes | 633 MiB |
| Pi 03 | completed | 38.0 s | 50 | 30 | 80 | yes | 631 MiB |
| Pi 04 | completed | 38.2 s | 50 | 30 | 80 | yes | 674 MiB |
| Pi 05 | completed | 47.3 s | 50 | 30 | 80 | yes | 639 MiB |
| PiSharp 01 | completed | 38.2 s | 50 | 30 | 80 | yes | 609 MiB |
| PiSharp 02 | completed | 45.4 s | 50 | 30 | 80 | yes | 602 MiB |
| PiSharp 03 | completed | 26.5 s | 50 | 30 | 80 | yes | 605 MiB |
| PiSharp 04 | completed | 34.9 s | 50 | 30 | 80 | yes | 600 MiB |
| PiSharp 05 | completed | 29.1 s | 50 | 30 | 80 | yes | 605 MiB |

All runs passed H01-H08, the visible integration test and change integrity. Every
run changed only `src/Fixture.Core/EnvFile.cs` and
`src/Fixture.Core/EnvInterpolator.cs`. No run was blocked, timed out or excluded.

## Recovery measurements

| Measurement | Pi | PiSharp |
|---|---:|---:|
| Tool errors | 3 | 3 |
| Recovered tool errors | 3 | 3 |
| Test executions | 8 | 8 |
| Errors conveyed to model | 3 | 3 |
| Duplicate side effects | 0 | 0 |

Both products followed the same recovery pattern: failing test, source
correction, second failing test, second correction, passing test. Runs without an
intermediate failure received full credit because the final outcomes and
continuation were correct.

## Deterministic agent-loop contract

`contract-report.json` scripts ten provider requests: discovery of both source
files, a failing test, a deliberately failing missing-file read, successful
recovery read, two production edits, a second failing test, a final passing test
and the final response. Both products pass:

- expected nine tool calls and error states;
- systematically increasing provider-request result counts `0..9`;
- all nine tool results present exactly once in the final request;
- the failing read result conveyed to the model;
- both production files changed and the visible suite passing;
- no duplicate execution IDs or side effects.

## Deterministic terminal result

`ui-report.json` uses the `objective-agent-recovery` fixture at 80x24 and 120x40.
The stable named viewport checkpoints pass same-Pi calibration. Full alternate-
buffer scrollback scheduling differed between two Pi executions and is retained
raw but not scored; the calibration note records this explicitly.

- Progress parity: **10/10**. Both represent tool start, failed test output,
  failure status, successful test output and final idle state.
- Visual parity: **1/10**.
  - 80x24: 1/5. Footer, cursor and final terminal state match; text positions,
    layout, colours/styles differ.
  - 120x40: 0/5. The same differences are present.
- Deterministic UI total: **11/20**. Pi receives the calibrated reference 20/20.

## Confirmed discrepancies

| Severity | ID | Finding |
|---|---|---|
| P1 | `ui-text-80x24-dark-fullscreen` | PiSharp collapses failed/recovered tool output at a different threshold and omits Pi's `ctrl+o to expand` hint, merging hidden content into the hint line and shifting stable text positions. |
| P1 | `ui-text-120x40-dark-fullscreen` | Same collapse/hint and position difference at 120x40. |
| P2 | `ui-colours-80x24-dark-fullscreen` | Decoded cell styles differ at the recovery checkpoints. |
| P2 | `ui-colours-120x40-dark-fullscreen` | Decoded cell styles differ at the recovery checkpoints. |
| P2 (tracked) | `ui-trailing-wrapped-command-style` | Prior ENV-PARSER-001 finding carried forward: 17 trailing blank cells of a wrapped bash command line are styled in Pi and unstyled in PiSharp. Not fixed in this milestone. |

No P0 defect and no functional or agent-loop discrepancy was confirmed.

## Aggregates

| Metric | Pi | PiSharp | Difference |
|---|---:|---:|---:|
| Mean functional | 50.0 | 50.0 | 0.0 |
| Mean agent/tool | 30.0 | 30.0 | 0.0 |
| Mean real-model | 80.0 | 80.0 | 0.0 |
| Deterministic UI | 20.0 | 11.0 | -9.0 |
| Combined | **100.0** | **91.0** | **-9.0** |
| Complete task success | 5/5 | 5/5 | 0 |

Both products completed the multi-step coding task reliably. PiSharp's remaining
gap is terminal presentation of collapsed failed/recovered tool output.

## Highest-priority confirmed discrepancy and next deliverable

**P1:** match Pi's collapsed tool-output threshold and `ctrl+o to expand` hint in
failed/recovered tool blocks so hidden content and following rows occupy the same
positions.

Next single deliverable: implement that terminal parity fix and verify it with
`tools/parity/terminal/fixtures/objective-agent-recovery.json` at 80x24 and
120x40, then rerun `ui_capture.py --fixture ENV-PARSER-002` and the report
verifier. Do not change agent-loop or functional behaviour.

## Verification

`verification.json` reports `valid: true` and all negative controls rejected:
inflated private score, out-of-range agent score, missing run, wrong fixture
checksum, forged change integrity, inconsistent UI score, false task success,
hidden private failure and wrong run score. Focused Python tests, terminal tests,
the full PiSharp .NET suite, warnings-as-errors build and documentation checks are
recorded in the PR description.

## Evidence

- [Real-model and aggregate report](report.json)
- [Deterministic UI report](ui-report.json)
- [Deterministic agent-loop contract](contract-report.json)
- [Baseline manifest and sequential validation](fixture-baseline.json)
- [Report verification and negative controls](verification.json)
- [Evidence index](evidence-index.json)
