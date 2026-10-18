# Native extension input and user-bash milestone — 2026-10-18

This scoped increment is based on PR #14's actual merge `1da1eb9a00ecdcfeaab1b0eb1ba35191e4893a2e` and keeps the pinned Pi reference at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. The implementation adds native input and user-bash contracts to the compiled extension API, then routes them through the existing TUI, RPC, conversation, shell, and package-lifecycle owners. It does not change the broad extension parity status.

Pinned reference files reviewed:

- `packages/coding-agent/src/core/extensions/types.ts`
- `packages/coding-agent/src/core/extensions/runner.ts`
- `packages/coding-agent/src/core/agent-session.ts`
- `packages/coding-agent/src/core/bash-executor.ts`
- `packages/coding-agent/src/core/tools/bash.ts`
- `packages/coding-agent/src/modes/interactive/interactive-mode.ts`
- `packages/coding-agent/src/modes/rpc/rpc-mode.ts`
- Relevant tests under `packages/coding-agent/test/`

## Contract matrix

| Surface | Pinned Pi | PiSharp | Evidence |
| --- | --- | --- | --- |
| Interactive idle input | Interactive submissions fire `input` before resource expansion and run/preflight. | TUI idle submissions dispatch with `Interactive` and no streaming behavior; handled input returns before auth, expansion, and a run. | `InteractivePromptExecutionInputTests.HandledIdleInputReturnsBeforeAuthenticationResourceResolutionAndRun`; Linux PTY external-package test below. |
| RPC input | RPC prompts, `steer`, and `follow_up` are input events with source and queue behavior. | `prompt`, `steer`, and `follow_up` dispatch before expansion and use the existing queue; prompt-with-streaming-behavior is dispatched once. | `RpcInputRoutingTests.HandledIdlePromptSucceedsBeforePreflightAndRun`, `RawInputTransformsBeforeResourceExpansionAndTransformedCommandIsNotRedispatched`, and `ActiveRpcPromptSteerFollowUpAndExtensionMessagesUseTheInputDispatcherAndExistingQueues`. |
| Extension input | Extension-originated user messages use source `extension` and normal input handlers. | The existing extension message owner dispatches source `Extension` with its active steering behavior, then resolves and queues through the existing run. | `RpcInputRoutingTests.ActiveRpcPromptSteerFollowUpAndExtensionMessagesUseTheInputDispatcherAndExistingQueues`. |
| Transform chaining and handled | Transforms feed later handlers; `handled` stops the chain and agent path. | Ordered native handlers carry transformed text/images forward; handled short-circuits; extension-command recognition is not repeated after transform. | `NativeInputAndUserBashDispatchTests.InputTransformsChainAcrossHandlersAndPreserveOrClearImagesExplicitly`, `InputHandlerFailureKeepsPriorTransformAndHandledStopsDispatch`; `RpcInputRoutingTests.RawInputTransformsBeforeResourceExpansionAndTransformedCommandIsNotRedispatched`. |
| Images | An omitted transform image collection preserves prior images; explicit empty images clear them. | Public result distinguishes omitted images from `[]`; callback images are detached `DataContent`; existing image validation remains after interception. | `NativeInputAndUserBashDispatchTests.InputTransformsChainAcrossHandlersAndPreserveOrClearImagesExplicitly`; `InteractivePromptExecutionInputTests.TransformedInputPreservesOmittedImagesAndClearsExplicitlyEmptyImages`; RPC image assertions in `RpcInputRoutingTests.ActiveRpcPromptSteerFollowUpAndExtensionMessagesUseTheInputDispatcherAndExistingQueues`. |
| Input callback errors/cancellation | Callback errors are source-attributed and continue with prior effective input; cancellation propagates and revokes callback capabilities. | Input errors report against the extension and continue; cancellation propagates without queueing; callback-scoped context is revoked. | `NativeInputAndUserBashDispatchTests.InputHandlerFailureKeepsPriorTransformAndHandledStopsDispatch`, `InputCancellationRevokesCallbackContextAndPropagates`. |
| Resource expansion ordering | Input sees raw text; transformed text is expanded once by the normal resolver. | TUI and RPC resolve only after input dispatch; transformed slash-prefixed text is not sent back through command dispatch. | `RpcInputRoutingTests.RawInputTransformsBeforeResourceExpansionAndTransformedCommandIsNotRedispatched`; external-package process checks provider receives only the final chained transform. |
| Auth/preflight ordering | A handled input does not require a provider request; continued input keeps ordinary validation. | Handled TUI/RPC input returns before model preflight, while continued RPC input follows existing preflight and continued TUI input keeps image/auth checks. | `InteractivePromptExecutionInputTests.HandledIdleInputReturnsBeforeAuthenticationResourceResolutionAndRun`; `RpcInputRoutingTests.HandledIdlePromptSucceedsBeforePreflightAndRun`; external RPC package process. |
| Steering/follow-up | Active submissions preserve `steer` versus `followUp` semantics and existing queue ownership. | TUI active input uses `Interactive` plus the editor's steer/follow-up mode; RPC prompt, explicit queue commands, and extension messages map to the same existing queue. | `InteractivePromptExecutionInputTests.ActiveInputInterceptionTransformsSteeringAndFollowUpAndConsumesHandledInput`; `RpcInputRoutingTests.ActiveRpcPromptSteerFollowUpAndExtensionMessagesUseTheInputDispatcherAndExistingQueues`. |
| User-bash precedence and metadata | First non-null handler result wins; event includes exact command, cwd, and context-exclusion flag. | Native and legacy handlers use deterministic registration order; first result wins, preserving `!`/`!!` and RPC metadata. | `NativeInputAndUserBashDispatchTests.UserBashUsesFirstNonNullHandlerAcrossNativeAndLegacyRegistrations`; `RpcModeTests.UserBashHandlerCanStreamAndReplaceDirectBash`; `NativeExtensionInputBashProcessTests.ExternalPackageReplacesInteractiveBangCommandsWithoutRunningLocalShellThroughLinuxPty`. |
| User-bash errors | A handler exception terminates that command; no later handler or shell fallback runs. | Emits source-aware `extension_error` and fails closed in TUI/RPC; the model bash tool does not enter this dispatcher. | `NativeInputAndUserBashDispatchTests.UserBashHandlerFailureIsSourceReportedAndDoesNotFallThrough`, `ModelInvokedBashToolDoesNotDispatchUserBashHandlers`; `RpcModeTests.UserBashHandlerFailureIsReportedAndDoesNotFallThroughToTheShell`; external RPC process. |
| Full result replacement | A full result replaces local shell execution and retains exit/cancel/truncated metadata. | Full result bypasses local shell and is shown in TUI/RPC, then enters canonical bash history with its metadata. | External package `ExternalPackageInterceptsRpcInputAndReplacesUserBashInFreshProcess`; PTY `ExternalPackageReplacesInteractiveBangCommandsWithoutRunningLocalShellThroughLinuxPty`. |
| Custom operations/output | Custom operations receive the host command, cwd, output sink, and cancellation token. | Existing shell output accumulator, configured command prefix, normalization, cancellation, display updates, and result assembly are reused. | `BashOperationsTests.CustomOperationUsesPrefixedCommandAndHostOutputPipeline`, `CustomOperationOutputUsesNormalTruncationAndFullOutputCapture`, `CallerCancellationReachesCustomOperationAndReturnsCapturedCancellationResult`; `RpcModeTests.CustomUserBashOperationUsesHostPipelineAndPersistsOnce`. |
| Bash cancellation / `abort_bash` | The active user operation is cancellable and the RPC abort command remains its owner. | Custom operation cancellation flows through host CTS; RPC emits one cancelled result and persists through the shared session path. | `BashOperationsTests.AbortBashCancelsCustomOperation`; `RpcModeTests.AbortBashCancelsAnActiveUserBashOperation`, `AbortBashCancelsTheActiveRpcCommandAndReturnsCancelledResult`. |
| Active-run bash persistence | Results produced during a prompt are deferred until the active run settles, then appended once. | `ConversationRun.RecordBashResult` defers active-run bash entries and flushes them on settlement; direct RPC and TUI keep their existing persistence owners. | `RpcModeTests.UserBashResultDuringActiveRunPersistsOnceAfterSettlement` verifies no pre-settlement entry and one complete result after settlement. |
| Package rollback/reload/ALC | Replacement/removal disposes registrations and releases package contexts after callbacks settle. | Input/user-bash registrations are owned by package source and reconciled with other registrations; v1 binary extensions load without registering the new APIs. | `NativeExtensionCatalogTests.CatalogReloadReconcilesAndUnloadsReplacedNativePackageContext`, `PublicNativeFixtureCallbacksRunThroughLoadedPackagesAndV1CallbacksDisappearOnReload`; `NativeExtensionInputBashProcessTests.ExternalPackageInterceptsRpcInputAndReplacesUserBashInFreshProcess`; legacy `NativeExtensionFixture` compile/load coverage. |
| External package | Compiled extensions depend on the stable public Abstractions package. | Fresh CLI processes install and exercise a `.nupkg` that references `PiSharp.Extensions.Abstractions` only, with no Runtime/CLI reference or embedded Abstractions assembly. | `NativeExtensionInputBashProcessTests` external-package RPC and Linux PTY tests. |

