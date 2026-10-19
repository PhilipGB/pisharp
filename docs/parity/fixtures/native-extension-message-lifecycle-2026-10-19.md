# Native extension message lifecycle hooks — 2026-10-19

## Scope

- Base `main`: `0f1f68b14bb636d4ed85bfd7dcce6916aa00cbff` (PR #16 merge).
- Pinned Pi: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
- Branch: `milestone/native-extension-message-lifecycle`.
- Product source commit: `e4f06852798d0b49ebdd46a5c5f8a333ce54317d`.
- Scope: additive native compiled-extension `message_start`, assistant stream
  `message_update`, and finalized `message_end` replacement callbacks.

The implementation adds detached Abstractions DTOs and builder methods, ordered
source-owned registration with checkpoint/replacement/removal handling, and
dispatch at existing `ConversationRun`, `ObservedChatClient`, and
`DurableToolFunction` message boundaries. Accepted replacements flow through
canonical persistence and subsequent provider/tool processing. Broad extension
parity remains `in_progress`.

## Pinned behavior and limitations

Pinned Pi's `types.ts` and `runner.ts` define ordered message-end replacement;
the next handler sees the latest accepted replacement, the original role is
preserved, invalid or thrown callbacks report an extension error and later
handlers continue. `agent.ts` emits message events around actual user,
assistant, tool-result and custom messages. Assistant updates identify Pi's
text/reasoning/tool-call phases. `agent-session.ts` applies the final assistant
message before session persistence and downstream lifecycle observation.

PiSharp emits start/end for newly submitted user and custom messages, assistant
messages, and durable tool results; it does not replay history. Assistant
updates carry a detached accumulated message and the current structured MEAI
content delta. MEAI does not expose all Pi-specific `start`, `end`, `done`, and
`error` stream markers, so those distinctions are not synthesized. A provider
failure without a finalized assistant message can emit start/update without
end. Supported text, reasoning, data, URI, function-call/result content,
additional JSON properties, and available response metadata are detached.
Opaque provider content can be observed as JSON but cannot be reconstructed by
a replacement. MEAI also omits Pi-only fields and raw provider representation.

Assistant updates are held until end handlers finish when replacement handlers
are registered, so accepted final content reaches the authoritative MAF message
boundary. If no handler changes the message, the original provider update chunks
are forwarded. Provider lifecycle/token events remain available while they are
held. Callback cancellation signals the handler token, revokes its public
context, observes late faults, and lets the owning operation stop without
awaiting non-cooperative extension code. Existing provider exceptions without a
finalized MEAI response do not create a synthetic assistant end message.

## Focused evidence

`NativeExtensionMessageLifecycleTests` covers ordered prompt/assistant events,
replacement persistence and later agent/turn visibility, provider-visible
queued follow-up replacement, tool-call update structure and tool-result
replacement on the following request, idle custom-message lifecycle and
replacement, role/malformed replacement rejection, continued dispatch after
ordinary failure, provider failure without message end, and callback
cancellation/context revocation. `NativeExtensionEventRegistrationTests`
covers source order, checkpoint rollback, source replacement and removal for
the new handler lists. Existing compiled-provider history fixtures continue to
build and load against the additive Abstractions API.

Focused lifecycle, registration, and observed-client tests: 15 passed, zero
failed/skipped. The suite includes custom messages, queued follow-up, and
partial-provider failure behavior. The compiled external-package lifecycle
process test passed all four packaging variants, including user, assistant, and
tool-result start/end callbacks plus text and tool-call updates.

## Integrated local verification

Restore completed with `dotnet restore PiSharp.slnx -p:NuGetAudit=false` after
the default audit request reported `NU1900`: the sandbox's shared vulnerability
cache under `/home/philip/.local/share/NuGet/http-cache` is read-only. Format
verification passed. The solution warnings-as-errors build passed with zero
warnings and errors. The final full .NET suite passed **2,448/2,448**, zero
skipped, with `TMPDIR=/tmp`; Architecture passed **7/7**. One earlier full run
had a transient missing fixture output path in an unrelated model-package test;
that test passed alone and the complete suite passed on rerun. Node terminal
tests passed **24/24**, Python terminal parity passed **50/50**, and
documentation path validation passed **146 documents / 568 references**. MCP
conformance reported no regressions; its eight previously classified failures
remain unchanged. Exact-head push and PR CI run IDs belong in the PR description
after publication to avoid changing the recorded commit solely for its own CI
results.

## Deferred

This milestone does not implement Pi's unavailable stream markers, opaque
provider-content reconstruction, generic `pi.on(...)`, TypeScript extensions,
provider OAuth, dynamic registration, advanced UI/renderers, or broader
extension parity.
