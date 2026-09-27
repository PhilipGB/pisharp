# RPC manual compact differential

Reference: Pi `2b0a123de98318c2ff8069661721ce0c3794c34e` (0.87.1), checked 2026-09-27. Both CLI processes used the same deterministic OpenAI Chat Completions loopback provider. The fixture completed three turns, streamed partial text from a fourth turn, called RPC `compact` with custom instructions, then inspected the response, lifecycle and persisted entries.

## Matched behavior

- Both accepted `compact` during a blocked provider response, aborted and settled that turn before `compaction_start`, then emitted `compaction_end` before the correlated response.
- Both returned success with the same keys: `summary`, `firstKeptEntryId`, `tokensBefore`, `estimatedTokensAfter`, `usage` and `details`. Both emitted one compaction start/end pair, observed the supplied custom instructions, persisted one compaction entry, and exited without stderr.
- Both persisted the interrupted partial assistant as a Pi `message` entry with `stopReason: "aborted"` and `errorMessage: "Request was aborted"`. Before this slice, PiSharp kept only its internal `interrupted` node in `get_entries`; `RpcInterruptedSessionProcessTests` now verifies the Pi-shaped entry and `get_messages` snapshot, reload persistence, and inclusion in the next provider request.

## Remaining differences

| Observation | Pi | PiSharp |
|---|---:|---:|
| Provider requests | 6 | 5 |
| Compaction calls | 2 | 1 |
| `firstKeptEntryId` points to | Aborted assistant message | Active user message |
| `summary` | `deterministic compact summary\n\n---\n\n**Turn Context (split turn):**\n\ndeterministic compact summary` | `deterministic compact summary` |
| `tokensBefore` / `estimatedTokensAfter` | 1467 / 1441 | 51 / 43 |
| Reported summary usage | All token counts zero | 30 input, 5 output, 35 total |

Pi's `prepareCompaction` selects a split-turn boundary and separately summarizes older history and the earlier part of the active turn. PiSharp currently keeps whole user turns and uses its conservative character estimate over canonical context, so the cut point, number of summary requests, summary text and token counts differ. The usage mismatch comes from this fixture's streamed Pi summary responses versus PiSharp's nonstream summary request path. These differences remain open; the command is not behaviorally equivalent.
