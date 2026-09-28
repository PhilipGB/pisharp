# Bounded RPC residual audit — 2026-09-27

Superseded for current-source coverage by [the 2026-09-28 audit](rpc-residual-audit-2026-09-28.md), pinned to Pi main `6f7551516b84278eb9da1c340c8e7bc66be1a6ba`. This file preserves the findings and source pin of the earlier audit.

Reference: current Pi `2b0a123de98318c2ff8069661721ce0c3794c34e` (0.87.1). Scope was the public command union in `packages/coding-agent/src/modes/rpc/rpc-types.ts`, Pi dispatch in `rpc-mode.ts`, PiSharp dispatch/handlers under `src/PiSharp.Cli/Protocols`, and the open RPC notes in the detailed inventory. This was a bounded interface/residual audit, not a full process differential of every command.

All 33 top-level command names in Pi's union have a PiSharp dispatch path. Existing Pi/PiSharp process or deterministic evidence covers the central prompt/queue, model/thinking, session/tree, compact, retry, Bash, stats, export and resource-command workflows. The current split-turn compact result and process evidence is in [the compaction fixture](fixtures/rpc-compact-baseline.md).

## Core runtime/session residuals

- Context estimates can affect automatic-compaction decisions. The active `get_session_stats` comparison recorded 1422 tokens in Pi and 817 in PiSharp; the compact fixture records 31/41 in Pi and 1085/67 in PiSharp. These fixtures differ in projected runtime/system inputs, so the next useful step is a matched persisted-system-message fixture before changing the estimator. Current pre-prompt automatic compaction and interrupted-assistant boundaries have deterministic coverage; provider-usage and overflow-recovery parity remain open.
- Session crash recovery and lock-wait behavior have PiSharp process tests for no replay and serialized writes, but no paired Pi process differential. This is the next core family, not a reason to extend RPC dispatch micro-parity.

## Material interoperability residuals

- Provider-specific RPC streaming and failure behavior is not established across OpenAI Responses and Anthropic adapters. OpenAI Chat Completions raw tool-call deltas have one paired fixture; partial argument-snapshot semantics and broader cancellation/error differentials remain open.
- Pi's extension UI/resource lifecycle is broader than PiSharp's current extension command/tool surface. These gaps belong to the extension/resource capability pass, with process evidence for the behavior that is already implemented.

## Narrow protocol-shape residuals

- The generated live system message uses `sections.preamble` in PiSharp while Pi exposes separate `preamble`, `tools`, `rules`, `docs` and `cwd` sections.
- Pi accepts truthy non-string `new_session.parentSession` values that PiSharp rejects under its string contract.
- Optional fields, wording, and remaining event payload differences should be handled in the final compatibility audit unless a new process comparison shows they change a caller-visible workflow.

No additional RPC-only fix was identified in this pass. Move to session recovery/concurrency, then settings/resources/extensions; revisit the estimator with a session that persists comparable system state and test provider-specific streaming at the provider-family boundary.
