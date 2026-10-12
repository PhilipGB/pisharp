# MCP

[McpRuntime](../../src/PiSharp.Runtime/Mcp/McpRuntime.cs) selects configured and
extension-registered servers, starts their connections and publishes their tool
availability through the extension catalog. The official C# MCP SDK owns protocol
transport/client behavior. Pi-specific naming, exposure, auth/cache identity,
startup waiting and retry policy remain Runtime responsibilities.

## Ownership and flow

- [McpClientConnection](../../src/PiSharp.Runtime/Mcp/McpClientConnection.cs) opens
  SDK stdio/HTTP transports, applies provider or OAuth authentication, negotiates
  supported protocol versions and discovers tools/resources. It owns discovery
  refresh gates/pending notifications and transfers lifetime to `McpConnectedServer`.
  Failed initialization disposes created clients/transports and refresh gates.
- [McpToolPublication](../../src/PiSharp.Runtime/Mcp/McpToolPublication.cs) projects
  server tools into collision-safe names, exposure policy, namespaces, output
  schemas and Pi-compatible renderers, replacing a server's owned registration set.
- [McpToolFunction](../../src/PiSharp.Runtime/Mcp/McpToolFunction.cs) invokes the
  connection with its timeout and converts SDK result content, images, errors and
  structured output into the shared Pi tool result contract.
- [McpResourcePublication](../../src/PiSharp.Runtime/Mcp/McpResourcePublication.cs)
  owns the shared resource-server catalog and publication locks. Enabled visible
  servers contribute resource tools; collisions with another tool owner leave
  its registrations intact and produce the existing startup diagnostic.
- `McpServerConnection` and `McpConnectedServer` own reconnect and active client
  state. `McpRuntimeManager` handles live enable/disable/exposure changes;
  `McpStartupCoordinator` bounds initial waiting and waits for only the server
  reached by an indirect tool. `ExtensionCatalog` owns connection disposal.

Per-server publication locks still coordinate current configuration and client
identity with list-change callbacks. Resource catalog updates release their lock
before taking the publication lock; refresh snapshots the catalog under its lock.
This preserves the existing lock ordering across independent server callbacks.

## Compatibility and decisions

Configured server namespaces override matching extension registrations. MCP
configuration, SDK protocol/payload behavior, tool names/schemas, errors and public
runtime APIs remain compatible. Owners remain internal to Runtime with the same
SDK/package/project/assembly identities; no additional interface or project is
needed.

Startup waits for directly exposed servers while indirect servers may connect
later. Cancellation/disposal terminates owned startup work. Session-expiry recovery
and safe resource retries remain distinct from uncertain tool-call side effects;
transient tool calls must not be blindly replayed. Optional resource-list failure
continues as an empty discovery result while cancellation propagates. This is an
existing compatibility policy, not permission to swallow unrelated errors.

Provider auth resolves credentials for each HTTP request without creating MCP OAuth
credentials. OAuth discovery, refresh and token-cache lifetime stay with their
existing owners/SDK adapters. Connection diagnostics expose exception types rather
than potentially sensitive child/server error details; a pre-change process
characterization protects that boundary.

## Verification

MCP runtime/lifecycle, provider-auth, OAuth, pagination, identifier, token-cache and
protocol tests use real local child processes or HTTP fixtures, without live keys.
They protect list changes, startup bounds, live exposure, reconnect, retries,
authentication and tool/result projection.

```sh
dotnet test PiSharp.slnx --filter FullyQualifiedName~Mcp
dotnet build tools/McpConformance/PiSharp.McpConformance.csproj --warnaserror
node tools/parity/mcp-conformance/run.mjs
```

Credential and Radius MCP process differentials use the
[configured Pi reference](../parity/pi-reference.md). Official conformance retains
its reviewed historical expectation file; source restructuring must preserve its
per-scenario/per-check results and classified failures. Passing these checks
establishes covered behavior, not complete external server interoperability.
