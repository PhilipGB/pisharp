# Compaction and Context Management Parity — 2026-10-28

## Scope and source pins

This branch starts at the verified PR #27 merge `12064420179f4119bdaf37479c7cba0f5ca40717`.
The Pi oracle is pinned to `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`; the installed
`Microsoft.Agents.AI` version is 1.21.0. The recovery implementation and paired
oracle are pinned to source commit `b22f7e7bad940a1dd0a26d716628f6739a8a9abf`.

The frozen contract is [contract.json](../../../tools/parity/compaction_context/contract.json),
SHA-256 `c774819bdb78f537ba75c80243891187128f7fae7d1e392c31a1bd24868a2cf6`:
22 checkpoints and 211 required cases.

## Implemented

- Branch summary navigation can generate and apply Pi-style summaries, accept
  extension decisions, retain summary usage/cost and file details, restore
  selected provider history, and return selected prompts to the editor.
- Missing usage totals include cached-read and cached-write token counts.
- Successful post-turn provider usage beyond a known context window triggers
  overflow compaction without replay. Cached input already included in the
  provider input count is not counted twice.
- Provider overflow message matching includes pinned generic/provider patterns
  and representative non-overflow exclusions.
- Recoverable `length` stops check reported output usage, the desired model
  output cap, selected-model identity, and projected history. PiSharp omits the
  failed response from outbound context, compacts once, and continues once.
  Partial output is not replayed; a repeated length stop ends with Pi's bounded
  recovery error.

These changes have unit, CLI, extension, navigation, session interchange, and
loopback coverage. Those tests establish implementation behavior; they do not
replace the frozen per-case differential corpus.

## Paired evidence

The deterministic C5 oracle ran pinned Pi twice and PiSharp twice at the
implementation source pin. Both runs were stable on each side, and the two
paired cases passed as equivalent:

- `C5.recoverable-length-stop-retries-once`
- `C5.retry-request-uses-compacted-transcript`

The only normalization removes a provider transcript's absent-versus-empty
system message. Provider requests, compaction events, retry transcript,
continuation state, and session projection remain compared. The compact,
hash-bound case record is [compaction-context-management-c5-case-results.json](compaction-context-management-c5-case-results.json).
Raw Pi/PiSharp process, request, event, and session captures remain in the
Git-ignored local directory
`artifacts/parity/20261028T062543Z-c5-length-recovery/`. The committed record
contains their paths and SHA-256 hashes; the raw captures are not in Git.

The runner validated 2 of C5's 11 cases: 2 executed, 2 passed, 0 failed, and 9
unexecuted. The full frozen denominator remains 2 of 211 paired cases.

## Local validation

- Full .NET suite: **3,939 passed, 0 failed, 0 skipped**.
- Focused compaction and provider loopback tests: **82/82 passed**.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore`: passed.
- Warning-as-error solution build: passed with 0 warnings and 0 errors using
  `NuGetAudit=false`; the default vulnerability-audit cache is read-only here.
- Terminal Python: 66 passed; built-in parity Python: 27 passed; compaction
  verifier controls: 20 passed; terminal Node: 24 passed.
- MCP conformance: no regressions against the classified baseline; three known
  scenarios remain classified.
- Documentation paths: 156 documents and 688 references verified after the
  receipt and compact case record were added.
- Pinned Pi AI overflow tests: 20/20 passed. Selected pinned Pi compaction and
  queue tests: 34 passed and 1 failed. The failing test uses model ID `unknown`
  and context window 0; the pinned `shouldCompact` formula triggers while that
  queue test expects no compaction. This source/test discrepancy remains open.

## Acceptance remains open

The verifier records 2/11 C5 cases and 2/211 overall. None of the 22 frozen
checkpoints passes its complete denominator. The remaining 209 cases still
need paired Pi/PiSharp adapters and evidence across manual and automatic
compaction, cut points, context estimates, tool continuation, summaries,
session persistence and branching, usage, CLI/RPC/TUI, extensions, failures,
cancellation, and publication.

Also open are the complete Responses, Chat Completions, and Anthropic
compaction-continuation wire corpus; classification of the separate transient
estimator formula; and case-level classification of the pinned unknown-window
discrepancy. PR #28 remains open, draft, and unmerged against `main`; its final
exact-head push and PR checks are tracked after the source-bound evidence and
documentation update.

PR #27's merge, exact-head workflows, and 27/27 checkpoint / 240/240 case
publication evidence are verified as the prerequisite. Its fresh local
standalone rebuild produced a different CLI DLL hash from the archived build,
so that binary identity was not reproduced locally.

See the [inventory](compaction-context-management-inventory.json) and
[execution ledger](../execution-ledger.json) for the ownership map, current
counts, and remaining acceptance work.
