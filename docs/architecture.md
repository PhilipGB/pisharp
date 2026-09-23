# Architecture decision record — evolving implementation

Reference: `earendil-works/pi@002fc8385268300ca91a5fc95f935c2afbbdac02`, version 0.87.1. Installed `pi --version` is also 0.87.1; version equality does not prove an identical distribution build. The pinned checkout remains the normative test baseline.

## Boundaries

- `PiSharp.Core`: deterministic product logic and data contracts, with no model SDK, filesystem or terminal references. Holds original-snapshot batched edit planning, an experimental Pi v3 JSONL entry codec, conversation tree and branch-aware context projection; settings validation and MAF bridging remain future work.
- `PiSharp.Runtime`: Microsoft Agent Framework `ChatClientAgent`, tool registration and local Linux filesystem/process adapters. It depends on Core but not the CLI or a provider SDK. Its `PiAgent` is the one execution runtime for all interaction modes. Tools use process permissions, not a sandbox.
- `PiSharp.Cli`: composition and OpenAI Chat Completions provider adapter (`Microsoft.Extensions.AI.OpenAI`), terminal and CLI mode. The current `Console.ReadLine` REPL is *not* an acceptable final TUI. Extend it with a separately testable VT input/rendering adapter rather than migrating state into the view.
- `PiSharp.Tests`: filesystem/process and deterministic provider/HTTP tests, including behavioural cases recorded against pinned Pi. Live server tests run separately and never gate PRs.

This is a small vertical-first layout; do not add separate projects for each technical concern. Split feature slices only when a real dependency boundary or independently deployable adapter warrants it. Future session persistence depends on Core, not terminal or model SDK types. Future providers supply `IChatClient` while preserving `ChatClientAgent` and the canonical session log. A service-managed history cannot be authoritative when branch navigation and provider switching are required; store the active conversation in an application-owned tree and reconstruct provider context from it. MAF `AgentSession` remains an execution detail, not the only durable record. Explicitly test tool-loop and streaming ordering before claiming Pi parity.

## Current risks / required next decisions

1. `CodingTools` is still one class with four operations, and shell capture is bounded but its live updates/terminal semantics differ. Move each contract to a cohesive feature file while adding the remaining tools and exact pinned comparisons.
2. The edit planner now matches original-snapshot batched replacements including limited fuzzy normalization and per-file queueing. It does not yet generate Pi's diff/patch details or preserve every fuzzy-match edge case. Tool errors currently return text instead of Pi's error result; error transport needs tests.
3. Tool file writes are not crash-atomic. Explicitly design temp-file/rename and permission preservation, with interrupted-write tests, before advertising full reliability.
4. Experimental Core Pi v3 codec, context projection and private JSONL file store are tested only on small pinned branch fixtures. Expand v1/v2 migration evidence and implement a durable branch-aware runtime history bridge, then replace preliminary MAF-only snapshots in **all** CLI modes. The codec is not yet the CLI's authoritative history.
5. Evaluate a VT library on actual normal-screen/alt-screen/editor/resizing and PTY tests rather than accepting a library because it can render styled lines. Preserve terminal scrollback in normal mode.

No capabilities have been marked Verified in the broad parity matrix solely for compiling. The deterministic edit cases in `EditConformanceTests` constitute *narrow* differential evidence for only those exact text cases, not full tool parity.
