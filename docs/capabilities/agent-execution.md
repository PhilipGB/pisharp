# Agent execution

[PiAgent](../../src/PiSharp.Runtime/PiAgent.cs) composes and runs Microsoft Agent
Framework `ChatClientAgent` directly over Microsoft.Extensions.AI clients. It owns
execution history, active run callbacks, provider request state and durable tool
binding. `ConversationRun` coordinates prompts/retries/compaction against canonical
[sessions](sessions.md). MAF sessions are disposable execution projections.

## Ownership and flow

- [AgentToolCatalog](../../src/PiSharp.Runtime/AgentToolCatalog.cs) owns initial
  built-in/extension selection, exposure policy, live catalog replacement and
  per-session `ToolLoadout` objects. The registry represents available tools;
  loadouts and declaration transcripts represent session selections and provider
  interface history. Restore honors selected/excluded/disabled tool policy.
- `PiAgent` binds available functions through `DurableToolFunction` to the current
  execution, lifecycle events, hook pipeline and nested-tool state. Before live
  registrations become visible, replacement executable wrappers are prepared
  under the existing update lock. Tool descriptions/declarations preserve Pi's
  provider-facing contract separately from executable identity.
- [PiCompactionSummarizer](../../src/PiSharp.Runtime/PiCompactionSummarizer.cs) owns
  bounded transcript construction, normal/update/turn-prefix prompts and its MAF
  summarizer agent. It uses the shared routed client directly, preserving physical
  route usage/pricing attribution and bypassing ordinary virtual selection where
  direct summarization hints require it.
- `ObservedChatClient` applies provider request projection, retry, context transforms,
  image normalization, tool/system transcript projection and steering injection.
  `MutableChatClient` supports replacement; `RoutedChatClient` retains Pi-specific
  virtual-model routing/state semantics that MAF does not supply.

The run gate serializes active execution on one agent. Each run sets its session
loadout/transcript and callbacks, then streams directly from MAF. Settlement
persists injected steering and normalized image history before clearing run
callbacks/loadout and releasing the gate, including failure/cancellation paths.

## Constraints and decisions

No generic agent interface or wrapper hides MAF/MEAI. The catalog and summarizer
have concrete lifecycle/ownership responsibilities and remain internal to Runtime;
no new project is justified. Public PiAgent constructor/method contracts and its
nested compaction result remain compatible. The constructor retains the existing
option-rich signature; internal ownership is explicit rather than replacing its
parameters with an opaque options bag.

Pi tool exposure, runtime function replacement, schema/description revisions,
context estimates and restored tools are parity-sensitive. A registered function
can change while a session keeps its declaration history. A tools-disabled policy
must also apply to restored branch selections. Compaction focus is bounded before
a provider request; empty summary output fails closed. Summarization retains the
most recent bounded excerpts and preserves previous-summary/turn-prefix semantics.

## Verification

Agent integration/lifecycle, tool selection/loadout/declaration history/search,
deferred restoration, nested execution, Codemode, compaction and virtual-model
tests exercise the shared path. `CompactionTests` characterizes oversized focus,
empty provider output and bounded latest-message retention. Live extension tool
replacement tests protect ordering and existing session behavior.

```sh
dotnet test PiSharp.slnx --filter FullyQualifiedName~Agent
```

Run compaction/tool and provider-loop tests when modifying these owners. Deferred
tool, tool-batch, Anthropic-inline and virtual-model process differentials use the
[configured Pi reference](../parity/pi-reference.md). Resource/skill/trust terminal
flows protect shared tool invocation and loadout interactions. Existing covered
checks preserve their frozen outcomes; overall Pi parity remains incomplete.