## Verification record

The pre-milestone PR #14 exact-head CI recorded 2,401 passing tests with no failures or skips. The first local post-merge baseline run reported two temp-directory-sensitive failures; both passed when rerun with `TMPDIR=/tmp`, and a broader local rerun at that baseline was interrupted before producing a result. This is recorded rather than presented as a clean local baseline.

Product source commit: `ed06a4c369197193280a55fc02e4cb9a67ec5966`.

Local verification on that product source passed:

- `dotnet restore PiSharp.slnx -p:NuGetAudit=false`: all projects up to date.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore`: passed.
- `dotnet build PiSharp.slnx --no-restore --warnaserror`: 0 warnings, 0 errors.
- `dotnet test PiSharp.slnx --no-build --no-restore`: 2,426 passed, 0 failed, 0 skipped.
- Architecture tests: 7/7.
- Timing-sensitive input cancellation/handled/queue, user-bash cancellation/active persistence, and package reload/ALC cases: six cases passed in each of 10 runs (60/60).
- Node terminal tests: 24/24; Python terminal parity tests: 50/50.
- Documentation path validation: 144 documents and 558 references.
- MCP conformance: no new failures; the same eight classified failures remain (six elicitation checks, basic CIMD client-id behavior, and scope retry-limit behavior).

The Linux PTY input test verifies that an unauthenticated handled input creates no provider request, a later input is transformed through both handlers before a loopback Responses request, and the terminal returns to idle before shutdown. The interactive bash PTY test verifies both `!` and `!!` replacement results, no local shell marker, preserved `excludeFromContext`, and no input-handler dispatch. Active TUI steering/follow-up and handled behavior, and active-run RPC bash deferral, also have deterministic in-process tests.

Exact-head publication remains pending:

- Final branch head: pending docs commit and push.
- Draft PR URL: pending.
- Push CI run and exact-head PR CI run: pending.

Broad extension API/orchestration parity remains `in_progress`. Deferred surfaces include provider wire hooks, message lifecycle mutation, public `exec`, advanced UI/renderers, remaining tree/fork overrides, dynamic registration, provider OAuth/custom streams, TypeScript extensions, and the global parity audit.
