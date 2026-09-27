# RPC extension command invocation baseline

Reference: `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`, inspected on 2026-09-27.

## Current Pi behavior

- RPC `prompt` passes `message`, `images`, and `streamingBehavior` to `AgentSession.prompt` and returns its response only after preflight reports a disposition (`packages/coding-agent/src/modes/rpc/rpc-mode.ts`, prompt case).
- `AgentSession.prompt` tries a registered `/command args` before active-run validation, input interception, image processing, and queue delivery (`packages/coding-agent/src/core/agent-session.ts`, `prompt` and `_tryExecuteExtensionCommand`). A recognized extension command therefore runs immediately during provider work and does not start or queue an agent run.
- A recognized command reports `data.disposition: "handled"`. If its handler throws, Pi emits `extension_error` with `extensionPath: "command:<name>"` and `event: "command"`, then still reports the command as handled.
- `rpc-prompt-response-semantics.test.ts` covers one success response for a handled extension command and confirms it starts no agent run.

## PiSharp process evidence

`RpcExtensionCommandProcessTests.ExtensionCommandsRunDuringProviderWorkAndReturnHandledOnSuccessOrFailure` starts the actual CLI RPC process against a barrier-controlled local provider. It verifies a command runs during an active provider request, returns `handled` without a queue update or second `agent_start`, emits its returned text before the correlated response, and accepts an image-bearing command before ordinary image rejection. A throwing command emits the Pi-shaped `extension_error` before its correlated handled response. The returned text uses a PiSharp `event` with `format: "pisharp"` and `extension_command_output`; Pi has extension UI and notification APIs instead of this return-string contract.

This is a scoped source/test comparison plus deterministic PiSharp process coverage, not a current-Pi process differential. Extension UI calls, command cancellation/shutdown, and broader extension lifecycle behavior remain open.
