# Compaction and Context Management Parity — 2026-10-28

## Scope and source pins

This branch starts at the verified PR #27 merge `12064420179f4119bdaf37479c7cba0f5ca40717`.
The Pi oracle is pinned to `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`; the installed
`Microsoft.Agents.AI` version is 1.21.0. C1-C8 passed on clean source
`843d0388ceca2adbe2725397c48e0e45b98cba7e`; C9 now passes on clean source
`1deb174887151679db19339c5bffbe660bc2e8c5`. C1-C8 need refreshing after the C9
source changes. Older C5-C7 captures remain in Git-ignored run folders as
historical context; compact case records bind the observations and raw hashes.

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

C1-C8 passed all **86 required cases** on source `843d0388` with clean source
trees and stable Pi/PiSharp reruns. Nine case-level safety differences are
explicitly classified and linked to regression evidence; none of these eight
checkpoints has an unresolved mismatch. Compact records bind each generated
report and its raw artifact hashes:

- [C1 manual compaction](compaction-context-management-c1-case-results.json): 9/9.
- [C2 automatic compaction](compaction-context-management-c2-case-results.json): 10/10; one unknown-window safety difference.
- [C3 cut points and retained history](compaction-context-management-c3-case-results.json): 14/14.
- [C4 context limits and estimates](compaction-context-management-c4-case-results.json): 12/12; one unknown-limit safety difference.
- [C5 provider overflow and retry](compaction-context-management-c5-case-results.json): 11/11; two safety differences.
- [C6 in-flight tool continuation](compaction-context-management-c6-case-results.json): 10/10; four safety differences.
- [C7 summary generation](compaction-context-management-c7-case-results.json): 10/10; one whitespace-only-summary safety difference.
- [C8 persistence and reload](compaction-context-management-c8-case-results.json): 10/10.
- [C9 branch summary and navigation](compaction-context-management-c9-case-results.json): 9/9 at source `1deb1748`.

C1 pairs manual eligibility, focus, retained history, split-turn summaries,
usage/details, failure rollback, cancellation, and queued follow-up ordering.
The oracle's terminal failure uses the same non-transient exception category
on both implementations. C2 and C4 classify Pi's `contextWindow=0` threshold
trigger versus PiSharp's no-speculation policy as a safety difference. C3
covers recent-message selection, assistant/tool boundaries, and retained
history. C4 also keeps Pi's context estimator separate from PiSharp's transient
request-budget estimator; the latter formula difference remains unclassified.

C5 includes actual OpenAI Responses, OpenAI Chat Completions, and Anthropic SDK
overflow exchanges. It matches the recognized pre-content overflow and
compacted retry. Two accepted safety differences are recorded: Pi can retry
after partial visible output while PiSharp avoids replay, and PiSharp does not
invent capacity for an unknown window. C8 independently compares actual
post-reload provider requests on all three adapters.

## C6 in-flight tool continuation

C6 passes 10/10 at source `843d0388`. The adapter drives pinned Pi's actual
`AgentSession` tool loop and records provider-hook requests and canonical
session projection. PiSharp's in-flight projector is checked separately from
unchanged raw history. Sequential, parallel, nested, steering, large-result,
incomplete-graph, duplicate-result, and post-compaction cases are stable across
two runs per side. Four accepted safety differences cover bounded transient
results and rejection of malformed call/result graphs while retaining canonical
history. The compact record includes the regression links and hashes.

## C7 summary generation

C7 passes 10/10 at source `843d0388`: nine equivalent cases and one intentional
safety difference. Summary prompts, previous-summary updates, split-turn focus,
tool serialization, file details, transient retries, usage, and routed
reasoning match. Pinned Pi accepts a whitespace-only summary and appends a
compaction; PiSharp rejects it and preserves canonical history. The compact
record includes the regression link and raw hashes.

## C8 persistence and reload

C8 passed all 10 required paired cases on clean source
`843d0388ceca2adbe2725397c48e0e45b98cba7e`. Pinned Pi and PiSharp runs were
stable across two executions per side. The comparison covers compaction usage
and metadata, native/Pi JSONL round-trips, branch reconstruction, fork/clone,
restart continuation, and provider requests after reload. The actual SDK
adapters matched for OpenAI Responses, OpenAI Chat Completions, and Anthropic
Messages. The provider comparison normalizes temporary working roots and the
repo-root prefixes on generated documentation hints; remaining message order,
roles, and text are compared directly. The same explicit retained-answer
boundary is used on both sides because C3 owns cut-point selection.

