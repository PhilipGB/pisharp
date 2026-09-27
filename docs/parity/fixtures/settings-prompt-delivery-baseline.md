# Prompt-delivery settings

## Pi reference

The current Pi settings reference at `2b0a123de98318c2ff8069661721ce0c3794c34e` defines `steeringMode` and `followUpMode` as `all` or `one-at-a-time`, both defaulting to `one-at-a-time`. Project settings override agent-directory settings. `SettingsManager.getSteeringMode()` and `getFollowUpMode()` provide these defaults to the active agent; the setters persist the global values.

## PiSharp behavior

`UserSettings` validates both values and overlays trusted project values over user settings. `/settings` edits each value in either permitted scope and can remove the scoped value to inherit. After saving, the CLI reloads the effective settings and updates the current `ConversationRun` queue policy.

`TerminalPtyTests.SettingsPickerEditsUserScopeThroughLinuxPty` edits both rows, reloads the saved file, and confirms unrelated compaction, theme, and editor settings survive. `TerminalPtyTests.ActiveRunStreamsExtensionToolThroughLinuxPtyAndRestoresTheTerminal` sets steering to `all` in `/settings`, blocks the first local Responses request, queues two messages during the turn, then confirms both appear in order in the next provider request after a tool call. This is a PiSharp CLI process fixture with a local HTTP provider; it does not claim a paired Pi process differential.

## Model and provider retry defaults

At the same Pi reference, `defaultThinkingLevel` is `medium`; PiSharp uses that fallback for reasoning-capable models and clamps it to `off` when a model does not support reasoning. The process fixture also verifies the initial Responses request sends `reasoning.effort: medium`.

Pi separates agent-level retry settings from `retry.provider.maxRetries`, whose default is zero. PiSharp now validates and merges the provider retry count independently, and maps it to the OpenAI `ClientRetryPolicy` and Anthropic `MaxRetries`. `ProviderChatClientFactoryTests` use local HTTP 5xx responses to verify one request at the default and exactly one retry when configured to one for both adapters.

## Remaining settings work

The rest of the settings schema and live reload behavior remain open. Provider timeout and transport settings still need adapter-specific mapping and runtime evidence. Terminal/display, image-processing, resource/theme paths, branch summaries, telemetry, and trust/reload preferences still need source/default/precedence review and runtime evidence.
