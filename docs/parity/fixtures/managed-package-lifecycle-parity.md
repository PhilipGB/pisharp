# Complete Managed Package Lifecycle and Resource Distribution Parity

## Scope and source pins

The milestone verifies package CLI commands, npm and Git installation/update,
configured package managers, legacy global lookup, identity and deduplication,
scope persistence and trust, selective and bulk updates, removal and listing,
resource loading, startup/reload, failure recovery, and concurrency.

- Actual PR #26 merge SHA: `d969d3ec5c4f0bb90e9c446a6c505c0374f59dd9`.
- New branch base SHA: `d969d3ec5c4f0bb90e9c446a6c505c0374f59dd9`.
- Branch: `milestone/managed-package-lifecycle-parity`.
- Implementation source SHA: `07eda4e67eb161c906223dfe2941bdc79206a1ce`.
- Pinned Pi SHA: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

The reviewed source inventory records 15 capability families and the Pi/PiSharp
owners for each. The compact [inventory](managed-package-lifecycle-parity/inventory.json)
and [source links](managed-package-lifecycle-parity/source-links.json) are
committed beside this receipt.

## Capability inventory

The 15 families cover CLI; npm install and update; configured managers; legacy
global lookup; Git install and update; package identity; removal/listing; bulk
updates; configuration/trust; resource distribution; startup/reload; failure
recovery; and concurrency/cancellation. The source-pinned inventory maps each
family to required cases, Pi source owners, PiSharp owners, and executable tests.

## Already-correct behaviour

Before this milestone, PiSharp already had package settings, local resource
discovery, npm/Git cache support, package command handling, and much of the
startup and update lifecycle. This work verified those paths with real pinned-Pi
processes and tests rather than treating prior implementation as parity proof.
The case corpus covers configured scopes, source identity, lockfile/ref
selection, update targeting, reload snapshots, provider/RPC-visible package
resources, and recovery after failed or interrupted operations.

## Confirmed mismatches

The source inventory confirmed a resource-distribution gap: temporary npm and
Git package roots were not resolved through the existing startup/package-store
path with invocation-only scope. The gap is covered by 17 process, test, and
resource-loading cases after correction. The remaining observed Pi differences
are explicitly classified as intentional safety differences below; there are no
unclassified mismatches in the accepted corpus.

## Corrections

- Added temporary managed npm/Git resource resolution through existing resource
  owners without persisting invocation-only sources into user/project settings.
- Completed package CLI install, remove, update, list, and scoped persistence
  behavior, with source identity and deduplication kept distinct from resolved
  file identity.
- Added staged package replacement, atomic settings publication, cancellation
  cleanup, per-source mutation coordination, and restart recovery that retain a
  last-known-good package when an update fails.
- Added startup/reload snapshots and process coverage for package-provided
  skills, prompts, themes, and native extensions.
- Stabilized same-Pi calibration by running the npm fixture build before the
  package CLI baseline, so both CLI samples use the same compiled extension DLL.

## Required case inventory

The committed contract contains 27 checkpoints and 240 required cases. The
latest local exact-source artifact passes 23/27 checkpoints and 211/240
executed cases with zero failures or skips. The committed-evidence checkpoint awaits validation against the refreshed bundle for source
`07eda4e67eb161c906223dfe2941bdc79206a1ce`. The remaining 29 cases are in committed evidence, CI
integration, draft publication, and exact-head CI. The compact
[case coverage](managed-package-lifecycle-parity/case-coverage.json) and
[case results](managed-package-lifecycle-parity/case-results.json) preserve the
core corpus and its exact required IDs and classifications.

## Differential results

All local source-pinned lifecycle checkpoints passed. The same-Pi calibration
repeated 53 package CLI, 6 npm, and 10 Git install oracle cases; all three
normalized oracle reports matched. The negative control mutated a Git HEAD and
was detected. The [differential summary](managed-package-lifecycle-parity/differential-summary.json)
records the pinned SHA and matching-digest checks; the runtime artifact retains the digests.

## Same-Pi calibration

The package CLI, npm, and Git install fixtures were each run independently
against pinned Pi at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Ephemeral paths,
ports, fixture commit identities, and npm debug-log timestamps are normalized;
behavioral results and process outcomes remain compared. All 69 repeated oracle
cases matched, and the intentional Git HEAD mutation remained detectable.

## Failure and concurrency results

