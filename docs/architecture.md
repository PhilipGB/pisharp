# Architecture (incomplete implementation)

Pinned upstream behavioural reference: `earendil-works/pi@002fc8385268300ca91a5fc95f935c2afbbdac02`. Pi Packages are the only planned exclusion. No broad feature is verified as equivalent.

- `PiSharp.Core`: dependency-free deterministic edit/read planning and `ConversationTree`. It knows nothing of provider SDKs, terminal, or filesystem.
- `PiSharp.Runtime`: `PiAgent` wraps Microsoft Agent Framework `ChatClientAgent`; `ConversationSession` owns the serializable application history, `ConversationRun` builds disposable MAF sessions from the selected branch and appends MAF turns back, `ConversationStore` atomically stores the single canonical document. MAF internal snapshots are not persisted. `CodingTools` hosts the tool registry and filesystem/process adapters. A failed tool throws and reaches the model as a `FunctionResultContent` with an exception; failures are separately encoded in the application document because M.E.AI omits exceptions during serialization.
- `PiSharp.Cli`: OpenAI-compatible provider composition, basic CLI and line-oriented terminal. TUI, JSON, RPC, provider/catalog selection, resources and extensions are missing. Interaction modes must use the same `ConversationRun`, not forked histories.
- `PiSharp.Tests`: deterministic fake-model, controlled HTTP/SSE and local filesystem/process cases. No credentials are needed; live tests are opt-in and separate.

Known risks: data format is PiSharp-specific and old MAF snapshot files are not migrated; a crash during a provider turn cannot reconstruct uncommitted partial MAF history; interruption marker does not guarantee a tool result is complete; `ConversationRun` assumes MAF only appends, but does not yet validate history prefixes; concurrent OS processes can overwrite the same session (no lock). No project trust or process sandbox; do not run against untrusted source. Tool fidelity is limited to narrow reference fixtures, and there is no real TUI yet.
