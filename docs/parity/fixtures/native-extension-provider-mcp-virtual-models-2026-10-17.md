# Native extension provider, MCP, and virtual model registration — 2026-10-17

## Scope and source

This bounded implementation increment is based on merged PR #10 at
`632bd08b87afb10ac6669b4072d08a8e3b452687`. The configured Pi source remains
pinned to `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. At that pin, the source
review inspected:

- [`extensions/types.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/types.ts): `ExtensionAPI` defines provider, MCP-server, and virtual-model registrations. Provider registrations queue during initial extension load and bind to the model registry; MCP definitions use the `mcp.json` server shape, configured servers take precedence, and another extension's name conflicts; virtual models are selectable catalogue entries routed to physical models.
- [`extensions/loader.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/loader.ts) and [`extensions/runner.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/runner.ts): the loader retains extension source identity; the runner binds queued provider and virtual-model registrations into the model owner and surfaces registration errors with extension paths.
- [`model-runtime.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/model-runtime.ts) and [`model-registry.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/model-registry.ts): the shared runtime supplies catalogue, resolution, credential, and model-selection paths; the registry owns provider definitions and models, including extension registrations.
- [`mcp-servers.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/mcp-servers.ts): extension servers share configured MCP startup, registration ownership, name conflict rules, and connection lifecycle.
- [`virtual-models.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/virtual-models.ts) and [`agent-session.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/agent-session.ts): each routed request carries logical identity, thinking, route reason, messages, previous/failed route context, and JSON state; the callback chooses a physical target and updated state, which the session path persists while assistant attribution records the physical model.
- [`docs/extensions.md`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/docs/extensions.md), [`docs/custom-provider.md`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/docs/custom-provider.md), and [`docs/virtual-models.md`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/docs/virtual-models.md): public extension, provider, and virtual-routing usage.

Pi supports some provider/MCP/virtual registrations after initial load; this
PiSharp slice deliberately keeps the public bridge configuration-time only.
PiSharp maps the observable registration, catalogue, runtime, ownership, and
route-state behavior to its existing owners without copying Pi's mutable
registration architecture.

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
saved session, unrelated configured MCP server, and a second compiled package.
After removal, the unrelated extension remains callable, its configured MCP
tool remains present, package-owned MCP tools are absent, and model selection
through the removed provider returns a deterministic unknown-provider error.
Separate coverage checks
provider registration validation, physical/virtual conflicts, source-aware
diagnostics, invalid route targets/thinking values, client drain, secret
redaction, and provider load-context collection.

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
- Focused native extension process/runtime/registration/ALC suites — 15/15 passed, zero skipped.
- Existing native extension API/loader/catalog and Configure-recovery suites — 29/29 passed, zero skipped.
- Architecture tests — 7/7 passed.
- `DirectMcpStartupWaitRemainsBoundedAndReportsPendingTools` — 1/1 passed in isolation.
- Node terminal parity — 24/24; Python terminal parity — 50/50.
- MCP conformance — no regression against its eight classified baseline failures.
- `test.pdf` was unchanged from the base commit.

The full local .NET invocation did not return a completion summary in this
runner. It emitted timing failures in the existing direct-MCP-startup and
llama.cpp manager TUI tests; the direct-MCP case passed when rerun alone. Final
exact-head Linux CI passed for strengthened source commit
`4287604b52cdb1896b7cd76a62711853f30b97e1`: push run
[39065424103](https://github.com/PhilipGB/pisharp/actions/runs/39065424103)
and PR run
[39065426738](https://github.com/PhilipGB/pisharp/actions/runs/39065426738).
Both runs pass 2,283/2,283 .NET tests with zero skips; their MCP conformance
jobs report no regression against the eight classified baseline failures.
The final receipt-head CI is tracked on draft PR #11.