The compact source-bound record is
[compaction-context-management-c8-case-results.json](compaction-context-management-c8-case-results.json).
Raw captures remain Git-ignored under
`artifacts/parity/20261028-c8-843d0388/runs/C8/differential/`.

## Local validation

- C1-C8 focused checkpoint filters: **80/80, 78/78, 79/79, 82/82, 87/87,
  86/86, 80/80, and 140/140 passed**, respectively, at source `843d0388`.
- C1-C8 paired runners: **86/86 passed**, zero failures/skips; nine accepted
  case-level safety differences; Pi and PiSharp observations stable across two
  runs per side.
- C8 post-reload provider tests: **3/3 passed** for OpenAI Responses, Chat
  Completions, and Anthropic Messages.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore` passed at the
  prior C8 source; rerun during final Q0 after implementation changes stop.
- Full .NET suite: **3,939 passed, 0 failed, 0 skipped** at source `472daccd`;
  it predates the C8 provider tests and is not final-source Q0 evidence.
- Warning-as-error solution build previously passed with `NuGetAudit=false`;
  the default vulnerability-audit cache is read-only in this environment.
- Terminal Python: 66 passed; built-in parity Python: 27 passed; compaction
  verifier controls: 20 passed; terminal Node: 24 passed (earlier validation).
- MCP conformance had no regressions against the classified baseline; three
  known scenarios remain classified.
- C9 focused checkpoint filter: **27/27 passed** at source `1deb1748`, including the Linux PTY tree-navigation test.
- Pinned Pi `interactive-mode-tree-navigation.test.ts`: **5/5 passed**.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore` passed after the C9 implementation changes.
- Documentation paths: rerun after this C9 receipt and progress update.
- The selected pinned Pi compaction/queue suites reported 34 passed and 1
  failing upstream test because the unknown model has context window zero. C2,
  C4, and C5 now classify the observed behavior as an intentional safety
  difference; the upstream fixture expectation itself remains unchanged.

## Acceptance remains open

C1-C8 passed 86/86 at source `843d0388`; C9 passed 9/9 at source `1deb1748`.
Across revisions, 95/211 cases have paired results, but only C9's 9 cases are
bound to the latest source. Refresh C1-C8 after the C9 changes; 202 cases lack
latest-source evidence. C10-C13 and final acceptance gates remain. Remaining
work covers usage/accounting surfaces, CLI/RPC/TUI, native extensions,
cancellation/crash recovery, independent acceptance, full quality,
committed-source validation, exact-head CI, and publication. The separate
transient estimator formula still needs a case-level classification.
Unknown-window behavior is already classified as a safety difference in C2,
C4, and C5. Full acceptance is not claimed.

PR #28 remains open, draft, and unmerged against `main`; its final exact-head
push and PR checks are tracked after the remaining implementation and evidence.


PR #27's merge, exact-head workflows, and 27/27 checkpoint / 240/240 case
publication evidence are verified as the prerequisite. Its fresh local
standalone rebuild produced a different CLI DLL hash from the archived build,
so that binary identity was not reproduced locally.

See the [inventory](compaction-context-management-inventory.json) and
[execution ledger](../execution-ledger.json) for the ownership map, current
counts, and remaining acceptance work.


## C9 branch-summary navigation

C9 passed all nine required paired cases on clean source
`1deb174887151679db19339c5bffbe660bc2e8c5`. The comparison covers common
ancestor and abandoned-path selection, chronological branch-entry preparation,
summary budget retention, nested file-operation details, append/replace custom
instructions, summary preamble and label placement, cancellation/failure
rollback, and public prompt defaults. The terminal prompt now matches pinned
Pi's three choices: no summary, summarize, or summarize with a custom prompt.
The pinned Pi `interactive-mode-tree-navigation.test.ts` passes 5/5. Focused
PiSharp tests pass 27/27, including the Linux PTY tree-navigation test.

The compact record is
[compaction-context-management-c9-case-results.json](compaction-context-management-c9-case-results.json).
Raw captures are Git-ignored under
`artifacts/parity/20261028-c9-1deb1748/runs/C9/differential/`.
