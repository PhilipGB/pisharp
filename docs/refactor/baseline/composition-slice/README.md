# Composition ownership checkpoint (H01, still OPEN)

Program retains executable composition and runtime adoption. Concrete CLI owners
now handle early startup commands/help/errors, raw terminal stream lifetime,
startup session loading and saved-model checks, JSON prompt expansion/rejection,
settings persistence, clipboard actions, session document commands and interactive
prompt validation/input monitoring/event presentation. No project, interface,
package or public API identity changed; tests did not move. Program is 1,211 lines
versus 1,511 at the frozen baseline. Responsibility isolation, rather than that
line count, is the change; model/project replacement and command coordination
remain H01 work. See `docs/capabilities/cli-composition.md`.

## Characterization and focused stages

Before production edits, the selected pre-change scope passed 54 checks and the
help/unknown-option characterization scope passed 21. The exact help fixture came
from the unchanged H08 CLI; unknown-option stdout/stderr/exit behavior was observed
before the new process test. Existing terminal PTY tests already cover settings,
clipboard, deletion confirmation, name/export, startup and session replacement.
The first owner stage passed 64 focused checks. The prompt owner initially had two
compile errors (unused constructor argument and a shadowed error writer); both
were corrected before tests of that stage. Raw failed-build diagnostics remain.

All terminal PTY checks passed in the 33-check terminal scope, but the direct-runtime
fast Bash update assertion failed once. Its unchanged isolated repeat passed;
the untouched `064bcc7b` control passed its corresponding 32-check scope. Both full
candidate .NET runs subsequently passed. `fast-output-investigation.json` records
the unconfirmed cause and unchanged exercised source: this test does not invoke
the new CLI owners. No expectation or frozen outcome set was changed.

## Broad and final-source verification

`verification/` contains format/build, 1,366 .NET rows/zero skips, 24 Node checks,
47 Python checks, all 17 process drivers/six total virtual-routing variants and
six complete calibrated terminal matrices (12 reports/54 cases/two applicable
semantic assertions). Its comparison preserves all 1,495 original identities in
that declared scope; 1,514 candidate identities add ten .NET and nine Python tests.
Known classifier/presentation failures remain classified. Render frame counts can
vary; raw reports remain alongside the semantic comparison and do not show new
startup error classifications.

Before committing, three glue details were tightened to preserve the original
semantics: the final footer update stays outside the session-command catch;
Console.Error is resolved when queued-input errors are written; JSON mode retains
the original Environment.ExitCode writes/timing. `stage-1-source/` preserves the
three earlier files losslessly; `compatibility-tightening.json` describes the delta.

`compatibility-verification/` verifies final production source: format/build,
another complete 1,366-row .NET run/zero skips, Node 24, Python 47, every process
driver/virtual variant, and complete paired selector/settings, thinking and resume
matrices (three reports/17 cases). Its comparison preserves all 1,456 original
identities in that scope; 1,475 candidate identities contain the same 19 additions.
Scope files list every expected identity; neither comparison reports missing tests,
new semantic outcomes or unexpected skips. These are intermediate H01 checkpoints,
not a new full 1,858-identity/all-MCP/all-terminal checkpoint. The last full
checkpoint remains H08; full verification remains required at subsequent major
boundaries and Section 23 finalization.

Each stage records its source manifest, patch, commands, working directories,
environment and raw results. Sources stayed unchanged during each capture. The
original paid-classifier failure and known terminal differences were retained;
fixtures, pin and frozen baseline data were untouched. Whitespace-bearing raw logs,
TRX and patches are losslessly gzip-compressed with `compression-manifest.json`.
Bulk viewport streams have omission hashes; compact assertion captures remain.

`capture-recovery.json` verifies the corrected capture inventory path with a
candidate checkout lacking docs/refactor: the preserved script reads its own
baseline inventory, supporting Phase F after cleanup. User integrity checks retain
all 1,748 starting files outside the authorized AGENTS append, whose original
content is separately checked. The test.pdf hash still matches the frozen value.
