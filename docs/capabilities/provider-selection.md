# Provider and model selection

[ProviderModelRuntime](../../src/PiSharp.Cli/Providers/ProviderModelRuntime.cs) composes
provider profiles, catalogs, scopes and physical/virtual model selection for CLI,
terminal and RPC modes. Provider-specific adapters/configuration stay in CLI;
Runtime owns shared model metadata, routing and agent execution.

## Ownership and flow

- [ProviderCredentialResolver](../../src/PiSharp.Cli/Providers/ProviderCredentialResolver.cs)
  owns stored/env/configured/command-line credential precedence, OAuth refresh
  policy, classifier access and Llama router URL resolution. An explicit runtime
  key binds to the first selected provider; unrelated providers cannot borrow it.
  MCP provider-token resolution excludes that command-line override.
- [AvailableModelSnapshot](../../src/PiSharp.Cli/Providers/AvailableModelSnapshot.cs)
  owns synchronized available-model snapshots and the interactive provider count.
  Full/provider-specific refreshes replace their existing scopes and deduplicate
  provider/model identities. Selection records availability separately from catalog
  discovery, preserving Pi's footer count behavior.
- [LlamaRouterDiscovery](../../src/PiSharp.Cli/Providers/Llama/LlamaRouterDiscovery.cs)
  serializes router refresh/cache writes and owns the current discovered profile.
  It preserves offline restore and safe cached fallback; explicit refresh also
  updates the shared model snapshot. Router credentials/URL are resolved before
  discovery, retaining existing read/precedence semantics.
- `ProviderModelRuntime` builds profiles and OAuth adapters, coordinates catalog
  lookup and scoped physical/virtual selection, protects endpoint authority, and
  supplies provider-specific connection metadata/token resolvers to consumers.
  Existing `ModelCatalog`, `RadiusModelCatalog`, `LlamaRouterCatalog` and
  `ProviderProfileLoader` own their protocol/configuration formats.

These concrete owners remain inside CLI. No new project/interface is justified;
there is no opaque options bag or wrapper hiding MAF/MEAI. Public provider/model
runtime APIs, profile/selection records, configuration and assembly identity stay
compatible. Auth storage/OAuth adapters retain their persistence/network contracts.

## Constraints

A stored credential owns its provider and blocks fallback to ambient credentials,
including unsupported OAuth/API-key combinations. Built-in credentials cannot be
redirected to another authority or borrowed by a custom provider. Provider-specific
OAuth, workload identity, Vertex and Bedrock ambient identity rules stay explicit.
Classifier access uses the selected provider's identity/environment and MCP tokens
exclude anonymous credential markers.

Scope patterns preserve pattern order and deduplicate matches. Exact configured
models can be selected without generic network discovery; virtual models retain
logical state and physical provider routing. Provider-specific manager lookups do
not implicitly change the interactive provider-count snapshot. Failed discovery
keeps the existing unavailable/catalog-cache behavior and redacts credentials.
Llama router cached capabilities/classifiers and inference endpoint normalization
remain parity-sensitive.

## Verification

[ProviderModelRuntimeTests](../../tests/PiSharp.Tests/Cli/Providers/ProviderModelRuntimeTests.cs)
protect credential precedence/isolation, endpoint authority, catalog scopes,
configuration and process selection. The command-line provider/classifier binding
case was characterized before extraction. Llama router, classifier auth, scope,
RPC active selection, provider OAuth and MCP provider-auth tests use deterministic
local fixtures.

```sh
dotnet test PiSharp.slnx --filter FullyQualifiedName~ProviderModelRuntimeTests
```

Llama classifier/router/context, virtual-routing and MCP credential differentials
use the [configured Pi reference](../parity/pi-reference.md). Full calibrated
terminal startup, provider/model/manager selectors and auth flows protect rendering
and snapshot behavior. Run complete repository/parity verification after changes
that affect shared selection or visible snapshots. Known evidence gaps remain
recorded; passing covered tests do not establish complete provider/Pi parity.
