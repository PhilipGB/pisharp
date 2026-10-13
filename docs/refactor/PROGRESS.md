# Architecture refactor progress

- **Baseline PiSharp:** `064bcc7b90344cffde595f9aca1c5b850cefe567` (untouched main branch point).
- **Frozen Pi:** `ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`; authority: execution-ledger source note, corroborated by latest TODO/trust-startup evidence.
- **Branch:** `refactor/architecture-maintainability`.
- **Phase:** H01 composition ownership complete and full checkpoint verified; H02 next. Last verified remote: `633c5655bf764bd030116a1501be7ead84591d81`. Earlier checkpoint SHAs are recorded in slice metadata/history.
- **Pilot:** coding tools, frozen hotspot H09. See `baseline/pilot-decision.json`.
- **Completed slices:** untouched behavioral baseline, architectural inventory, neutral Pi-reference infrastructure, coding-tools pilot, session interchange, native session/context, agent catalog/summarization, streamed-attempt, MCP connection/publication, provider credentials/snapshots/router ownership and initial CLI workflow ownership.
- **Active slice:** H01 RESOLVED: current model/project/conversation bindings and command workflows have named owners; root retains bootstrap/framework composition and resource leases. Full checkpoint preserves all 1,858 original identities (1,890 candidate), 1,378 .NET rows/zero skips, 24 Node/47 Python, 269 MCP checks, all process variants, all 66 terminal reports/136 cases/13 assertions. Two first terminal outcomes are retained and investigated; complete unchanged repeats return frozen acceptable outcomes. No sets, fixtures, tests or pin widened.
- **Latest observed main:** `064bcc7b90344cffde595f9aca1c5b850cefe567`; no divergence or merge conflicts at the session-start/checkpoint queries.
- **Blockers:** none. A required push failure is a durability blocker; stop before continuing another slice.

## Verification and baseline state

Two passes: format/build pass; .NET 1,356/1,356, zero skips; Node 24/24;
Python 38/38; official MCP 261 passing checks plus eight classified failures,
no regressions. All 17 process differential drivers and six virtual-routing
variants are attempted twice at rebuilt frozen Pi. The old paid-classifier probe
has invalid boolean criteria and is classified baseline-unrunnable. All 33
terminal fixtures cover 68 declared matrix cases per pass, both calibration and
paired. All 13 applicable semantic assertion invocations pass. The stable index
has 1,858 identities (1,353 .NET display groups preserve all 1,356 data rows).
Four terminal cases are baseline-flaky; exact outcome sets are retained. Post-pin verification preserves all 1,858 identities, with nine additional passing Python tests (47 total), all 66 terminal reports/136 cases and all 13 semantic assertions. See `baseline/post-pin/README.md` for interrupted/setup attempts and exact comparison. Known
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

The selection is immutable. H01 and H04–H10 are RESOLVED; H02–H03 remain OPEN. Current dispositions are recorded separately in `baseline/hotspot-dispositions.json`; untouched selection/metrics stay in `baseline/frozen-hotspots.json`.

| ID | File/type | Capability |
|---|---|---|
| H01 | Program.cs (RESOLVED) | Composition/startup |
| H02 | TerminalScreen | Terminal rendering |
| H03 | TerminalEditor | Terminal interaction |
| H04 | ConversationRun (RESOLVED) | Agent execution |
| H05 | ConversationSession (RESOLVED) | Sessions |
| H06 | PiJsonlSessionInterchange (RESOLVED) | Session interchange |
| H07 | PiAgent (RESOLVED) | Agent execution |
| H08 | ProviderModelRuntime (RESOLVED) | Provider selection |
| H09 | CodingTools (RESOLVED) | Coding tools |
| H10 | McpRuntime (RESOLVED) | MCP |

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
- `baseline/pilot-checkpoint/`: full pilot capture, characterizations, preserved comparison, control evidence and pattern assessment.
- `baseline/interchange-slice/`: characterization, focused/full .NET, affected terminal evidence and preserved comparisons.
- `baseline/session-slice/`: native session characterizations, focused/full .NET and affected parity evidence.
- `baseline/agent-slice/`: summary/catalog characterization, focused/full .NET and affected process/terminal evidence.
- `baseline/run-slice/`: checkpoint-failure protection, journal/attribution/stream focused stages, full .NET and affected parity.
- `baseline/mcp-slice/`: startup-error protection, focused/full .NET, complete conformance and affected process evidence.
- `baseline/provider-checkpoint/`: complete second structural checkpoint and pre-change/characterization/focused evidence.
- `baseline/composition-slice/`: startup/help/error goldens, ownership stages, broad/final-source checks, declared preservation scopes, fast-output diagnostic and capture-recovery proof.
- `baseline/composition-runtime/`: stale-save rollback/retry characterization, replacement/reload/navigation owners, permanent architecture checks and scoped original-identity comparison.
- `baseline/documentation-validation/`: repaired provenance/path audit, negative controls, format/build, parser-neutrality and integrity receipts.
- `baseline/composition-checkpoint/`: full all-identity composition capture, unchanged fixture repeats, original new-outcome attempts, source/commands and integrity receipts.
- `baseline/hotspot-dispositions.json`: current dispositions without altering untouched metrics.
- `tools/`: standalone capture/parser/comparison/metrics tooling; parser dependencies only in `/tmp`.

## Exact next action

Continue H02 screen rendering: characterize under-protected scheduling/live transcript
transitions, extract actual scheduling/presentation ownership under the existing screen
lock, run focused tests and complete affected calibrated terminal fixtures. Then H03
editor interaction/dialog ownership. Preserve all complete terminal matrices and
frozen semantic sets. Commit/push verified slices. Architecture and docs-path checks
are wired into ordinary .NET tests and CI.
`tools/validate_doc_paths.py` verifies tracked local links/code paths; upstream Pi
shorthand and synthetic input examples are distinct. The eight historical links
were repaired without staging or changing user-owned evidence. Capture.py now reads starting-user-file inventory relative
to its preserved script, so Phase F does not require temporary files in final HEAD.
No test/project moves.
All Section 23 review/preservation/cleanup/fresh-checkout phases remain; no
preservation ref or cleanup exists yet.

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
