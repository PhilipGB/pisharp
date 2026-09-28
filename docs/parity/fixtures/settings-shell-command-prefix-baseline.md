# `shellCommandPrefix` compatibility fixture

Reference: `earendil-works/pi@c1449660c83fd00a7c71d5f7e1bd29fafd400550`, refreshed 2026-09-28.

## Pi behavior

`shellCommandPrefix` is an optional string in user `settings.json` or trusted project `.pi/settings.json`. It has no default. Project settings deep-merge over user settings, and untrusted project settings are discarded. Pi prepends a truthy prefix followed by one newline to every built-in Bash command. The Bash tool receives it in `createBashTool` options; direct `AgentSession.executeBash` applies the same transformation, which is used by interactive `!` and RPC Bash. Interactive `!!` runs the same shell command but excludes its recorded result from model context. Session history records the original command, not the expanded shell text.

Pi does not expose `shellCommandPrefix` as an interactive `/settings` picker entry. Its settings-manager tests verify the unset default, loading from user settings and preservation when an unrelated setting is saved. The focused current-Pi run passed 3/3 tests. Its `tools.test.ts` contains command-prefix tests for the Bash tool, but the suite could not load in this sparse checkout because generated `packages/ai/src/providers/data/amazon-bedrock.json` was absent; source inspection at the pinned SHA confirms both tool and direct-execution paths. The current settings picker source has no `shellCommandPrefix` entry.

## PiSharp behavior and evidence

`UserSettings` parses the optional string without trimming or rewriting it. `ProjectRuntimeConfiguration` reads project settings only after trust resolution; `Overlay` applies a trusted project prefix over the user prefix. `ProjectRuntimeContext.CreateAgent` passes the effective value to `CodingTools`. `CodingTools.BashCoreAsync` prepends it once, shared by the coding-tool Bash function and `ConversationRun.ExecuteBashAsync`; interactive `!`/`!!` and RPC Bash both route through that run method. Durable Bash entries retain the requested command string. `/settings` deliberately does not offer a control Pi itself lacks, while the settings writer preserves the configured value when another setting is saved.

Focused PiSharp evidence:

- `UserSettingsTests.ShellCommandPrefixIsAnOptionalStringAndTrustedProjectValueOverridesUserValue` covers unset/string/empty/wrong-type values and precedence.
- `ProjectRuntimeContextTests.LoadsProjectScopedSettingsResourcesAndStoreUnderTheTrustDecision` confirms trusted project override and proves an untrusted project setting with the wrong type is not parsed.
- `CodingToolsTests.ShellCommandPrefixRunsOnceForToolAndDirectBashExecution` verifies one application on each shared-core entry point.
- `RpcModeTests.RpcBashUsesTheSharedShellCommandPrefixAndPersistsTheUnprefixedCommand` verifies RPC output and canonical command persistence.
- `InteractiveBashExecutorTests` and `TerminalPtyTests.BangCommandsUseTheConfiguredPrefixAndDoubleBangExcludesContext` verify `!`/`!!` parsing, prefixed PTY execution, original command persistence, and context exclusion.
- `UserSettingsWriterTests.UpdatesSupportedValuesAndPreservesOtherSettingsAtomically` verifies saving unrelated `/settings` values retains the prefix.

The focused PiSharp settings/trust/tool/RPC/writer/bang-command filter passed 14/14. Exact-head Linux CI run [36435658026](https://github.com/PhilipGB/pisharp/actions/runs/36435658026) passed restore, format, warnings-as-errors build, and 748/748 tests (0 skipped). PiSharp extension `user_bash` handlers currently run only for RPC; interactive extension interception remains part of the later extension lifecycle audit. This is setting-level compatibility evidence, not overall Pi parity verification.
