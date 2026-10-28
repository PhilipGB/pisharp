# Compaction and Context Management Parity — 2026-10-28

## Scope and source pins

This completed milestone is based on the verified PR #27 merge `12064420179f4119bdaf37479c7cba0f5ca40717`.
The pinned Pi source is `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. The frozen contract at
[contract.json](../../../tools/parity/compaction_context/contract.json) has SHA-256
`c774819bdb78f537ba75c80243891187128f7fae7d1e392c31a1bd24868a2cf6` and contains 22 checkpoints / 211 required cases. The final
implementation source SHA is `7480e81dcba48ca2861596789c6f39fecbc6ad2b` on branch
`milestone/compaction-context-management-parity`.

## Verified implementation and parity

All 22/22 checkpoints and 211/211 required cases pass, with zero failed or skipped cases.
C1-C13 pass 135/135 at the final implementation source; D0 passes 9/9; Q0 passes 10/10;
P0/P1 pass 10/10; V0/V1 pass 28/28; E0 passes 9/9; CI0 passes 5/5; PUB0 passes 5/5.
The paired C corpus has 121 equivalent observations and 14 case-level intentional safety
differences. Each safety difference has dedicated regression evidence, and there are no
unresolved mismatches. D0 verifies stable same-Pi observations and artifact hashes.

Implemented behavior covers manual and automatic compaction, cut-point retention,
context-window and usage accounting, provider overflow and bounded retry, transient
tool-context projection with canonical-history preservation, summary generation, JSONL
persistence and branch recovery, branch-summary hooks, CLI/RPC/JSON/TUI projection,
provider request boundaries, and cancellation/crash recovery. The C6 transient estimator
is separate from Pi's persistent message estimate; its conservative character-based formula
is limited to an ephemeral outbound safety projection.

The 14 accepted safety cases cover unknown context windows and limits, partial-output
no-replay, conservative transient estimation and bounded tool results, malformed tool
histories, empty summaries, unknown usage/prices, invalid compaction boundaries, and unknown
tool outcomes. Compact source-pinned result records include normalized observations,
SHA-256 digests, regression references, and raw artifact hashes.

## Quality and publication evidence

- Full .NET suite: **3,959 passed, 0 failed, 0 skipped**.
- Focused compaction/session/provider tests: **114/114**; focused RPC/TUI/extension: **52/52**.
- Terminal Python: **66/66**; compaction verifier/helper tests: **27/27**; built-in parity Python: **27/27**; terminal Node: **24/24**.
- MCP conformance reports no regressions against its classified baseline; three existing scenarios remain classified.
- Pinned Pi suite: **200 passed, 0 failed, 20 credential-gated skips**, with exact skip identities verified.
- Formatting and warnings-as-errors solution build passed. Q0 suppresses only NU1900 because the local NuGet audit cache is read-only; all other warnings remain errors. Exact-head CI passed the standard build with a clean cache.
- Q0 recorded **156 documents / 700 references**. Final post-evidence documentation path validation passes for **156 documents / 690 references**.
- Both automatic exact-head workflows passed with every required job successful and no manual dispatch. Their run IDs and the final PR head SHA are recorded in the PR description and ignored CI0 evidence.
- E0 validated the committed inventory, evidence index, source identity and raw artifact hashes. PUB0 validated the current explicit evidence directory and independently aggregated all 22 checkpoints / 211 cases.

Raw process streams remain Git-ignored under
`artifacts/parity/20261028T-final-source-7480e81d/`. The 211-case contract-derived index and
reviewed compact source-bound case results are committed under `docs/parity/fixtures/`.

## Limitations and review status

Twenty pinned Pi interactive/LLM integration cases are credential-gated and skipped; their
identities are checked. No live model endpoint was called. Provider behavior outside the
frozen cases and broader model/provider matrices are outside this milestone's scope. The
local Q0 build suppresses only NU1900 because the managed NuGet audit cache is read-only;
exact-head CI passes the standard build in its clean environment.

The PR #27 prerequisite publication records a historical archived CLI binary hash that
was not reproduced by a fresh standalone local build; the caveat is retained in the
prerequisite evidence. PR #28 remains **open, draft, and unmerged** for independent review.
