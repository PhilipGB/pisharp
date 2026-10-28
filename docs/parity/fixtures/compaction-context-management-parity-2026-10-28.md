# Compaction and Context Management Parity — Review Correction Refresh — 2026-10-28

## Scope and source pins

The milestone is based on PR #27 merge `12064420179f4119bdaf37479c7cba0f5ca40717` and pinned Pi source `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. The frozen contract at [contract.json](../../../tools/parity/compaction_context/contract.json) has SHA-256 `c774819bdb78f537ba75c80243891187128f7fae7d1e392c31a1bd24868a2cf6` and defines 22 checkpoints / 211 required cases.

The native branch-summary usage omission from RPC `get_session_stats` is fixed at implementation source `41e9b7e7ebdea70b878e409204cd8b3d177144e2` on branch `milestone/compaction-context-management-parity`. Regression coverage verifies native generation, persisted reload, imported Pi summaries, mixed sessions, and exactly-once accounting.

## Current verified results

Current refreshed evidence passes 19/22 checkpoints and 192/211 required cases:

- C1-C13: 135/135, with 121 equivalent observations, 14 verified intentional safety differences, and zero unresolved mismatches.
- D0: 9/9, including two-run pinned-Pi stability and hashes for current compact C evidence.
- Q0: 10/10.
- P0/P1: 10/10, with P1 passing on a clean committed tree; V0/V1: 28/28.

E0, CI0, and PUB0 have not run for the corrected source. The corrected commits have not yet been pushed, and PR #28 still publishes the prior head `763f008a347282078b86946b398e9229dd930f06`.

## Quality evidence

- Full .NET suite: **3,963 passed, 0 failed, 0 skipped**.
- Focused compaction/session/provider: **114/114**; RPC/TUI/extensions: **56/56**.
- Terminal Python: **66/66**; compaction helper Python: **27/27**; built-in parity Python: **27/27**; terminal Node: **24/24**.
- MCP conformance has no regressions against the classified baseline; three existing scenarios remain classified.
- Pinned Pi suite: **200 passed, 0 failed, 20 exact credential-gated skips**.
- Formatting and warnings-as-errors build passed. Q0 suppresses only NU1900 because the local NuGet audit cache is read-only.
- Q0 recorded **156 documents / 700 references**; post-evidence documentation path validation recorded **156 / 690**.
- Provider-boundary checks use local loopback fixtures; no live model endpoint was called.

Raw process streams remain Git-ignored under `artifacts/parity/20261028T-review-fix-41e9b7e7/`. Compact source-bound reports and the 211-case contract index are tracked under `docs/parity/fixtures/`.

## Limitations and separate follow-up

Twenty pinned Pi interactive/LLM tests require credentials and were skipped with their exact identities verified. Broader provider behavior outside the frozen contract remains out of scope. The CI managed-package corpus remains scoped to its historical milestone branch; the independent review identified this as a separate verification concern. PR #28 remains **open, draft, and unmerged** pending the final acceptance refresh.
