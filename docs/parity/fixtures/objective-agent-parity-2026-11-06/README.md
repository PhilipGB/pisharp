# Objective agent parity — ENV-PARSER-001 — 2026-11-06

This receipt records the fixed coding fixture, the private evaluator, five
real-model runs per application, a deterministic provider/tool contract and a
deterministic terminal comparison. Raw traces remain under
`artifacts/parity/objective-agent-parity-20261106/` and
`artifacts/parity/objective-agent-ui-20261106/` (Git ignored).

## Configuration

| Fact | Value |
|---|---|
| Fixture | `ENV-PARSER-001` |
| Fixture checksum | `e90c4e5c49d063f6ce0e4ce6664295aaed01145d70faabe1d461fdae2cd4da0f` |
| Baseline result | build succeeds; 3 tests, 2 pass, 1 intended failure (`ValuesContainingAdditionalEqualsSignsArePreserved`) |
| Pi revision | `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (`/home/philip/Projects/pi-pinned`, clean) |
| PiSharp revision | `504be244dc36b29b0ffc70fe0892d4dcdd485bc3` plus this uncommitted milestone tree |
| Pi provider/model/effort | `openai` / `gpt-6-luna` / `xhigh` |
| PiSharp provider/model/effort | `openai-codex` / `gpt-6-luna` / `xhigh` |
| Tools | `read`, `bash`, `edit`, `write`, `grep`, `find`, `ls` |
| Workspace | `/home/philip/.cache/tmp/opencode/objective-agent-parity/ENV-PARSER-001` |
| Limit | 10 minutes wall clock, monotonic; 605 s CPU; 8 GiB address space; 4096 open files |

Both applications used their existing OpenAI subscription credentials. The
provider identifiers differ because Pi's configuration exposes the subscription
through `openai` and PiSharp's through `openai-codex`; the model identifier and
effort are identical. Credentials were never copied into artifacts.

## Real-model results

| Run | Status | Duration | Functional | Agent/tool | Total | Complete |
|---|---|---:|---:|---:|---:|---|
| Pi 01 | completed | 36.5 s | 50 | 30 | 80 | yes |
| Pi 02 | completed | 33.9 s | 50 | 30 | 80 | yes |
| Pi 03 | completed | 303.2 s | 50 | 30 | 80 | yes |
| Pi 04 | completed | 141.9 s | 50 | 30 | 80 | yes |
| Pi 05 | completed | 154.2 s | 50 | 30 | 80 | yes |
| PiSharp 01 | completed | 116.5 s | 50 | 30 | 80 | yes |
| PiSharp 02 | completed | 101.3 s | 50 | 30 | 80 | yes |
| PiSharp 03 | completed | 94.5 s | 50 | 30 | 80 | yes |
| PiSharp 04 | completed | 119.4 s | 50 | 30 | 80 | yes |
| PiSharp 05 | completed | 86.7 s | 50 | 30 | 80 | yes |

Every run passed H01-H08, all three visible tests and change integrity. The only
changed file in every run was `src/Fixture.Core/EnvFile.cs`. No run was blocked,
timed out or excluded. Three Pi runs and five PiSharp runs recovered from identical
Roslyn/compiler out-of-memory pressure caused by the shared 8 GiB address-space
limit; each recovered and its final `dotnet test` passed. Recovered tool failures
are reported without penalty.

## Deterministic agent-loop contract

`contract-report.json` serves one scripted bash tool call followed by a final
answer to both products. Both pass:

- exactly one tool start and matching end, with the expected output;
- exactly two provider requests, one assistant tool call ID and one matching
  tool result;
- the second request contains the tool result after the tool call and before a
  new assistant turn, with no duplicates;
- the final response and settlement event complete.

## Deterministic terminal result

`ui-report.json` scores the scripted tool lifecycle at 80x24 and 120x40 after a
same-Pi calibration that passed with zero differences.

| Checkpoint | Result |
|---|---|
| Tool execution starts | represented in both |
| Tool progress/output | represented in both |
| Tool completion status | represented in both |
| Assistant resumes | represented in both |
| Final idle state | represented in both |

- Progress parity: **10/10**.
- Visual parity: **9/10**.
  - 80x24: 4/5. Labels/indicators/positions, layout, footer/cursor and final
    state match. Seventeen trailing blank cells of the wrapped command line at
    `tool-complete`, `assistant-resume` and `final-idle` carry Pi's styled
    foreground while PiSharp leaves them unstyled; the visible text is
    identical.
  - 120x40: 5/5, including colours and styles.
- Deterministic UI total (PiSharp parity against Pi): **19/20**. Pi receives the
  calibrated reference score of **20/20**.

## Aggregates

| Metric | Pi | PiSharp | Difference |
|---|---:|---:|---:|
| Mean functional | 50.0 | 50.0 | 0.0 |
| Mean agent/tool | 30.0 | 30.0 | 0.0 |
| Mean real-model | 80.0 | 80.0 | 0.0 |
| Deterministic UI | 20.0 | 19.0 | -1.0 |
| Combined | **100.0** | **99.0** | **-1.0** |
| Complete task success | 5/5 | 5/5 | 0 |

Both results are in the 90-100 band. Pi is the calibrated UI reference and
therefore scores 20/20; PiSharp's measured parity score is 19/20. This five-run
sample is not statistical proof of general model performance.

## Stability and preflight notes

- An initial one-run-per-product harness probe was not scored. Its rate-limit
  classifier matched numeric substrings in timestamps and reported a completed
  Pi run as blocked; the classifier was corrected before the official batch.
  The probe remains under `artifacts/parity/objective-agent-probe` for audit.
- An initial deterministic UI comparison was rejected because the fixture
  asserted the literal duration `Took 0.3s`; the completion check was changed to
  the duration-independent token `Took` before the recorded calibrated
  comparison. No product score depends on the literal duration.
- No official run was retried, removed or replaced. The ten official attempts
  are the complete five-run sample for each product.
- A repeat deterministic UI capture and agent-loop contract both reproduced the
  recorded results (`19/20` and pass).

## Classified differences

| Severity | ID | Finding |
|---|---|---|
| P2 | `ui-colours-80x24-dark-fullscreen` | At 80x24, 17 trailing blank cells of the wrapped bash command line carry Pi's styled foreground while PiSharp leaves them unstyled; visible text, layout, indicators, footer, cursor and final state match. |

No P0 or P1 discrepancy was confirmed. Recovered compiler memory failures are
environment observations, not product-level differences: both applications
recovered, reported the workaround, and produced passing tests.

## Highest-priority confirmed discrepancy and next deliverable

**P2:** extend the bash command-line syntax style across the trailing blank
cells of wrapped command output at 80 columns to match pinned Pi.

Next single deliverable: implement that trailing-cell style parity and verify it
with `tools/parity/terminal/fixtures/objective-agent-progress.json` at 80x24 and
120x40, then rerun this milestone's deterministic UI scoring. Do not change
agent-loop or functional behaviour for this task.

## Verification

`verification.json` reports `valid: true` and nine negative controls rejected:
inflated private score, out-of-range agent score, missing run, wrong fixture
checksum, forged change integrity, inconsistent UI score, false task success,
hidden private failure and wrong run score. All validity gates pass, including
same-Pi calibration, five assessable runs per application, complete traces and
deterministic score calculation.

## Evidence

- [Real-model and aggregate report](report.json)
- [Deterministic UI report](ui-report.json)
- [Deterministic agent-loop contract](contract-report.json)
- [Baseline manifest and checksum](fixture-baseline.json)
- [Report verification and negative controls](verification.json)
- [Evidence index](evidence-index.json)
