# Native extension provider, MCP, and virtual model registration — 2026-10-17

## Scope and source

This bounded implementation increment is based on merged PR #10 at
`632bd08b87afb10ac6669b4072d08a8e3b452687`. The configured Pi source remains
pinned to `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. The source review covered
the extension registration and loader/runner contracts, provider model and
registry selection, MCP server configuration and lifecycle, virtual-model
routing, and agent-session route-state persistence, including the corresponding
Pi extension, custom-provider, and virtual-model documentation.

The public builder now accepts provider definitions backed by
`Microsoft.Extensions.AI.IChatClient`, typed MCP server configurations, and
virtual-model route callbacks. Registrations retain their declaring package
path and enter the normal `ProviderModelRuntime`, MCP startup manager, and
`VirtualModelRegistry`. Package Configure failure restores prior registrations;
reload, session replacement, package update, and removal replace the normal
catalogue and connection ownership. Existing provider clients drain active
requests before disposal and reject later calls after removal. Secret values are
redacted from provider factory/request and extension MCP connection diagnostics.

An ordinary external fixture project references the public Abstractions package
and MEAI only. Process acceptance packs and installs v1, selects and invokes its
provider in the normal tool loop, connects and invokes its stdio MCP server,
routes and resumes virtual-model state in a fresh process, replaces it with v2,
checks failed-Configure rollback, and removes the package while retaining the
saved session and unrelated configured MCP server. Separate coverage checks
provider registration validation, physical/virtual conflicts, source-aware
diagnostics, client drain, secret redaction, and provider load-context
collection.

## Explicit limits

Provider-specific OAuth/browser/device authorization, dynamic late
registration, broader extension event availability, advanced renderers/UI,
and TypeScript source compatibility are still outside this increment. The broad
Extensions / .NET API and orchestration parity ledger entries remain
`in_progress`. An extension provider implements `IChatClient` and uses the
normal host selection/credential path; it does not add provider-specific login
flows or a second provider protocol layer.

## Local verification

- `dotnet restore PiSharp.slnx -p:NuGetAudit=false` — passed.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore` — passed.
- `dotnet build PiSharp.slnx --no-restore --warnaserror --disable-build-servers -m:1` — passed, zero warnings/errors.
- Focused native extension process/runtime/registration/ALC suites — 14/14 passed, zero skipped.
- Existing native extension API/loader/catalog and Configure-recovery suites — 29/29 passed, zero skipped.
- Architecture tests — 7/7 passed.
- `DirectMcpStartupWaitRemainsBoundedAndReportsPendingTools` — 1/1 passed in isolation.
- Node terminal parity — 24/24; Python terminal parity — 50/50.
- MCP conformance — no regression against its eight classified baseline failures.
- `test.pdf` was unchanged from the base commit.

The full local .NET invocation did not return a completion summary in this
runner. It emitted timing failures in the existing direct-MCP-startup and
llama.cpp manager TUI tests; the direct-MCP case passed when rerun alone. Final
exact-head Linux CI results will be recorded here when available.
