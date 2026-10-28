# Compaction and Context Management Parity — 2026-10-28

## Scope and source

This milestone branch is based on the merged PR #27 commit
`12064420179f4119bdaf37479c7cba0f5ca40717`. The Pi oracle is pinned to
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`; the installed
`Microsoft.Agents.AI` version remains 1.21.0.

Implementation source: `4e96fa6873192e51a130c0ba5a5e4fcccddaba71`.

The frozen contract is [contract.json](../../../tools/parity/compaction_context/contract.json),
with SHA-256
`c774819bdb78f537ba75c80243891187128f7fae7d1e392c31a1bd24868a2cf6`: 22
checkpoints and 211 required cases.

## Implemented

- Branch navigation can generate and apply Pi-style summaries, accept extension
  decisions, retain summary usage/cost and file details, restore selected
  provider history, and return selected prompts to the editor.
- Missing usage totals include cached-read and cached-write token counts.
- Successful post-turn provider usage beyond a known context window triggers
  overflow compaction without replay. Cached input already included in the
  provider input count is not counted twice.
- Provider overflow message matching includes the pinned generic/provider
  patterns and representative non-overflow exclusions.

These changes have focused unit, CLI, extension, navigation, session
interchange, and loopback coverage. They establish implementation behavior; they
do not provide the frozen per-case differential records.

## Local validation

- Full .NET suite: **3,932 passed, 0 failed, 0 skipped**.
- Format verification passed; warning-as-error solution build passed with zero
  warnings and errors.
- Terminal Python: 66 passed; built-in parity Python: 27 passed; compaction
  verifier controls: 19 passed; managed-package verifier controls: 62 passed;
  terminal Node: 24 passed.
- Selected pinned Pi compaction, tree-navigation, branch-summary, stats and
  overflow suites: **128 passed, 12 skipped upstream**; Pi AI overflow tests:
  **20/20 passed**.
- Documentation paths verified 156 documents / 687 references.
- MCP conformance reported no new regressions against the classified baseline;
  three known scenarios remain classified.

## Acceptance remains open

The current compaction runner records command-level test results but produces
**0/211 per-case artifacts**. The verifier therefore cannot pass any complete
checkpoint, and this receipt does not claim full Pi parity or acceptance.

Open behavior and evidence include:

- Recoverable output-length stops do not yet run one bounded compact-and-retry
  continuation.
- A complete same-scenario provider-wire corpus for Responses, Chat
  Completions, and Anthropic is absent; the Cerebras bodyless error case also
  needs provider-identity handling.
- The separate transient estimator formula differs from Pi's message estimate
  and still needs source-pinned classification.
- Contract prerequisite P0 remains unresolved. The downloaded PR #27 artifact
  [from exact-head PR run 40617750121](https://github.com/PhilipGB/pisharp/actions/runs/40617750121)
  contains 240 case records, but its draft-publication and exact-head-CI
  checkpoints are marked blocked. PR #27's merged description and receipt
  report 27/27 checkpoints and 240/240 cases, but the standalone final
  publication artifact was not found in the run artifact.
- Draft PR [#28](https://github.com/PhilipGB/pisharp/pull/28) is open against
  `main` and remains unmerged. Its first hosted run on head
  `7e55e214b9833cd27acdb512be0fe39032381c1d` passed the platform, MCP, and full
  .NET checks, then failed when the managed-package corpus asserted its own
  historical branch name and pre-merge base (`d969d3e`). That package corpus
  is now scoped to `milestone/managed-package-lifecycle-parity`; verifier
  controls and common CI remain active on this branch. The frozen compaction
  contract remains incomplete, and exact-head checks must pass on the updated
  PR head before this draft can advance.

See the [inventory](compaction-context-management-inventory.json) and the
[execution ledger](../execution-ledger.json) for the source ownership map and
remaining acceptance work.
