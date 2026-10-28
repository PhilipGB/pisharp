# Compaction and Context Management Parity — 2026-10-28

## Scope and source pins

This milestone is based on the verified PR #27 merge `12064420179f4119bdaf37479c7cba0f5ca40717`.
The pinned Pi source is `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. The frozen contract at
[contract.json](../../../tools/parity/compaction_context/contract.json) has SHA-256
`c774819bdb78f537ba75c80243891187128f7fae7d1e392c31a1bd24868a2cf6` and contains 22 checkpoints / 211 required cases. The final
implementation source SHA is `7480e81dcba48ca2861596789c6f39fecbc6ad2b` on branch
`milestone/compaction-context-management-parity`.

## Implementation and differential evidence

C1-C13 pass **135/135** at the final implementation SHA. The paired corpus has
121 equivalent observations and 14 case-level intentional safety differences;
all have regression evidence and no unresolved mismatches. D0 independently
passes **9/9**, checking all 135 paired cases, stable same-Pi observations,
artifact hashes, and the case classifications. Q0 passes **10/10**. P0/P1 pass
10/10 and V0/V1 pass 28/28, bringing the current contract result to **192/211**;
E0, CI0, and PUB0 (19 cases) remain.

Implemented and verified behavior covers manual and automatic compaction,
cut-point retention, context-window and usage accounting, provider overflow and
bounded retry, transient tool-context projections with canonical-history
preservation, summary generation, JSONL persistence and branch recovery, branch
summary hooks, CLI/RPC/JSON/TUI projection, provider request boundaries, and
cancellation/crash recovery. The C6 transient estimator is deliberately kept
separate from Pi's persistent message estimate; its conservative character-based
formula is explicitly limited to an ephemeral outbound safety projection.

The 14 accepted safety cases are C2 unknown-window handling; C4 unknown context
limits; C5 partial-output no-replay and unknown capacity; C6 conservative
transient estimation, bounding oversized results, latest-outcome retention, and
pre-send rejection of malformed tool histories; C7 rejection of empty summaries;
C10 preserving unknown usage and price; C12 rejecting invalid boundaries and
unbalanced tool groups; and C13 not replaying a tool with an unknown outcome.
The compact case records under `compaction-context-management-c*-case-results.json`
include their normalized observations, SHA-256 digests, regression references,
and raw artifact hashes.

## Local quality evidence

- Full .NET suite: **3,959 passed, 0 failed, 0 skipped**.
- Focused compaction/session/provider tests: **114/114**; RPC/TUI/extension: **52/52**.
- Terminal Python: **66/66**; compaction verifier/helper tests: **27/27**; built-in parity Python: **27/27**; terminal Node: **24/24**.
- MCP conformance reports no regressions against its classified baseline; three known scenarios remain classified.
- Pinned Pi suite: **200 passed, 0 failed, 20 credential-gated skips**, with exact skip identities verified.
- Q0 validated **156 documents / 700 references**. After the final evidence updates, the documentation path validator passes for **156 documents / 694 references**.
- Format and warnings-as-errors solution build passed. The workspace NuGet audit cache is read-only, so Q0 suppresses only the NU1900 cache-write warning; all other warnings remain errors. Automatic exact-head CI runs the standard build in its clean environment.

Raw process streams remain Git-ignored under
`artifacts/parity/20261028T-final-source-7480e81d/`; compact source-pinned records and the contract-derived
211-case index are committed under `docs/parity/fixtures/`.

## Remaining publication gates

The source-bound implementation, differential, and quality checkpoints are complete.
The final publication run will validate E0, both automatic CI workflows on the exact final PR head, and PUB0.
PR #28 remains open, draft, and unmerged for independent review. Provider tests use
local loopback fixtures; no live model endpoint was called. The selected pinned Pi
suite requires provider credentials for its 20 skipped interactive/LLM cases.

The clean-baseline and inventory/verifier checkpoints pass: P0/P1 **10/10**, V0/V1 **28/28**. Their compact records are
[P0 compact record](compaction-context-management-p0-case-results.json),
[P1 compact record](compaction-context-management-p1-case-results.json),
[V0 compact record](compaction-context-management-v0-case-results.json), and
[V1 compact record](compaction-context-management-v1-case-results.json). Final exact-head run IDs and publication counts will be recorded in the PR description
and ignored CI0/PUB0 evidence for the exact final head.