The corpus includes failed installs/updates, settings publication failures,
cancellation, process interruption, restart recovery, concurrent install/update,
and resource discovery during update. Failure and concurrency checkpoints pass
11/11 and 9/9 cases respectively, including explicit retained-package and
no-replay assertions. Their full reports and executable identities are recorded
in the local run artifact and summarized by the committed case results.

## Intentional differences

Eleven cases document PiSharp's safer staged-update and failure-reporting
behavior. Pi may temporarily remove a usable package, leave partial install
state, or silently succeed after settings publication fails; PiSharp preserves
the prior valid package where possible, removes incomplete staging, and reports
settings failures. The `pm.cancellation` case also records a reproduced SIGINT
delivery race: pinned Pi exits 130 and may leave the interrupted package,
while PiSharp exits 1 or 130 only when cancellation is observed, settings remain
unchanged, and the partial package is removed. Any other exit or state fails.
The current run records differences in these cases: `npm.update-cancelled`,
`npm.update-interrupted`, `pm.cancellation`, `git.update-dependency-failure`,
`bulk.cancelled`, `config.failed-write`, `failure.cancelled-install`,
`failure.settings-publication`, `failure.interrupted-staging`,
`failure.restart-recovery`, and `concurrent.update-discovery`.

The contract also allows the observed concurrent npm target race and Git clone
races when they occur. Concurrent Git installation can produce a clone
destination collision and a missing template file in pinned Pi; PiSharp
publishes one valid package. The verifier requires both specific errors and a
valid PiSharp final state.

The contract classifies 198 cases as strict matches, 2 as equivalent, and 11 as
intentional safety differences. It records no exclusions or unclassified
mismatches.

## Remaining limitations

This receipt is bounded to the committed 15-family inventory and 240 required
cases; it does not claim broader package-manager or platform parity outside those
cases. CI integration and publication verification remain pending. The new PR
must remain open, draft, and unmerged. Its automated GitHub Actions will run on
publication; exact-head workflow results remain pending.

The npm publication regression terminates an updater immediately after the old
installation is moved aside; offline startup then restores its original resource.
The earlier PR concurrency failure lacked its Pi stderr; ten local repetitions
and the complete local concurrency differential passed, and failed-command
diagnostics are now retained for future inspection.

The push-event verifier now checks the exact event SHA and branch and validates
the workflow’s PR-merge safeguards. Deterministic push, PR synthetic-merge, and
local controls reject invalid identities, missing or skipped cases, failed
checkpoints, failed controls, and unsuccessful bootstrap checks. Package-result
adapters, checkpoint and evidence validation, quality, publication and GitHub
gates are separated into modules.

Push run `40491919378` captured concurrent Pi clone failures at the same
repository destination: one clone found a nonempty destination while the other
failed to initialize its removed config. The classifier accepts this precise
pair only when the independent verifier also confirms a safe PiSharp outcome.
A cancelled Pi npm update also restarted with the newer local registry version;
that exact observed alternative is recorded without relaxing PiSharp safety.

## Local quality results

The local quality checkpoint at source SHA
`07eda4e67eb161c906223dfe2941bdc79206a1ce` passed formatting, a
warnings-as-errors build, all 3,901 .NET tests with zero failures/skips, focused
package tests 116/116, architecture tests 7/7, managed-package Python tests
57/57, terminal Python 66/66, built-in differential Python tests 27/27, terminal
Node tests 24/24, MCP conformance, documentation path validation, and all 18
lifecycle differential rechecks. A prior push run also passed this quality gate
at an earlier source SHA but stopped at its stale committed-evidence fixture.
Two full-suite attempts on the preceding verifier source hit timeouts in the
large-line read test (and one provider stream test on the first attempt). The
affected tests passed in isolation; this Q0 passed with `DOTNET_PROCESSOR_COUNT=2`.
PR run `40407150225` failed when npm emitted an update notice for only one side
of calibration. The disposable fixture now disables npm update notifications
for both products while retaining installation warnings and errors. The evidence
normalizer also handles repeated checkout directory names: all 198 previously
passing CI case records matched their local counterparts after normalization.
The [quality summary](managed-package-lifecycle-parity/quality-summary.json)
contains the machine-readable results.

Raw process captures and profiles remain in the ignored local run directory;
this committed bundle contains compact case, coverage, inventory, quality,
differential, and source-link evidence only.
