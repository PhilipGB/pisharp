# Untouched refactor baseline

PiSharp branch point: `064bcc7b90344cffde595f9aca1c5b850cefe567`.
Frozen Pi source: `ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`.
The current execution-ledger source note selects Pi; TODO and the latest committed
trust/startup evidence corroborate it. Older capability/oracle pins are historical
evidence and are not alternate current references.

`GOAL.md` contains the complete user request. No product or test source changed
while these measurements and results were captured. `source-selection.json`,
`capture-metadata.json` and each `*.invocation.json` retain exact commands, working
directories, flags, logger forms, source pins and the bounded-worker environment.

## Verification scope

Both baseline passes include solution formatting and warnings-as-errors build,
all .NET tests, the complete Node terminal-state tests and Python terminal tests,
all three official MCP conformance protocol modes, every committed process
differential (including all six virtual-routing variants), and every committed
terminal fixture across its entire declared dimensions/themes/modes matrix.
Each terminal matrix has both same-Pi calibration and Pi/PiSharp comparison.
Applicable committed semantic assertion scripts run against the retained paired
captures. This is PiSharp's verification scope, not a claim of full upstream Pi
repository testing or complete Pi parity.

Terminal launches preload Pi's source resolver, which explicitly rejects falling
through to stale workspace package outputs. Supplemental capture installs Pi
dependencies from its committed lockfile and rebuilds offline at the frozen SHA,
then repeats every process differential that might resolve a dependency through
`dist`. Preliminary results are retained under `*.before-pi-build.*` but are not
the frozen process outcomes. Build setup must leave tracked Pi source unchanged.

Initial .NET runs collected coverage with the already-installed Coverlet
collector. Instrumented child processes occasionally aborted in Coverlet's
`UnloadModule` tracker with abandoned mutex/shared-file exceptions. Those raw
TRX and coverage reports remain diagnostic evidence; two subsequent full runs
without instrumentation establish ordinary .NET semantic outcomes. Coverage is
practical path evidence with this collection limitation, not a completeness gate.

An initial capture invocation used the project name as the extension DLL name;
the project explicitly names its assembly `dynamic-resources`. The five extension
fixture matrices are repeated with the correct DLL in both baseline passes.
Their `*.invalid-attempt.*` files are setup diagnostics and excluded from the
frozen semantic inventory. Product, fixture and conformance expectations remain
unchanged throughout these corrections.

## Data and limitations

`semantic-baseline.json` holds stable logical identities and observed semantic
outcome sets. `tools/results.py` extracts TRX, TAP, verbose unittest, terminal and
MCP check results. .NET identities use class/method/data displays rather than
source paths or namespaces. Some long parameter displays collide after VSTest
truncation: those groups preserve exact row counts and outcome multisets; original
TRX retains per-row adapter IDs for movement audits. Explicit rename/move and
invocation mappings start empty and must change with the corresponding moves.

Duration, timestamps, result order, generated directory names and machine paths
do not participate in semantic comparison. Terminal comparison preserves the
existing harness's exact state/render/HTTP policy; reports retain first concrete
differences. Raw viewport/render streams are hashed and omitted from durable
baseline storage to avoid retaining very large redundant buffers. Their exact
comparison reports and compact semantic assertion captures remain. The omission
inventory states this explicitly; baseline artifacts are not presented as full
raw PTY evidence. Existing committed parity fixtures remain unchanged.

`failure-classifications.json` classifies baseline failures, unavailable live
checks and flaky outcome sets. The old Codemode classifier probe passes invalid
boolean criteria: current Pi rejects it before billing; a scratch diagnostic with
valid criteria succeeds. The Markdown fixture expects `second`, but its default
fixture server emits `fixture reply`, so its same-Pi calibration also fails.
Regular TUI mode is already unsupported by PiSharp. Other terminal differences
are retained as baseline parity gaps, not normalized or repaired during this
architecture task. Four live llama tests return without live assertions when no
endpoint is configured; their xUnit pass status is not live-provider evidence.

## Architectural inventory

`architecture.json` measures Git source at the untouched branch point, including
all projects/references/packages, file size/churn, type size, constructor
parameters and syntax decision complexity. Standalone parser dependencies are
installed only under `/tmp`; versions and limitations are recorded. Two files
use grammar constructs this parser cannot fully parse; their file size/churn
measurements remain usable, but their syntax metrics are not authoritative.
Primary constructors/data records are listed separately from ordinary injected
constructor maxima.

`candidate-ranking.json` records the diagnostic candidate inventory;
`frozen-hotspots.json` freezes ten meaningful responsibility/coupling risks before
Commit 0. Each includes capability, reason, size/churn/imports, constructor data,
coverage and test-reference navigation. All ten initially remain `OPEN`.
`compatibility-protection.json` identifies existing characterization anchors;
these are not claims of exhaustive surface coverage.

`audits.json` records publication/documented extension consumers, dependency
reproducibility, temporary-tool isolation and `test.pdf` dependency/hash audit.
No code/test/script depends on the user-owned PDF; no verification copy is needed.
`starting-user-files.json` records hashes of the existing untracked user files,
which are neither swept into this commit nor removed.

Raw coverage diagnostics with whitespace in exception output are losslessly gzip-compressed; `compression-manifest.json` records original paths and SHA-256 values.
