# CLI composition and command workflows

[Program](../../src/PiSharp.Cli/Program.cs) is the executable composition root.
It establishes process streams and HTTP/resource leases, parses startup input,
invokes project/model/conversation preparation, composes active bindings and
selects RPC, JSON, print or interactive execution. The three separate startup
error boundaries for provider discovery, project loading and chat-client selection
remain visible. Runtime owns shared agent execution and canonical conversations;
CLI owns configuration, provider adapters, modes and terminal workflows.

## Startup and conversation composition

- [CliTerminalIO](../../src/PiSharp.Cli/Startup/CliTerminalIO.cs) owns raw terminal
  streams, Linux terminfo initialization and stream disposal.
  [CliStartupCommands](../../src/PiSharp.Cli/Startup/CliStartupCommands.cs) handles
  early standalone commands, parsing/help/version, provider/model requirements
  and filtered model listings.
- [ProjectStartupPreparation](../../src/PiSharp.Cli/Startup/ProjectStartupPreparation.cs)
  resolves the invocation/session target, changes the working directory, loads
  trust/configuration and applies startup defaults. Its result contains only this
  phase's configuration, arguments and startup decisions. Program owns the returned
  configuration lease. Provider discovery still precedes project runtime loading.
- [InitialModelComposition](../../src/PiSharp.Cli/Composition/InitialModelComposition.cs)
  resolves initial model/thinking preferences and constructs the initial agent,
  compaction and pricing binding. Chat-client selection and extension transfer
  remain explicit in Program. Initial and saved-model preparation retain their
  distinct validation and failure ordering.
- [SessionStartupController](../../src/PiSharp.Cli/Sessions/SessionStartupController.cs)
  resolves paths, loads/forks/imports documents and selects saved model metadata.
  [ConversationStartup](../../src/PiSharp.Cli/Sessions/ConversationStartup.cs) uses
  those operations to restore the saved execution configuration, open the initial
  run and commit name/fork persistence before publishing the active bindings.
- [ConversationRunComposition](../../src/PiSharp.Cli/Composition/ConversationRunComposition.cs)
  owns PiSharp's run-opening policy: current or explicitly overridden project/model,
  persistence destination, compaction/pricing/retry, prompt delivery and tools.
  Bindings are read at opening; the save callback stays bound to that opened run's
  project, conversation and path.
- [ModeInputPreparation](../../src/PiSharp.Cli/Startup/ModeInputPreparation.cs)
  combines redirected-input mode selection, bounded stdin reading and authentication
  gating. RPC retains ownership of its input stream.

## Modes and terminal workflows

[RpcModeComposition](../../src/PiSharp.Cli/Protocols/Rpc/RpcModeComposition.cs)
composes protocol catalog/settings callbacks and session transitions around RpcMode.
[JsonPromptMode](../../src/PiSharp.Cli/Protocols/Json/JsonPromptMode.cs) retains JSON
prompt expansion, attachments and final save ordering.
[PrintPromptMode](../../src/PiSharp.Cli/Startup/PrintPromptMode.cs) owns print prompt
preparation and rejection. [InteractiveMode](../../src/PiSharp.Cli/Tui/InteractiveMode.cs)
composes concrete command owners and runs the editor/command loop. Its direct switch
makes command routing explicit; it does not implement another execution loop.

[TerminalPresentation](../../src/PiSharp.Cli/Tui/Rendering/TerminalPresentation.cs)
owns theme resolution, editor/screen/input-mode setup, startup headers, footers,
transcript projections and MCP authorization presentation. Mode cleanup precedes
screen cleanup, including failed construction. Program owns its lease inside the
extension and HTTP leases. [TerminalResourceCompletion](../../src/PiSharp.Cli/Tui/Editor/TerminalResourceCompletion.cs)
reads current resources/extensions when completing and describing slash commands.

[TerminalConversationExecution](../../src/PiSharp.Cli/Tui/Commands/TerminalConversationExecution.cs)
owns prompt/bash cancellation, active transcripts and settlement: stop input
monitoring, save the current conversation even after failure, then clear active UI.
It uses [InteractivePromptExecution](../../src/PiSharp.Cli/Tui/Commands/InteractivePromptExecution.cs)
for resource expansion and event presentation. Both call ConversationRun directly.

