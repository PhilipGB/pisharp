# Native extension public runtime bridge — 2026-10-17

## Source and scope

- Base main: `5de6aac534ea1548f28f63999c81addcc8189f48`
- Product source: `6019c32cdcc65a2c17d59a8842d09f67c5f9526d`
- Acceptance hardening: `103222187a2973dbd324b899bea3ca53586c41f2`
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
| Injected messages and retry | `ExtensionBeforeAgentStartTests.ReturnedCustomMessageDoesNotDuplicateAcrossRetryOrNextUserTurn` covers returned-message retry and a later prompt. `TriggeredCustomMessageExecutesOnceAcrossAutomaticProviderRetry` covers one custom-trigger turn across provider retry. |
| User/custom sending and trigger behavior | `RpcModeTests.NativeCommandContextStartsNormalUserAndCustomTurnsThroughRpc` sends both message types through the normal RPC run path. `NativeCustomTriggerSentDuringBeforeAgentStartJoinsTheActiveRunOnce` verifies an active setup run owns provider execution and handles the queued custom message once. `ExtensionBeforeAgentStartTests.BusyCustomMessageWithoutTriggerIsAppendedAfterToolResultsAndSeenByNaturalContinuation` verifies a context-only message follows the tool result into the natural tool-loop continuation without creating another request; `BusyNextTurnCustomMessageWaitsForTheNextRunAfterItsStartingPrompt` and `IdleNextTurnCustomMessageWaitsAfterTheNextStartingPromptWithoutStartingARun` check aside staging and order for busy and idle runs. |
| Cancellation and recovery | `ExtensionBeforeAgentStartTests` covers cancellation during queue persistence, custom-turn setup and provider execution, plus provider failure followed by an ordinary prompt. The public transform and hook bridge tests cover callback errors followed by a successful prompt. |
| Public live context | The compiled callback bridge exposes working directory, idle, project trust and pending-message snapshots; `NativeExtensionHookBridgeTests.PublicBeforeAgentStartContextCapturesPendingMessagesAddedDuringSetup` verifies a later callback sees newly queued work. |
| Resource discovery | The external fixture publicly registers skills, prompts and themes. `NativeExtensionCatalogTests.PublicResourceDiscoveryLoadsOnStartupAndReplacesResourcesOnReloadAndRemoval` checks startup/reload/removal. `ProjectNativePackageIsInspectedButNotLoadedBeforeTrust` verifies untrusted project packages contribute no commands or resources. |
| Context transforms | `NativeExtensionHookBridgeTests.PublicContextTransformsComposePerRequestWithoutChangingConversationHistory` checks ordered, request-local transformations in provider and tool-follow-up requests, including after compaction. The loaded-package fixture verifies transforms and tool-loop continuation. |
| Tool hooks | `NativeExtensionHookBridgeTests` verifies ordered argument mutation, blocking, result transformation and callback error recovery. The packaged fixture composes mutation and result transformation with a deterministic provider and real tool execution. |
| Reload, source ownership and unload | `NativeExtensionCatalogTests` verifies replacement removes v1 commands, tools, handlers, transforms, hooks and resources; package removal clears every contribution. The old load context becomes collectible after replacement, and remains alive only while an old callback is executing. |
| Package boundary | `ResourcePackageCommandProcessTests.DotnetPackedExternalNativeFixtureInstallsAndExecutesInFreshRuntime` covers normal `dotnet pack`, child-process installation and a fresh runtime. `PublicNativeFixtureCallbacksRunThroughLoadedPackagesAndV1CallbacksDisappearOnReload` composes the loaded package with deterministic agent/tool execution, then checks v2 behavior. |
| Persistence, compaction and restart | `ExtensionBeforeAgentStartTests.TriggeredCustomMessageResumesFromDiskWithoutReplayingItsTurn` checks compaction, resume and branching. `ResourcePackageCommandProcessTests.ExtensionGeneratedCustomTurnResumesInAFreshProcessWithoutReplayingTrigger` uses separate create/resume processes, confirms the persisted custom type, verifies no trigger on runtime startup, and completes an ordinary follow-up. Custom display settings remain covered through session/RPC projection and restored TUI transcript rendering. |

The feature is adapted onto the existing `ExtensionRegistration`, source
ownership, conversation run, queue, session persistence, resource catalog and
collectible load-context implementations. No second extension engine or
extension-specific session file was added.

## Local verification

- `dotnet restore PiSharp.slnx`: passed
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore`: passed
- `dotnet build PiSharp.slnx --no-restore --warnaserror`: passed, zero warnings
- `dotnet test PiSharp.slnx --no-build --no-restore`: **2,200 passed, zero skipped**
- Architecture tests: **7/7 passed**
- `npm test --prefix tools/parity/terminal`: **24/24 passed**
- `python3 -m unittest discover -s tools/parity/terminal -p 'test_*.py'`: **50/50 passed**
- `python3 tools/validate_doc_paths.py`: passed (**137 documents, 530 references**)
- MCP conformance build: passed with zero warnings
- `node tools/parity/mcp-conformance/run.mjs`: passed, **261 checks** and the same eight classified baseline failures; no new regression

## CI

- Push exact-head Linux CI for acceptance source `10322218`: [run 38993113554](https://github.com/PhilipGB/pisharp/actions/runs/38993113554) — passed (`test` and `mcp-conformance`)
- Pull-request merge-result Linux CI for acceptance source `10322218`: [run 38993115988](https://github.com/PhilipGB/pisharp/actions/runs/38993115988) — passed (`test` and `mcp-conformance`)

## Custom-message delivery review correction

The review follow-up preserves the pinned Pi state model at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`: `triggerTurn: false` during an
active run queues context-only insertion until the active turn's tool results
are recorded. The message is then included in the next provider request when
the tool loop naturally continues, without scheduling its own request.
`NextTurn` remains an aside until a later run and follows that run's starting
message. Idle `triggerTurn: false` appends to existing context, while idle
`NextTurn` starts no run and is appended after the next starting message.

The natural-continuation regression checks the tool result precedes the custom
message in provider request two, confirms the run uses exactly two requests
(the first returned the tool call), and confirms a later ordinary prompt still
sees the single durable custom-message entry.

Focused `ExtensionBeforeAgentStartTests` pass **15/15**. Format verification
passes; the warnings-as-errors solution build passes with zero warnings; the
full .NET suite passes **2,204/2,204**, zero skipped, using `TMPDIR=/tmp` for
the sandbox's temp-path-sensitive fixtures. Documentation paths verify **137
documents / 531 references**. Exact-head CI for the correction passes on source
`d0ee1611ce1aa4008beaf79269cd12ee611e39ed`: [push run
39021311731](https://github.com/PhilipGB/pisharp/actions/runs/39021311731)
and [PR merge-result run
39021313792](https://github.com/PhilipGB/pisharp/actions/runs/39021313792)
both pass `test` (2,204/2,204, zero skipped) and `mcp-conformance`.

## Remaining boundaries

The global `feature-extensions-net-api` and `extension-orchestration-api`
ledger entries remain `in_progress`. Deferred surfaces include per-session
extension state, settings, extension UI and keybindings, provider/model
registration, structured prompt-option mutation, and broader extension
parity. Long-term MEAI binary compatibility remains an open concern; the
existing host-shared MEAI assembly behavior is retained.
