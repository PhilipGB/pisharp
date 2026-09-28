# RPC split-turn compact differential

Paired reference: Pi `2b0a123de98318c2ff8069661721ce0c3794c34e` (0.87.1), checked 2026-09-27. Both CLI processes compacted the same deterministic transcript through a loopback OpenAI-compatible provider: an earlier user/assistant exchange, a current user request, a `read(current.txt)` call and result, and an interrupted assistant message. Both used `keepRecentTokens: 1` and the custom focus `preserve decisions`. The Pi CLI used an SSE provider response; PiSharp used its SDK's JSON response path.

Current-main check: Pi `6f7551516b84278eb9da1c340c8e7bc66be1a6ba`, verified 2026-09-28. A current `pi --mode rpc` process replayed that transcript against a loopback SSE endpoint. It made two summary requests; the custom focus appeared in the history request only; `firstKeptEntryId` resolved to the aborted assistant message; and the response contained the split-turn summary, `current.txt` details, and 30/7/37 aggregate usage. Pi's estimates were 25 tokens before and 41 after in this run. The current PiSharp process regression, `RpcProcessUsesPiSplitTurnCompactionAndCustomInstructionsForHistoryOnly`, verifies the matching boundary, summary, file detail, usage, persisted entry, and event order. Exact token-estimate comparison for identical process/session state remains open; the historical paired run below recorded different inputs and estimates.

## Matched behavior

- Both RPC processes returned success with the same result fields and emitted one `compaction_start`/`compaction_end` pair before the correlated response. Both persisted one compaction entry and exited without stderr.
- Both made two summary requests. The history request included the custom focus; the split-turn request did not. The second request summarized the current user request and completed tool call/result before the retained interrupted assistant message.
- Each process pointed `firstKeptEntryId` at its interrupted assistant entry. The identifiers differ between the independently created sessions; the entry type and retained context match.
- Both produced the same combined summary, including the `**Turn Context (split turn):**` section and `<read-files>` metadata for `current.txt`.
- Both reported combined summary usage of 30 input, 7 output, and 37 total tokens, with identical `readFiles` and `modifiedFiles` details.

## Remaining estimate difference

| Observation | Pi | PiSharp |
|---|---:|---:|
| `tokensBefore` | 31 | 1085 |
| `estimatedTokensAfter` | 41 | 67 |

Pi's hand-authored session has no persisted system-message entry, and its compaction projection estimates the visible transcript. PiSharp includes its configured system instructions and tool declarations in `tokensBefore`, while its after estimate uses the projected messages. This changes the reported estimate but not the split boundary, retained context, summary-call purpose, summary result, or billing usage. Token-estimation parity remains open and should be evaluated against sessions that persist Pi's runtime system message as well.
