# branchSummary.reserveTokens — bounded parity receipt

## Provenance

PR #54 was corrected in place, marked ready and merged before this milestone.
Reviewed head: `d8da7c4e9900cf5f013961f2f251d28fae4ae695`; implementation:
`aef1209305e445176092556d79f775109f8386d1`; successful exact-head CI:
`43274890874`, five jobs. Merge-commit method, merge and milestone base:
`bf893d5ab22a7d0503847127e10cb58e46a2f374`. The reviewed head is an ancestor;
local main equalled fetched origin/main and was clean when the new branch
`milestone/branch-summary-reserve-tokens-parity` was created.

Pinned Pi: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
Fail-first commit: `e507fafb3`. Implementation:
`cab70fdd4dce42bcf5f88dd987a2000d43caf83a` (initial implementation
`c852d1a174d6c70566a2fe1a718f02134431d94f`).
The evidence commit, exact final PR head and successful final-head CI are recorded
in [draft PR #55](https://github.com/PhilipGB/pisharp/pull/55), without a metadata-only
commit after CI. The PR remains draft, open and unmerged for review.

## Pinned source inventory

Paths are relative to `packages/coding-agent/src/` at the pin.

| Source | Contract |
| --- | --- |
| `core/settings-defaults.ts:14-17` | reserve 16384, skipPrompt false |
| `core/settings-schema.ts:29-42` | number description, not file-load validation |
| `core/settings-manager.ts:65-82,366-380,882-887` | JSON.parse, presence-based recursive merge, trust exclusion, nullish getter |
| `core/agent-session.ts:3994-4158` | abandoned path, reserve forwarding, actual navigation and summary attachment |
| `core/compaction/branch-summarization.ts:195-246` | newest-first selection, positive-budget gate, summary exception, file operations |
| `core/compaction/branch-summarization.ts:293-380` | context fallback, subtraction, empty outcome, provider request, separate output cap |
| `modes/interactive/interactive-mode.ts:5602-5622` | truthy skipPrompt means no summary |
| `modes/interactive/components/settings-selector.ts` | reserve is file-only |

Relevant tests inspected: `test/branch-summarization.test.ts`,
`test/agent-session-branching.test.ts`, `test/branch-summary-extensions.test.ts`.
Ancillary upstream run: five passed, three existing credential-gated skips, not counted
as mandatory corpus coverage. All 54 mandatory oracle cases execute without skips.

## Fail first and executable results

The [baseline](settings-branchsummary-reservetokens-baseline-2026-11-12.json), frozen
before product changes at post-#54 main, has **27 settings matches / 27 divergences**
and **0 complete request/navigation matches**. Negative, fractional, explicit null,
malformed, duplicate and wide-number reserves failed loading. Ordinary integers
loaded, but output caps differed; tiny budgets omitted Pi's empty-selection summary.

The [final differential](settings-branchsummary-reservetokens-differential-2026-11-12.json)
matches **54/54 settings and 54/54 complete request/navigation cases**, with zero
unexplained semantic differences. Two independent Pi executions calibrate exactly.

| Context | Reserve | Budget | Provider markers in chronological order |
| --- | --- | --- | --- |
| 4116 | 4096 | 20 | MIDDLE-MARKER, RECENT-MARKER |
| 32 | 2.5 | 29.5 | MIDDLE-MARKER, RECENT-MARKER |
| 32 | 2 | 30 | OLDEST-MARKER, MIDDLE-MARKER, RECENT-MARKER |
| 100 | 90 | 10 | RECENT-MARKER |
| 100 | 100 / 101 / "bad" | 0 / -1 / NaN | OLDEST-MARKER, MIDDLE-MARKER, RECENT-MARKER |
| 100 | 99 / 90.5 | 1 / 9.5 | no request; persisted `No content to summarize` |

Each ordinary message has exactly 40 characters, ten estimated tokens. Summary and
compaction entries use 160 characters and exercise the cutoff exception. Nested file
details accumulate; reached tool calls are tracked before the budget comparison.

Unset, child null and non-object parents default to **16384**. Normal/zero/negative/
fractional/wide/large finite numbers load. Numeric strings and `[1000]` coerce to
1000; empty strings, false and empty arrays to zero; true to one; bad strings,
objects and multi-element arrays to NaN. Nested/null arrays, radix strings,
Infinity strings and negative zero are frozen too. The later duplicate wins in both
orders; unknown siblings remain tolerated. Trusted project values replace by presence,
including null and malformed values; sibling-only objects preserve inherited reserve;
untrusted project settings contribute nothing. Context sizes 32/100/200 and others,
plus the zero-context 128000 fallback, are covered. Only positive budgets limit history.
Two supplementary [pinned radix probes](settings-branchsummary-reservetokens-radix-oracle-2026-11-12.json)
confirm that punctuation in `"0x_"` and `"0x\u0060"` yields NaN, not hexadecimal digits.

## Implementation and approved scope extension

Raw values and bounded JSON Number coercion stay at the CLI boundary. Runtime receives
a double, not JsonElement. The integer navigation API retains negative-reserve
validation; an additive configured-reserve path carries file semantics. Effective
reserve is double because integer normalization loses the fractional selection boundary.
Preparation changes only its parameter type, not traversal or file tracking.

The fail-first run also proved PiSharp sent output cap 8192 where Pi sent 4096.
**The user explicitly approved** correcting that branch-only cap to
`min(4096, positive model max)`. Limits 1024/8192/zero are tested independently of
reserve. Compaction output budgets, prompt wording and retry behavior are unchanged.

## Validation and evidence

- [Six physical HTTP PTY captures](settings-branchsummary-reservetokens-pty-requests-2026-11-12.json)
  load settings, open `/tree`, explicitly choose **Summarize** with skipPrompt false,
  and reload persisted sessions. Constrained/equal/negative/fractional/NaN budgets
  are covered; skipPrompt true makes no request and creates no summary.
- Focused settings/navigation/PTY/compaction/context tests: **285 passed, 0 failed, 0 skipped**.
- Full `dotnet test PiSharp.slnx`: **4770 passed, 0 failed, 0 skipped**, no exclusions.
  Base NativeExtensionUiTests passed 44/44; the historical exclusion was not used.
- Warnings-as-errors build: zero warnings/errors. Format verification: clean.
- [skipPrompt differential](settings-branchsummary-reservetokens-skip-regression-2026-11-12.json):
  **40/40**, zero differences; its gate and verified ledger row remain unchanged.
- Independent verifier: **44 mutation controls rejected**, including defaults,
  merge/trust/null/duplicate/coercion, budgets, selected/request history, output cap,
  skipPrompt, calibration, provenance, tests and ledger.
- Exact implementation-head CI: `43313157929`, all five jobs green at `cab70fdd4`.
  A transient local format restore failure cleared after an explicit successful
  `dotnet restore`; the final build and format commands both passed again.

The [acceptance manifest](settings-branchsummary-reservetokens-acceptance-2026-11-12.json)
binds source/evidence hashes and actual TRX digests. Only the reserve capability is
promoted; no compaction ledger row changes. Logs/TRX/raw captures remain ignored under
`artifacts/parity/branch-summary-reserve/`.

```sh
python3 tools/parity/branch_summary_reserve_differential.py --pi artifacts/parity/pi-ownership-oracle --output artifacts/parity/branch-summary-reserve/final
python3 tools/parity/verify_settings_branch_summary_reserve_tokens.py --focused-trx artifacts/parity/branch-summary-reserve/tests/focused.trx --full-trx artifacts/parity/branch-summary-reserve/tests/full.trx --self-test
```

Add `--publication artifacts/parity/branch-summary-reserve/publication.json` to check
the captured exact-final-head run and draft/open/unmerged PR state. Otherwise the
verifier explicitly evaluates local gates only. New test runs change TRX digests;
regenerate acceptance with `branch_summary_reserve_evidence.py` after new captures.

## Limitations

No safety projections in the mandatory corpus. Coercion implements bounded JSON values,
not a general JavaScript runtime or arbitrary prototype callbacks. Raw transcripts
are retained; semantic comparison ignores existing summary-wrapper newline formatting,
but recomputes marker inclusion/order/multiplicity from requests. No new UI/writer,
general schema change, session format change or compaction-setting change is claimed.
