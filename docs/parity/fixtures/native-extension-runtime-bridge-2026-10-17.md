# Native extension public runtime bridge — 2026-10-17

## Source and scope

- Base main: `5de6aac534ea1548f28f63999c81addcc8189f48`
- Product source: `6019c32cdcc65a2c17d59a8842d09f67c5f9526d`
- Branch: `milestone/native-extension-runtime-bridge`
- Pull request: [#8](https://github.com/PhilipGB/pisharp/pull/8), draft
- Configured Pi source: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`

This receipt covers the bounded native extension runtime bridge. It does not
claim full Pi extension parity or change broad extension capability statuses.

## Acceptance evidence

| Capability | Evidence |
| --- | --- |
| API-v1 compatibility | `ResourcePackageCommandProcessTests.ExternalPackageCompiledAgainstPr7ApiV1LoadsAndRunsWithTheAdditiveHost` copies the PR #7 contract snapshot, builds and packs a package without shipping a second abstractions assembly, installs it, then executes its original command, tool and before-agent-start callback in the current runtime. |
| Turn start and images | `NativeExtensionHookBridgeTests.PublicBeforeAgentStartContextCapturesCurrentTrustIdleAndPendingState` verifies user-turn type, working directory, idle/trust/pending values and public image media type/byte length. The loaded-package fixture distinguishes user/custom starts and verifies the injected custom message is durable once. |
| Injected messages and retry | `ExtensionBeforeAgentStartTests.ReturnedCustomMessageDoesNotDuplicateAcrossRetryOrNextUserTurn` covers provider retry and a later prompt. `TriggeredCustomMessageResumesFromDiskWithoutReplayingItsTurn` covers fresh session reload and branching. |
| User/custom sending and trigger behavior | `RpcModeTests.NativeCommandContextStartsNormalUserAndCustomTurnsThroughRpc` sends both message types through the normal RPC run path. `NativeCustomTriggerSentDuringBeforeAgentStartJoinsTheActiveRunOnce` verifies an active setup run owns provider execution and handles the queued custom message once. `ExtensionBeforeAgentStartTests.CustomMessageQueuedWhileBusyJoinsTheActiveRunWithoutAnotherStartEvent` covers an already-running provider request. |
| Cancellation and recovery | `ExtensionBeforeAgentStartTests` covers cancellation during queue persistence, custom-turn setup and provider execution, plus provider failure followed by an ordinary prompt. The public transform and hook bridge tests cover callback errors followed by a successful prompt. |
| Public live context | The compiled callback bridge exposes working directory, idle, project trust and pending-message snapshots; runtime state is read when each callback is invoked. |
| Resource discovery | The external fixture publicly registers skills, prompts and themes. `NativeExtensionCatalogTests.PublicResourceDiscoveryLoadsOnStartupAndReplacesResourcesOnReloadAndRemoval` checks startup/reload/removal. `ProjectNativePackageIsInspectedButNotLoadedBeforeTrust` verifies untrusted project packages contribute no commands or resources. |
| Context transforms | `NativeExtensionHookBridgeTests.PublicContextTransformsComposePerRequestWithoutChangingConversationHistory` checks ordered, request-local transformations in provider and tool-follow-up requests, including after compaction. The loaded-package fixture verifies transforms and tool-loop continuation. |
| Tool hooks | `NativeExtensionHookBridgeTests` verifies ordered argument mutation, blocking, result transformation and callback error recovery. The packaged fixture composes mutation and result transformation with a deterministic provider and real tool execution. |
| Reload, source ownership and unload | `NativeExtensionCatalogTests` verifies replacement removes v1 commands, tools, handlers, transforms, hooks and resources; package removal clears every contribution. The old load context becomes collectible after replacement, and remains alive only while an old callback is executing. |
| Package boundary | `ResourcePackageCommandProcessTests.DotnetPackedExternalNativeFixtureInstallsAndExecutesInFreshRuntime` covers normal `dotnet pack`, child-process installation and a fresh runtime. `PublicNativeFixtureCallbacksRunThroughLoadedPackagesAndV1CallbacksDisappearOnReload` composes the loaded package with deterministic agent/tool execution, then checks v2 behavior. |
| Persistence and display | Custom messages retain their custom role/type and display setting through session/RPC projection. Hidden custom context remains available to the model and is omitted from live and restored TUI transcript rendering. |

The feature is adapted onto the existing `ExtensionRegistration`, source
ownership, conversation run, queue, session persistence, resource catalog and
collectible load-context implementations. No second extension engine or
extension-specific session file was added.

## Local verification

- `dotnet restore PiSharp.slnx`: passed
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore`: passed
- `dotnet build PiSharp.slnx --no-restore --warnaserror`: passed, zero warnings
- `dotnet test PiSharp.slnx --no-build --no-restore`: **2,197 passed, zero skipped**
- Architecture tests: **7/7 passed**
- `npm test --prefix tools/parity/terminal`: **24/24 passed**
- `python3 -m unittest discover -s tools/parity/terminal -p 'test_*.py'`: **50/50 passed**
- `python3 tools/validate_doc_paths.py`: passed (**137 documents, 530 references**)
- MCP conformance build: passed with zero warnings
- `node tools/parity/mcp-conformance/run.mjs`: passed, **261 checks** and the same eight classified baseline failures; no new regression

## CI

- Push exact-head Linux CI: [run 38991202559](https://github.com/PhilipGB/pisharp/actions/runs/38991202559) — passed (`test` and `mcp-conformance`)
- Pull-request merge-result Linux CI: [run 38991226901](https://github.com/PhilipGB/pisharp/actions/runs/38991226901) — passed (`test` and `mcp-conformance`)

## Remaining boundaries

The global `feature-extensions-net-api` and `extension-orchestration-api`
ledger entries remain `in_progress`. Deferred surfaces include per-session
extension state, settings, extension UI and keybindings, provider/model
registration, structured prompt-option mutation, and broader extension
parity. Long-term MEAI binary compatibility remains an open concern; the
existing host-shared MEAI assembly behavior is retained.
