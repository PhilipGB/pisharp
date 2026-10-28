# Compaction and Context Management Parity — 2026-10-28

## Scope and source pins

This branch starts at the verified PR #27 merge `12064420179f4119bdaf37479c7cba0f5ca40717`.
The Pi oracle is pinned to `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`; the installed
`Microsoft.Agents.AI` version is 1.21.0. C5 recovery observations were first
captured at source `b22f7e7bad940a1dd0a26d716628f6739a8a9abf`; C6 passed at
`1ff1d67d85d4ef76995700e0e345632c9c6ec16f`. The most recent clean
source-bound checkpoint was C7 at `d274475f25512c9568112985896a17136586a2a6`;
the current implementation source is `472daccd`.

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
- Summary generation now matches pinned prompt serialization, split-turn
  requests, file details, retry behavior, usage, model routing, and reasoning
  options. It rejects length-stopped responses, uses reserve-aware output caps,
  and retries transient provider failures using the configured retry policy.

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

The runner validated 2 of C5's 11 cases at that earlier source: 2 executed, 2
passed, 0 failed, and 9 unexecuted. These observations are historical after
later parity-oracle changes and must be refreshed at the final source head.

## C6 in-flight tool continuation

The C6 adapter drives pinned Pi's actual `AgentSession` tool loop and records
provider-hook requests and canonical session projection. Its PiSharp side calls
the in-flight projector directly and checks the transient request separately
from unchanged raw history. Sequential and parallel tool groups, a nested
`executeTool` call, steering after a result, large results, incomplete graphs,
duplicate results, and the valid post-projection history case all ran twice per
side with stable observations.

At source `1ff1d67d85d4ef76995700e0e345632c9c6ec16f`, C6 passes 10/10 required
cases. Four accepted safety differences are classified: Pi sends the full
large tool result while PiSharp bounds only the transient request; Pi retains
large raw tool outputs while PiSharp's summary preserves the latest outcome;
and Pi sends seeded incomplete or duplicate call/result graphs that PiSharp
rejects before sending the projected continuation. The compact record is
[compaction-context-management-c6-case-results.json](compaction-context-management-c6-case-results.json).
The raw request/process captures remain Git-ignored under
`artifacts/parity/20261028-c6-1ff1d67d/runs/C6/differential/`; the committed
record binds their paths and SHA-256 hashes.

## C7 summary generation

The C7 runner at source
`d274475f25512c9568112985896a17136586a2a6` passes all 10 required cases. Nine
cases are equivalent: initial prompts, prior-summary updates, manual focus,
split-turn requests, tool serialization, file details, transient retry, usage,
and routed model/reasoning options. The remaining case is an intentional safety
difference: pinned Pi accepts a whitespace-only summary and appends compaction;
PiSharp rejects it and preserves canonical history. Length-stop behavior and
state preservation match. The compact hash-bound record is
[compaction-context-management-c7-case-results.json](compaction-context-management-c7-case-results.json).
Raw captures remain Git-ignored under
`artifacts/parity/20261028-c7-d274475f/runs/C7/differential/`.
The later C8 source change at `472daccd` makes this C7 result historical under
the exact-source gate; rerun C7 before final acceptance.

## Local validation

- C6 checkpoint runner: **10/10 passed**; focused C6 .NET filter: **85/85 passed**.
- C6 parity Python validation: **20/20 passed**; frozen contract: 22 checkpoints
  and 211 cases; format verification passed.
- C7 checkpoint runner: **10/10 passed** at `d274475f`; focused C7 .NET filter:
  **80/80 passed**; format verification passed. Nine cases are equivalent and
  one whitespace-only-summary case is an intentional safety difference.
- C8 focused .NET filter: **140/140 passed** at `472daccd`; the compaction-entry
  usage round-trip regression passes; format verification passed. C8 paired
  evidence is not yet recorded.

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

The last source-bound result is C7 10/10 at `d274475f`, or 10/211 cases
(4.7%). The C8 implementation change at `472daccd` means no checkpoint result
is currently bound to the latest source; refresh C1-C7, complete C8 paired
evidence, and then continue across the remaining 201 cases. Open work covers
manual and automatic compaction, cut points, estimates, session persistence and
branching, usage, CLI/RPC/TUI, extensions, failures, cancellation, and
publication. The frozen denominator remains 22 checkpoints / 211 cases, and
full acceptance is not claimed.

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
