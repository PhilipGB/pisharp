# Program composition refactor verification

This bounded change stays on `refactor/architecture-maintainability`; it does not
merge into main. Starting commit: `95edc6771d6871ea36aa5692e59426d72ebe9d4b`.
Production code commit: `ad4319d2a2d68ecf379190fd77cc1dc32100bef1`.
The final documentation/evidence commit follows that code commit.

[Program](../../src/PiSharp.Cli/Program.cs) shrank from 1,003 lines / 58,388 bytes
to 118 lines / 6,274 bytes. [CLI composition](../capabilities/cli-composition.md)
records each preparation, transition, mode and terminal owner. Program retains
process/HTTP/configuration/extension/presentation leases, startup sequencing,
provider/project/chat startup catches, concrete capability construction and mode
selection. Those remaining blocks select implementations or establish process
lifetime; none implements a substantial application workflow.

Project preparation returns only configuration/arguments and its startup decisions.
Initial model selection and conversation opening have separate results. Run opening
owns execution policy; model/project transitions own publication and rollback.
Terminal presentation owns screen/theme/input lifetime; execution owns cancellation
and save settlement. Settings, model/session selection, input actions and resource
completion have individual capability owners. Shortcut and mode owners route those
capabilities directly. No new owner accumulates application services or implements
startup, execution and UI together.

No production project, package version, public API or existing namespace changed.
Core and Runtime source are unchanged. AGENTS.md and documentation path-validation
inputs already describe the capability folders used here and require no edits.
The scratch responsibility inventory remains outside the repository.

## Automated verification

Environment: .NET SDK 10.0.112, Node.js 24.21.0, Python 3.12.3, Linux.

| Command | Result |
| --- | --- |
| `dotnet restore PiSharp.slnx` | Passed |
| `dotnet format PiSharp.slnx --verify-no-changes --no-restore` | Passed |
| `dotnet build PiSharp.slnx --no-restore --warnaserror` | Passed; zero warnings/errors |
| `dotnet test PiSharp.slnx --no-build --no-restore` | 1,390 passed; zero skipped |
| `python3 tools/validate_doc_paths.py` | Passed |
| `npm test --prefix tools/parity/terminal` | 24 passed; zero skipped |
| `python3 -m unittest discover -s tools/parity/terminal -p 'test_*.py'` | 47 passed |
| `dotnet build tools/McpConformance/PiSharp.McpConformance.csproj --no-restore --warnaserror` | Passed |
| `node tools/parity/mcp-conformance/run.mjs` | Baseline comparison passed: 261 passing checks and eight classified failures; 269 total |

The original .NET baseline passed 1,382 rows. Eight rows were added across
[StartupOrderingProcessTests](../../tests/PiSharp.Tests/Cli/Startup/StartupOrderingProcessTests.cs)
and [ConversationCompositionTests](../../tests/PiSharp.Tests/Cli/Sessions/ConversationCompositionTests.cs).
The five initial process rows characterized configuration/provider precedence,
initial session-name persistence, print/JSON authentication gating and RPC stdin
ownership. Three composition rows cover current settings/tool policy, persistence
binding, reload publication and failed preparation. Existing tests were not edited
or removed. Focused extraction checkpoints passed 76, 209, 141, 96, 86, 34 and 208
rows respectively, and the intermediate full checkpoint passed 1,387 rows.

## Differential scope

The configured Pi SHA is unchanged:
`ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`. The source checkout was verified using
`python3 tools/parity/pi_reference.py verify --pi /tmp/pisharp-current-pi`.

The existing quiet-startup differential matched 28 settings scenarios, three writes
and selector ordering. The default-project-trust process differential matched its
scenario classifications; it retains the existing Pi/PiSharp authentication-error
exit-code distinction. Virtual-model RPC/provider differentials matched session
transitions and maximum-thinking/context-compaction requests using deterministic
local providers.

Twenty-one terminal reports retain 63 attempted cases, including baseline controls
and calibration repeats. Project settings reload, extension resource reload and
trust-extension UI pass their paired comparisons and all three semantic assertions.
Startup resize buffers, thinking footer colors, settings-panel cell representation
and unsupported regular-mode settings reproduce unchanged-baseline failures.
Across 19 original/refactored control cases, named checkpoint states and HTTP match;
eight of those cases retain the regular-mode rejection rather than exercising UI.
Four intermediate-render sequences vary. An unchanged baseline repeat reproduces
that variance, and its repeated render sequence exactly matches the candidate.
The first trust calibration caught a synchronized-output flag mid-frame; the
unmodified repeat and subsequent product comparison pass. Failed attempts remain
recorded, and comparison fixtures/normalization are unchanged.

Terminal verification results and baseline controls are recorded in the linked
[verification evidence](../parity/fixtures/program-composition-2026-10-13/verification.json).
Reports retain failed comparisons and timing observations. Full Pi parity is not
claimed. Raw captures and command logs remain in the local task evidence directory;
the committed summary identifies commands, code/reference pins and scoped outcomes.
