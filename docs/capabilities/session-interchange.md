# Pi session interchange

PiSharp imports Pi v1–v3 JSONL and exports v3 while keeping its own native session
format. [PiJsonlSessionInterchange](../../src/PiSharp.Runtime/Sessions/PiJsonlSessionInterchange.cs)
retains the public import/export, entry projection and branch-selection metadata
APIs. Persistent application history belongs to `ConversationSession`; MAF
`AgentSession` objects are reconstructed execution state, not the interchange
format.

## Ownership and flow

- [PiJsonlSessionImporter](../../src/PiSharp.Runtime/Sessions/PiJsonlSessionImporter.cs)
  checks file/header limits, parses bounded records, migrates v1 links and older
  hook messages, builds the tree and restores metadata from the selected path.
- [PiJsonlSessionTreeBuilder](../../src/PiSharp.Runtime/Sessions/PiJsonlSessionTreeBuilder.cs)
  validates unique IDs and parent relationships. Missing Pi parents become roots
  in the stricter native graph while their original Pi links remain available for
  export; forward parents preserve physical record order.
- [PiJsonlEntryCodec](../../src/PiSharp.Runtime/Sessions/PiJsonlEntryCodec.cs)
  converts Pi records and Microsoft.Extensions.AI content. It retains original
  imported entries alongside canonical native payloads, including unknown fields,
  tool errors, nested calls, images and provider/system metadata.
- [PiJsonlSessionWriter](../../src/PiSharp.Runtime/Sessions/PiJsonlSessionWriter.cs)
  writes parent-before-child branches, adds a selected-head/name anchor when
  needed and creates private export files without overwriting existing files.
  Failed or cancelled new exports remove their partial file.
- [PiSessionRunProjection](../../src/PiSharp.Runtime/Sessions/PiSessionRunProjection.cs)
  projects bounded active-branch windows into Pi messages. It correlates tool
  names, preserves interrupted assistant output and avoids duplicating a partial
  message already recorded in canonical history.

Runtime and CLI RPC consumers call the codec/projector directly. The public facade
remains for compatibility; its internal forwarding methods have been removed.
All owners remain internal to Runtime, using the existing CLI/test friend assembly
relationships. No additional project, interface or framework wrapper is needed.
Core continues to own the deterministic conversation tree.

## Format and parity constraints

Imports reject files larger than 128 MiB and skip malformed/non-record lines or
entries larger than 16 MiB. Unsupported header versions and invalid graph identities
fail closed. Legacy migration preserves Pi's linking and compaction semantics.
Imported unknown fields/records survive export; native records project current Pi
shapes. An invalid imported image becomes a readable omission in model context
while its original encoded content remains intact for export.

Exports retain all branches and the selected leaf. Newly generated timestamps,
IDs and a final session-info anchor are intentional; comparison should inspect
semantic records rather than volatile values. Tool result contracts and their
Pi representations retain images, errors, usage and termination metadata.
PiSharp's native persistence format and assembly/public API identity are unchanged.

## Verification

[PiJsonlSessionInterchangeTests](../../tests/PiSharp.Tests/Runtime/Sessions/PiJsonlSessionInterchangeTests.cs)
protect migrations, branches, context edits, compaction, unknown records, media,
interruptions, metadata and private/non-overwriting file exports.
`PiJsonlSessionFileStoreTests` and `PiJsonlSessionRecoveryProcessTests` protect
persistence/recovery. RPC projector/process, nested-tool and tool-result contract
tests protect shared wire boundaries. The transcript-resume terminal fixture
compares imported history with the [configured Pi reference](../parity/pi-reference.md).

```sh
dotnet test PiSharp.slnx --filter FullyQualifiedName~PiJsonl
```

Run affected RPC and canonical-session tests when changing record conversion;
run the full solution at structural checkpoints. Existing tests and parity
captures establish their covered behavior, with known terminal presentation gaps
remaining in the parity evidence.
