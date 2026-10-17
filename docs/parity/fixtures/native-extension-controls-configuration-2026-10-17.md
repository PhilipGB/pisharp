# Native extension controls and configuration receipt — 2026-10-17

## Scope

This bounded implementation increment starts from main
`15b7acceb9c41a5b278b005ac5df6dadad7cda48` on
`milestone/native-extension-controls-configuration`. The configured Pi source
remains pinned at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Product source is
`c9d3f078fa0dba6a487bc178708667c7e562b64d`. Draft PR [#10](https://github.com/PhilipGB/pisharp/pull/10)
is open. Exact-head push CI
[39051662051](https://github.com/PhilipGB/pisharp/actions/runs/39051662051)
and PR merge-result CI
[39051669964](https://github.com/PhilipGB/pisharp/actions/runs/39051669964)
both pass, including the full .NET suite and MCP conformance. This receipt
records implementation evidence only; it does not promote the broad extension
API or orchestration rows and is not a global parity audit.

The public compiled-extension contracts now cover fresh read-only effective
settings JSON snapshots; Boolean/string CLI flags with typed defaults and
source ownership; session names and entry labels; current command and tool
projections; session-local active tools, model and thinking selection; abort,
compaction and graceful shutdown; and ordered request-local
`before_agent_start` prompt sections. These APIs are additive under
`PiSharp.Extensions.Abstractions`; the package fixture references no Runtime or
CLI assembly.

## Pinned Pi references inspected

The reference SHA is immutable for this milestone. Relevant source locations:

- [`extensions/types.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/types.ts): extension command API contracts, tool exposure and active-tool semantics, prompt-option mutation ordering, and session/compaction lifecycle types.
- [`extensions/runner.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/runner.ts) and [`extensions/loader.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/loader.ts): ordered callbacks, request-local event flow, registration ownership, replacement and unload behavior.
- [`agent-session.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/agent-session.ts): session model/thinking controls, active tools, abort/compact/shutdown ownership.
- [`session-manager.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/session-manager.ts), [`settings-manager.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/settings-manager.ts), and [`model-registry.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/model-registry.ts): session metadata, effective settings, and model identity/selection owners.
- [`extension guide`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/docs/extensions.md): public usage and extension CLI flag behavior.

PiSharp routes these controls through its current settings snapshot provider,
session store, tool loadout, model runtime controller, conversation run,
compaction machinery, shutdown path, and system-prompt builder. It does not add
parallel registries or provider-facing general hooks.

## Acceptance evidence

- `NativeExtensionSettingsFlagContextTests` covers fresh snapshots, defaults,
  typed flags and extension ownership.
- `NativeExtensionCatalogTests` covers untrusted project flags, deterministic
  flag conflicts, reload reconciliation, package removal and old package load
  context collection.
- `NativeExtensionStateProcessTests.ExternalPackageReadsFlagsAndSettingsAndPersistsSessionMetadataAcrossGracefulShutdown`
  packs and installs the normal external fixture; it checks project-effective
  settings, default and explicit flags, session name/label save and resume,
  label clearing, compaction result, and the graceful shutdown lifecycle at a
  real CLI process boundary.
- `NativeExtensionControlsProcessTests.ExternalPackageControlsDriveTheNextProviderRequest`
  uses a deterministic local provider and verifies that active tools, selected
  model, thinking level and structured prompt changes reach the next request;
  it also checks deterministic unknown-model and unavailable-auth results and
  settings defaults in a fresh process.
- `RpcExtensionControlAbortTests` checks idle abort, active cancellation,
  settlement event and a later successful prompt through the RPC extension
  context.
- `PiSharpExtensionCompactionTests` verifies that the public compact operation
  uses the owning conversation run and persists the normal summary/history.
- API-v1, PR #8 and PR #9 compatibility fixtures remain in the full native
  extension loader/catalog suite.

Extension CLI flags are executable registrations, so they are discovered only
after the existing host trust decision allows the package to run. Untrusted
project packages contribute no flags. Startup still rejects unrelated unknown
options after trusted extension registrations are known. Reload replaces flag
ownership with the package source, and removal drops its flags together with
its other registrations.

## Deferred boundary

Provider registration and OAuth/custom streams, extension MCP registration,
virtual models, the full event subscription surface, advanced UI and custom
renderers, Markdown transforms, generic exec/user-bash interception, session
switch/new/fork initiation, dynamic registration, long-term MEAI binary
compatibility redesign, and the final broad extension audit remain deferred.

## Verification

Local verification passes format checking, a warnings-as-errors solution build,
the full .NET suite (**2,268/2,268**, zero skips), architecture checks
(**7/7**), Node (**24/24**), Python (**50/50**), and documentation paths
(**139 documents, 536 references**). MCP conformance passes with no regressions
against the classified baseline (**261 passing checks; the same eight known
failures**). The first full-suite attempt hit the existing ephemeral-port
reservation race in `InteractiveMcpProcessTests`; that test passed in
isolation and the complete rerun passed. Local restore uses
`-p:NuGetAudit=false` because this environment's NuGet audit cache is
read-only; the ordinary restore also exits successfully but reports NU1900 for
that cache write.

The source checks above pass on the exact product head. A later documentation-
only commit records this evidence; its own push and PR checks are tracked by the
same draft PR. Broad extension status remains `in_progress`.
