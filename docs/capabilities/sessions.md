# Canonical sessions

[ConversationSession](../../src/PiSharp.Runtime/Sessions/ConversationSession.cs)
owns the application conversation tree, selected branch and model/session metadata.
MAF sessions are rebuilt from this canonical history. Native persistence and Pi
JSONL are distinct formats; neither serializes MAF execution snapshots.

## Ownership and flow

- `ConversationSession` performs canonical mutations: messages, tool selections,
  declaration transcripts, router state, usage, compaction checkpoints, recovery
  and forks. Metadata changes use its existing lock and preserve branch semantics.
- [ConversationMessageCodec](../../src/PiSharp.Runtime/Sessions/ConversationMessageCodec.cs)
  encodes chat/Bash payloads and restores messages, nested calls and explicit tool
  failure state. Microsoft.Extensions.AI does not serialize function exceptions;
  native records retain them separately and restore them before replay.
- [NativeSessionDocumentCodec](../../src/PiSharp.Runtime/Sessions/NativeSessionDocumentCodec.cs)
  validates versioned native documents, every branch's records and compaction
  boundaries before returning a tree. `ConversationSession` reconstructs its
  metadata from that validated document. Version 1 text migrates; ambiguous v1
  tool turns fail closed.
- [ConversationContextProjection](../../src/PiSharp.Runtime/Sessions/ConversationContextProjection.cs)
  projects a selected canonical path into model context, provider-usage estimates,
  compaction entries and recovery-omission boundaries. It receives a path explicitly
  and does not own/mutate the session. Context edits and compaction filter model
  input while original history remains available for export and branch selection.

`ConversationStore` and `PiJsonlSessionFileStore` own persistence/leases. The
[Pi interchange](session-interchange.md) owners handle Pi import/export and
message projection. `ConversationCompactionCoordinator` plans and applies
summaries; `ConversationRun` rebuilds execution state after branch changes.
Native codec consumers call it directly rather than through internal forwarding
methods on `ConversationSession`.

## Invariants

Native format version 2 and all public session APIs remain compatible. Explicit
null-head selection differs from selecting the last entry. Parsing validates
inactive branches too, including tool failure indices/kinds, Bash booleans,
checkpoints, usage and compaction boundaries. Corruption must not become silent
success merely because its branch is currently inactive.

Recovery closes interrupted runs with an explicit unknown-outcome warning and
never invokes a tool or guesses whether a side effect happened. Forks retain their
existing selected-path/model rules and parent-session metadata. Context projections
omit excluded Bash, preserve imported custom/branch messages and avoid duplicating
checkpointed interrupted assistant text. Provider usage estimates respect
compaction, context-edit invalidation and in-flight-only projections.

Owners remain internal to Runtime; Core retains the pure conversation tree.
No new project, interface, mediator or framework wrapper is needed. Runtime's
native-extension assembly identity and documented facade APIs remain intact.

## Verification

[ConversationSessionTests](../../tests/PiSharp.Tests/ConversationSessionTests.cs)
protect migration, inactive-branch validation, leases, branching, side-effect
recovery and persistence. Explicit null-head and invalid inactive usage are
characterized before restructuring. Compaction, context budget/estimator,
Pi interchange, durable execution and RPC projector/process tests protect model
context and wire projections. The virtual-model context-compaction differential
and transcript-resume terminal matrix use the [configured Pi reference](../parity/pi-reference.md).

```sh
dotnet test PiSharp.slnx --filter FullyQualifiedName~ConversationSessionTests
```

Use the full solution for persistence changes and affected compaction/RPC tests
for projection changes. Covered tests do not establish complete Pi parity or
network-filesystem locking correctness; remaining evidence gaps stay recorded.
