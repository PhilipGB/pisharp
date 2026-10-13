# Repository Guidelines

## Project Structure & Module Organization

- `src/PiSharp.Core/`: deterministic conversation trees, edit/diff logic, and read planning; keep provider, terminal, filesystem, and process dependencies out.
- `src/PiSharp.Runtime/`: shared agent execution under `Agents/`, sessions, tools (including `Tools/Shell/`), extensions, resources, MCP, and Codemode.
- `src/PiSharp.Cli/`: startup, authentication, composition, sessions, provider adapters under `Providers/`, and JSON/RPC front ends under `Protocols/Json/` and `Protocols/Rpc/`. TUI code is grouped by responsibility; embedded assets live in `Providers/Data/` and `Tui/Themes/`.
- `tests/PiSharp.Tests/`: capability-organized `Architecture/`, `Core/`, `Runtime/`, and `Cli/` tests; shared fixtures and helpers live in `TestInfrastructure/`. `tests/ResourceDiscoveryExtension/` supplies an extension fixture.
- `tools/parity/` and `tools/McpConformance/`: differential probes and conformance tooling. Architecture, protocol contracts, and parity evidence live in `docs/`.

## Build, Test, and Development Commands

Use the .NET 10 SDK. Codemode requires Node.js 22.19+; CI uses Node.js 24. Python 3 supports terminal and MCP fixtures.

```sh
dotnet restore PiSharp.slnx                       # Restore dependencies
dotnet format PiSharp.slnx --verify-no-changes    # Check formatting
dotnet build PiSharp.slnx --warnaserror           # Build; reject warnings
dotnet test PiSharp.slnx                         # Run the .NET suite
dotnet run --project src/PiSharp.Cli              # Start the interactive CLI
python3 -m unittest discover -s tools/parity/terminal -p 'test_*.py'
```

For MCP changes, build `tools/McpConformance/PiSharp.McpConformance.csproj`, then run `node tools/parity/mcp-conformance/run.mjs`.

## Coding Style & Naming Conventions

Follow existing C# style: four-space indentation, file-scoped namespaces, and braces on separate lines. Use PascalCase for types and methods, camelCase for parameters and locals, and `_camelCase` for private fields. Preserve nullable annotations and use `Async` suffixes for asynchronous production methods. Use `dotnet format`; extract cohesive feature components when adding concentrated CLI or runtime behavior.

## Testing Guidelines

Use xUnit (`[Fact]`/`[Theory]`), naming files and classes `<Feature>Tests` and methods after observable behavior. Prefer deterministic agent and local HTTP fixtures; the suite requires no live provider keys. Run focused tests with `dotnet test PiSharp.slnx --filter FullyQualifiedName~ToolSearchTests`. Add process or differential evidence for protocol boundaries. No numeric coverage threshold is configured; passing tests establish only their covered behavior.

## Commit & Pull Request Guidelines

Recent commits use imperative subjects such as “Align startup spacing with Pi.” Keep commits focused. PR descriptions should explain the behavior change, validation commands/results, relevant issues, and remaining limitations; include terminal captures for visual changes.

Before parity work, review `TODO.md`, `docs/continuation.md`, and `docs/parity/feature-matrix.md`. Update relevant inventories, fixtures, and the execution ledger when evidence changes.

Write generated parity captures to `artifacts/parity/<run>/` (Git ignored). Commit reviewed, compact evidence under `docs/parity/fixtures/` with source pins, scope and limitations. Keep raw streams out of routine commits; ignored local archives need a separate backup for long-term retention.

## Security & Configuration

Keep credentials out of commits and fixtures. Prefer provider environment variables over `--api-key`. Use `PISHARP_AGENT_DIR` and `PISHARP_SESSION_DIR` to isolate local configuration and sessions.