[TerminalSettingsCommands](../../src/PiSharp.Cli/Tui/Commands/TerminalSettingsCommands.cs)
applies persisted settings to execution and presentation in their existing order.
[TerminalModelSelectionCommands](../../src/PiSharp.Cli/Tui/Commands/TerminalModelSelectionCommands.cs)
owns picker/default persistence, cycling and model status.
[TerminalSessionNavigation](../../src/PiSharp.Cli/Sessions/TerminalSessionNavigation.cs)
owns resume/fork/new/clone selection, binding adoption, history and draft updates.
[TerminalInputActions](../../src/PiSharp.Cli/Tui/Input/TerminalInputActions.cs) coordinates
external editing and clipboard actions. [TerminalApplicationActions](../../src/PiSharp.Cli/Tui/Commands/TerminalApplicationActions.cs)
routes editor shortcuts to those concrete owners and retains shortcut error/footer
settlement. Provider, project, import, resource and session command owners retain
their existing capability workflows.

[TerminalSessionShare](../../src/PiSharp.Cli/Sessions/TerminalSessionShare.cs) binds
`/share` to the current conversation and effective settings. It owns terminal
progress, cancellation and editor restoration.
[SessionShare](../../src/PiSharp.Cli/Sessions/SessionShare.cs) owns isolated temporary
exports, destination selection, Radius HTTP upload and the GitHub CLI child lifetime.
Explicit gist selection bypasses Radius authentication; forced Radius without a
credential fails before GitHub is invoked. Handled Radius failures do not fall back.
Credential resolution reuses the existing provider OAuth refresh window.

## Active state and transitions

[ActiveModelRuntime](../../src/PiSharp.Cli/Composition/ActiveModelRuntime.cs),
[ActiveProjectRuntime](../../src/PiSharp.Cli/Composition/ActiveProjectRuntime.cs) and
[ActiveConversationRuntime](../../src/PiSharp.Cli/Composition/ActiveConversationRuntime.cs)
retain the current execution, project and conversation bindings. Callbacks read
those bindings after asynchronous discovery and adoption.

[ConversationModelTransitions](../../src/PiSharp.Cli/Composition/ConversationModelTransitions.cs)
prepares model/thinking replacement and publishes its agent/run only after the
existing [ConversationModelReplacement](../../src/PiSharp.Cli/Sessions/ConversationModelReplacement.cs)
branch/checkpoint transaction succeeds. Active-run replacement still uses the
existing ModelRuntimeController gates.
[ProjectConversationTransitions](../../src/PiSharp.Cli/Composition/ProjectConversationTransitions.cs)
owns resource reload and cross-project adoption, extension transfer, run preparation,
session-controller rebinding and failure cleanup. Explicit presentation callbacks
preserve UI refresh before execution publication and history reload afterward.
[ProjectResourceReload](../../src/PiSharp.Cli/Sessions/ProjectResourceReload.cs) still
reads current arguments/settings/tools after configuration loading.

These owners are concrete collaborators inside CLI. There is no additional project,
public API migration, framework wrapper, service locator, generic command bus or
mutable application context. The startup result types describe bounded preparation
phases; they do not accumulate application services.

## Verification

[StartupOrderingProcessTests](../../tests/PiSharp.Tests/Cli/Startup/StartupOrderingProcessTests.cs)
characterizes configuration-before-provider precedence, initial name persistence,
redirected authentication gating and RPC input ownership.
[ConversationCompositionTests](../../tests/PiSharp.Tests/Cli/Sessions/ConversationCompositionTests.cs)
covers run settings/tools/persistence binding and reload publication/failure ordering.
Existing informational/startup, replacement rollback, project/resource, JSON/RPC
process and [TerminalPtyTests](../../tests/PiSharp.Tests/Cli/Tui/TerminalPtyTests.cs)
retain their scenarios, including terminal cleanup and cancellation. Full terminal
differentials use the [frozen Pi reference](../parity/pi-reference.md); passing
covered checks does not establish complete Pi parity.
