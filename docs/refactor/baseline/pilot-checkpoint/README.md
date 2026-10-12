# Coding-tools pilot checkpoint

`pilot-preservation-comparison.json` passes all 1,858 frozen identities with no
new skips or missing scenarios. The 1,869 candidate identities add two .NET
cancellation characterizations and the nine post-pin Python guard tests.
`completeness.json` verifies the full scope: 1,358 .NET rows, 24 Node tests,
47 Python tests, 269 MCP identities, every process differential/virtual variant,
all 33 terminal definitions/66 reports/136 cases and 13 semantic assertions.
Known classified failures and the four frozen baseline-flaky sets are unchanged.

`pre-change-characterization/` records 25 coding-tool tests passing against the
original production implementation; `focused-verification/` records 57 tests
passing after extraction. No test/project paths, names or invocation scopes moved;
logical/invocation mappings therefore remain unchanged. The full checkpoint uses
one candidate source manifest and patch. Final harness source hashes are recorded
separately for the session setup correction described below.

`pilot-assessment.json` evaluates the pattern: five cohesive files replace one,
with a modest aggregate line increase. The public facade binds MAF/MEAI tools;
file reads, coordinated mutations, shell execution and Pi declarations have real
owners. No project, package, assembly or interface was added. Keep this pattern
where responsibilities separate; require a concrete dependency/build reason for
any new project. Core and the native extension facade remain compatible.

## Retained capture diagnostics

The initial full run had three outcomes outside the frozen sets. Download-cancel
calibration had one extra HTTP poll between two unchanged Pi runs; verbose-startup
calibration had one extra render frame between two Pi runs. Complete unchanged
fixture reruns matched the original sets. First attempts remain archived.

Load/replace-cancel repeatedly had 12 Pi versus 11 PiSharp render frames, with
matching HTTP. An isolated, freshly built control at untouched PiSharp
`064bcc7b90344cffde595f9aca1c5b850cefe567` reproduced the same exact difference.
The control built with zero warnings/errors using the recorded writable NuGet
HTTP cache; no warning suppression or source change was used. Its raw report's
`pisharpSha` identifies the driver checkout HEAD; `--pisharp` selects the baseline
control assembly, as documented in `session-isolation-correction.json`.

Changing only the harness session-directory environment restored all 12 frames
and zero differences with the candidate C# unchanged. The normal runner now
applies an owned session directory when its command permits persistence; ordinary
`--no-session` captures retain their original environment. Both conditions are
protected by the Python test, and all 47 Python tests pass. The previous claim
that a global session-directory override could not affect no-session captures
was too broad for this strict render-sequence harness. The original source and
expectations remain unchanged; no frozen outcome was widened.

`baseline-control-load-replace/`, `original-session-environment-candidate/`,
`session-isolation-correction.json`, archived first/second attempts and the
control script preserve the causal evidence. The corrected complete fixture
replaces only its authoritative result; raw failed attempts remain diagnostic.
Bulk raw streams are hashed/omitted under the same baseline retention policy.
Files with trailing whitespace are losslessly gzip-compressed with hash receipts.
All 1,748 other starting user files, including test.pdf, remain byte-identical.
