# CLI composition and command workflows

[Program](../../src/PiSharp.Cli/Program.cs) is the executable composition root.
It loads configuration, selects provider adapters, composes shared Runtime agents
and wires terminal, JSON and RPC modes. Runtime keeps the agent loop and canonical
conversation state; CLI owns configuration, provider adapters and presentation.

## Startup and modes

- [CliTerminalIO](../../src/PiSharp.Cli/Startup/CliTerminalIO.cs) owns raw terminal
  streams, Linux terminfo initialization and stream disposal. Redirected streams
  keep Console behavior; raw streams borrow their process file descriptors.
- [CliStartupCommands](../../src/PiSharp.Cli/Startup/CliStartupCommands.cs) handles
  auth, MCP and standalone export dispatch, argument errors, version/help and the
  explicit provider/model requirement before configuration is read.
- [SessionStartupController](../../src/PiSharp.Cli/Sessions/SessionStartupController.cs)
  resolves startup session paths, loads/forks/imports documents, restores registered
  virtual models and validates saved provider/model/thinking choices. It returns
  the selected document/model; Program constructs and adopts the runtime.
- [JsonPromptMode](../../src/PiSharp.Cli/Protocols/JsonPromptMode.cs) owns JSON
  prompt expansion, attachment processing, rejection and final save ordering.
  [JsonEventMode](../../src/PiSharp.Cli/Protocols/JsonEventMode.cs) retains the
  shared LF-framed lifecycle contract. RPC retains its existing command owners.

## Terminal workflows

[InteractivePromptExecution](../../src/PiSharp.Cli/Tui/InteractivePromptExecution.cs)
validates prompt capabilities, expands resources, monitors steering/follow-up input
and presents lifecycle events. Its explicit current-run/resource accessors preserve
runtime replacement during application actions. Program owns cancellation and the
active transcript, stops the monitor before saving, then clears active UI state.
Both paths call ConversationRun directly.

[InteractiveSessionCommands](../../src/PiSharp.Cli/Sessions/InteractiveSessionCommands.cs)
owns export, search, guarded deletion, tree/branch selection, naming and statistics.
Confirmation input and transcript reload are explicit UI callbacks. It receives the
current conversation/run/path on each command, including after project switches.
Existing session controllers retain resume/fork/new/clone behavior.

[ConversationModelReplacement](../../src/PiSharp.Cli/Sessions/ConversationModelReplacement.cs)
owns the idle model/thinking branch transaction: append metadata, open the next
execution, checkpoint it, and restore the selected head/model metadata on failure.
Open/checkpoint delegates are explicit execution/persistence boundaries. Program
composes the next agent and passes its prepared policy/pricing to OpenRunAsync;
root runtime fields are published only after the transaction succeeds. Failed
attempt entries remain as inactive descendants, matching the original tree contract.

[ProjectResourceReload](../../src/PiSharp.Cli/Sessions/ProjectResourceReload.cs)
loads replacement configuration/resources and computes the existing default-tool
reload plan. Failed loading disposes the replacement configuration. Program keeps
registry publication, agent/run construction, extension transfer and UI adoption
in their original order, including cleanup on failure. Explicit accessors read the
current arguments/settings/tool state after asynchronous configuration loading,
preserving the original lookup timing and settings updates before reload.

[TerminalModelNavigation](../../src/PiSharp.Cli/Tui/TerminalModelNavigation.cs)
owns available scoped-model cycling and supported-thinking-level navigation.
It resolves current selection after asynchronous catalog discovery and writes
status to the current console writer. Program applies the resulting selection
through its existing active/idle runtime gates.

[UserSettingsPersistence](../../src/PiSharp.Cli/UserSettingsPersistence.cs) validates
project trust, writes the selected scope, reloads that scope and merges effective
settings. Program applies the resulting runtime/terminal effects in their existing
order. [TerminalClipboardActions](../../src/PiSharp.Cli/Tui/TerminalClipboardActions.cs)
owns selection/assistant copy, sign-in URL status and text/image insertion; native
clipboard process access remains in TerminalClipboard.

These concrete owners stay inside CLI. There is no additional project, framework
wrapper, service locator or generic command bus. Remaining command coordination
and project/runtime UI adoption still concentrate state in Program and need further
composition work.

## Verification

[CliInformationalProcessTests](../../tests/PiSharp.Tests/CliInformationalProcessTests.cs)
checks exact help output, argument errors and configuration-loading precedence.
[RpcModelReplacementProcessTests](../../tests/PiSharp.Tests/RpcModelReplacementProcessTests.cs)
characterizes stale-checkpoint model/thinking rollback, retained inactive entries,
unchanged disk bytes and a successful retry before transaction extraction.
[TerminalPtyTests](../../tests/PiSharp.Tests/TerminalPtyTests.cs) covers real terminal
startup/disposal, settings, clipboard, session confirmation, branching and import.
Startup session, JSON lifecycle, project settings, session export and RPC session
replacement tests use deterministic local fixtures. Full terminal differentials
use the [frozen Pi reference](../parity/pi-reference.md); passing tests establish
their covered behavior, not complete Pi parity.
