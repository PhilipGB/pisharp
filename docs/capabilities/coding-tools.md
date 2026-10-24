# Coding tools

`CodingTools` is the shared public entry point for built-in tool selection and
local execution. Runtime owns its filesystem and process behavior; Core owns
pure text planning, exact edit planning and diffs. CLI and extensions use the same
facade, including interactive Bash and model-specific image limits.

## Ownership and flow

- [CodingTools](../../src/PiSharp.Runtime/Tools/CodingTools.cs) selects tools and binds
  their delegates with Microsoft.Extensions.AI's `AIFunctionFactory`. The Bash
  adapter reads Pi session properties from the current MAF run and passes explicit
  environment values to shell execution.
- [CodingToolDeclarations](../../src/PiSharp.Runtime/Tools/CodingToolDeclarations.cs)
  retains Pi's exact descriptions and schemas through `DelegatingAIFunction`.
  This customization is needed because inferred .NET schemas differ from Pi's
  numeric offset/limit and batch-edit contracts.
- [FileReader](../../src/PiSharp.Runtime/Tools/FileReader.cs) detects images,
  enforces inline limits, applies current model resizing and delegates large text
  reads to `StreamingTextReader`; `ReadTextPlanner` provides pure selection.
- [FileMutations](../../src/PiSharp.Runtime/Tools/FileMutations.cs) performs atomic
  writes and exact batch edits. A shared `FileMutationQueue` serializes resolved
  aliases across independent facade instances. Edit planning happens against the
  original file; UTF-8 BOMs, original newline convention and file mode survive.
- [ShellExecution](../../src/PiSharp.Runtime/Tools/Shell/ShellExecution.cs) owns running
  processes, cancellation, timeouts, environment sanitation, output pumping and
  process-group cleanup. Direct execution returns cancellation and exit metadata;
  model tool execution projects Pi-compatible failures and structured output.

[SearchTools](../../src/PiSharp.Runtime/Tools/SearchTools.cs) delegates built-in grep
to `RipgrepSearch` and find to `FdSearch`. Real `rg` and `fd`/`fdfind` supply their
regex/glob/ignore and encounter-order semantics. Streaming parsers bound retained
output; the old managed 10 MiB file and 20,000-path inventory caps do not apply to
these tools. `DirectoryListingTool` owns ls separately, including locale ordering,
stat filtering and its own limits. Install `rg` for opt-in grep and `fd` (or
`fdfind` on Linux) for opt-in find. Default read/bash/edit/write and native ls do
not need those search executables. The built-in default order remains read, bash, edit, write. Discoverable
inventory additionally includes grep, find and ls; requested/excluded selection
and unknown-name errors remain facade contracts.

## Dependency and parity constraints

All owners remain internal to PiSharp.Runtime. No additional project or interface
is needed: they share local tool execution semantics and do not form independently
published or volatile implementation contracts. The public facade and Runtime
assembly identity stay available to native extensions. Core remains independent
of Runtime, CLI, MAF and external I/O.

Shell tools remove inherited `PI_SESSION_*`, provider, model and reasoning values
before applying the current run's values. Abort reaches both tool and direct
executions on that facade. A completed parent process may leave pipes inherited
by descendants; the 100 ms idle grace bounds drain time while preserving output.
Cancellation before process start has no command side effect and leaves the
runner usable. User-visible error text, limits, timeout formatting and shell
selection are parity-sensitive contracts.

## Filesystem and model-visible contracts

Text read preserves decoded UTF-8 byte counts, BOM/newlines, selected ranges and
head notices with bounded buffers. Image read uses the existing Skia pipeline,
source-matched orientation/Lanczos3 behavior and current model limits. Named JPEG
cases use calibrated decoded-image equivalence rather than claiming byte identity
between distinct encoders. Image input/pixel allocation bounds remain explicit.

Writes and edits retain atomic replacement and private new files. Existing Linux
ownership and kernel-compatible permission changes are restored through the
private handle at publication. If ownership cannot be restored, publication fails
with the original file intact. Replacing an inode breaks its hard-link identity;
this and stricter parent-directory requirements are documented strict-equivalence
exclusions. Invalid edit batches never partially apply. Internal patches,
truncation metadata and structured content remain in lifecycle/history data;
providers receive the pinned text/image projection.

Seven source-extracted declarations preserve real numeric argument semantics,
validation, defaults and output schemas. Dynamic selection uses the existing
registry/MAF loadout, with excluded tools rejected and extension tools retained
when built-ins are disabled. A completed side effect has one durable outcome;
cancellation and restart do not replay it.

## Verification

[CodingToolsTests](../../tests/PiSharp.Tests/Runtime/Tools/CodingToolsTests.cs) protects exact
schemas, selection, file coordination, shell results, cancellation and timeouts.
`ReadTextConformanceTests`, `EditConformanceTests`, `ReadImageToolTests`,
`CodemodeBashOutputTests` and session/environment/provider tests protect execution
and persistence boundaries. The added cancellation characterizations use real
shell and queue behavior, without replacement interfaces.

Run focused tests with:

```sh
dotnet test PiSharp.slnx --filter FullyQualifiedName~CodingToolsTests
```

Run the full solution after structural changes. [Codemode Bash differential](../../tools/parity/codemode-bash-differential.py)
compares process behavior with the [configured Pi reference](../parity/pi-reference.md).
The full calibrated terminal matrices protect skill-read, Bash, resource and
trust interactions. Their known baseline differences remain recorded in parity
evidence; passing covered checks do not establish complete upstream parity.

The [built-in milestone receipt](../parity/fixtures/builtin-coding-tool-parity-2026-10-22.md)
records 456 direct cases, three physical provider APIs, actual CLI restarts,
concurrency/cancellation and safety exclusions against the unchanged Pi pin.
Eight additional ownership/mode observations are facets of the contract; atomic
inode replacement remains excluded from strict identity. Linux CI captures the
actual pinned oracle under its assignable supplementary group and provides it to
`FileMutationOwnershipTests` using the test-only
`PISHARP_TEST_OWNERSHIP_ORACLE` path. The committed local fixture uses an
unprivileged identity; privileged execution needs its own executed oracle.
