# Repository verification

Use .NET 10, Node.js 22.19+ (Linux CI uses 24), Python 3 and Git.

```sh
dotnet restore PiSharp.slnx
dotnet format PiSharp.slnx --verify-no-changes --no-restore
dotnet build PiSharp.slnx --no-restore --warnaserror
dotnet test PiSharp.slnx --no-build --no-restore
python3 tools/validate_doc_paths.py
```

The normal .NET suite includes ArchitectureTests and DocumentationPathTests.
Linux CI also runs the documentation validator directly. It validates required
AGENTS/architecture/capability/parity Markdown links, reference definitions and
local inline paths against Git-tracked files/directories. Parity inventories also
contain explicitly upstream Pi shorthand and synthetic input examples; those
external/example paths are separate from PiSharp-owned inline paths. Local,
untracked files cannot satisfy repository links.

Follow the [Pi reference setup](../parity/pi-reference.md) for differential checks.
Node/Python terminal tests and official MCP conformance remain separate normal CI
jobs/steps. Passing covered checks does not establish complete provider or Pi parity.
