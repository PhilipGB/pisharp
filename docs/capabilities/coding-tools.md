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

Search and listing already have separate `SearchTools` and `DirectoryListingTool`
owners. The built-in default order remains read, bash, edit, write. Discoverable
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
