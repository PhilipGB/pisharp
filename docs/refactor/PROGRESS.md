# Architecture refactor progress

- **Baseline PiSharp:** `064bcc7b90344cffde595f9aca1c5b850cefe567` (untouched main branch point).
- **Frozen Pi:** `ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`; authority: execution-ledger source note, corroborated by latest TODO/trust-startup evidence.
- **Branch:** `refactor/architecture-maintainability`.
- **Phase:** Stage 1 complete; this is Commit 0. Stage 2 starts only after this checkpoint is confirmed pushed.
- **Pilot:** coding tools, frozen hotspot H09. See `baseline/pilot-decision.json`.
- **Completed slices:** untouched behavioral baseline and architectural inventory only; no product/test source changed.
- **Active slice:** permanent Pi-reference mechanism and its neutrality comparison.
- **Latest observed main:** `064bcc7b90344cffde595f9aca1c5b850cefe567`; no divergence or merge conflicts at the session-start/checkpoint queries.
- **Blockers:** none. Push failure is a durability blocker; stop before Stage 2 if this commit is not remote.

## Verification and baseline state

Two passes: format/build pass; .NET 1,356/1,356, zero skips; Node 24/24;
Python 38/38; official MCP 261 passing checks plus eight classified failures,
no regressions. All 17 process differential drivers and six virtual-routing
variants are attempted twice at rebuilt frozen Pi. The old paid-classifier probe
has invalid boolean criteria and is classified baseline-unrunnable. All 33
terminal fixtures cover 68 declared matrix cases per pass, both calibration and
paired. All 13 applicable semantic assertion invocations pass. The stable index
has 1,858 identities (1,353 .NET display groups preserve all 1,356 data rows).
Four terminal cases are baseline-flaky; exact outcome sets are retained. Known
regular-mode/presentation gaps and the malformed Markdown fixture remain.

Optional full coverage collection caused Coverlet tracker shutdown errors in
child processes; raw coverage/diagnostics remain, while two uninstrumented full
runs establish ordinary test outcomes. Incorrect extension DLL/mode invocations
were corrected without product/test/fixture changes. Every process probe was
repeated after locked Pi installation/offline build to rule out stale dist.

`test.pdf`: SHA-256 `2ffe89f6c60efbb5b47bc0dc541ef0f27a1911591e7b92a42dcadb713a7dfb1b`.
No tracked code/test/fixture/script depends on it; no Phase F copy is needed.
All 1,749 starting untracked user files passed integrity checks before Commit 0;
only the explicitly required AGENTS pointer is now appended.

## Frozen hotspots

All remain OPEN; do not re-rank or replace them.

| ID | File/type | Capability |
|---|---|---|
| H01 | Program.cs | Composition/startup |
| H02 | TerminalScreen | Terminal rendering |
| H03 | TerminalEditor | Terminal interaction |
| H04 | ConversationRun | Agent execution |
| H05 | ConversationSession | Sessions |
| H06 | PiJsonlSessionInterchange | Session interchange |
| H07 | PiAgent | Agent execution |
| H08 | ProviderModelRuntime | Provider selection |
| H09 | CodingTools | Coding tools |
| H10 | McpRuntime | MCP |

## Decisions and evidence locations

Keep the installed MAF 1.21.0/.NET 10 contracts. Preserve documented native
extension APIs/assembly identity; there is no evidenced NuGet/tool publication.
Use capability owners inside existing projects for the pilot; evaluate its
pattern before proposing meaningful project boundaries. Keep Core pure and MAF
execution direct; retain Pi-specific semantics. No new dependency lockfiles.

- `baseline/README.md`: capture scope, corrections, limitations and data layout.
- `baseline/source-selection.json`, `capture-metadata.json`, per-run invocation files: source/runner metadata.
- `baseline/run-1/`, `run-2/`: raw TRX/TAP/unittest/conformance reports, terminal comparison and compact assertion captures.
- `baseline/semantic-baseline.json`: authoritative identities/outcome sets; empty logical/invocation mappings alongside it.
- `baseline/architecture.json`, `candidate-ranking.json`, `frozen-hotspots.json`: untouched-source inventory/selection/coverage.
- `baseline/audits.json`, `compatibility-protection.json`, `failure-classifications.json`: consumers, dependencies, compatibility and failures.
- `tools/`: standalone capture/parser/comparison/metrics tooling; parser dependencies only in `/tmp`.

## Exact next action

Confirm Commit 0 exists on `origin/refactor/architecture-maintainability`, then
implement one permanent Pi SHA configuration and checkout-verification/acquisition
entry point. Wire every live Pi differential/terminal/probe entry point to reject
mismatched (or tracked-dirty) checkouts; preserve historical evidence pins.
Document intentional bump procedure under `docs/parity/`. Re-run the same logical
baseline scope, compare against `baseline/semantic-baseline.json` using the
preserved parser, and save pin-neutrality results. Commit/push this as Commit 1
only after neutrality is demonstrated. Then characterize and refactor H09 pilot.

## Exact temporary AGENTS pointer

The exact append block, including its leading blank-line and terminating newline delimiters,
is stored in `baseline/temporary-agents-pointer.json` as `exactAppendBlock`:

```text

## Active architecture refactor

Read `docs/refactor/GOAL.md` and `docs/refactor/PROGRESS.md` before refactor work.
Continue the recorded next action on the dedicated branch; preserve the frozen baselines.

```

This block must be removed exactly during Phase E, after the mandatory remote
preservation-ref audit. Section 23 of GOAL.md is the only finalization sequence.
