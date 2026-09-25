# Structured search/list result details

Pinned reference: `earendil-works/pi@d5629e20489ccf770ed90b5a33941cb3b7ef24d0`; the inspected `grep.ts`, `find.ts`, `ls.ts`, and tool renderer paths are unchanged in refreshed upstream main `5fd446ca1843682e8da3fec4ceb71c42f56fbace`.

Pi's tool results attach these optional fields to `details`:

| Tool | Detail fields |
|---|---|
| `grep` | `truncation`, `matchLimitReached`, `linesTruncated` |
| `find` | `truncation`, `resultLimitReached` |
| `ls` | `truncation`, `entryLimitReached` |

The truncation record carries `truncated`, `truncatedBy`, `totalLines`, `totalBytes`, `outputLines`, `outputBytes`, `lastLinePartial`, `firstLineExceedsLimit`, `maxLines`, and `maxBytes`. Search/list tools apply a 50 KiB byte limit and pass `Number.MAX_SAFE_INTEGER` as the line limit. Pi's text notices remain in the tool result alongside these details.

PiSharp now returns corresponding `GrepToolDetails`, `FindToolDetails`, and `LsToolDetails` records. `DurableToolFunction` emits these values on `tool_execution_finished`; canonical `FunctionResultContent` history retains the structured result across save/reload. `ObservedChatClient` flattens the result to its text property at the provider boundary.

`StructuredSearchToolTests.GrepFindAndLsExposeTheirLimitDetails` checks each limit field. `GrepReportsByteAndLongLineTruncationDetails` checks byte counts, `truncatedBy`, the complete-line boundary and the long-line flag. `StructuredGrepDetailsPersistAndProviderReceivesOnlyText` invokes grep through MAF, verifies the event and history metadata, reloads the session, and verifies the provider receives only text on both turns. `OutputParserKeepsOrdinaryJsonTextUntouchedAndFlattensSearchRecords` checks the generic output parser does not flatten unrelated JSON text. The local MAF boundary supplies event details as a `JsonElement`; tests inspect its serialized contract instead of relying on a specific runtime representation.

This establishes metadata transport, not complete Pi parity: remaining ignore, traversal, permission, cancellation and filesystem differences are listed in the [feature matrix](../feature-matrix.md). TUI renderer presentation of these details remains part of the TUI work.

## Remaining search gaps

The pinned `find.ts` delegates traversal to `fd` with `--max-results`; pinned `grep.ts` delegates matching to `rg`. PiSharp's `SearchInventory` has a 20,000-path cap, and `SearchTools.GrepForTool` rejects individual files above 10 MiB before reading them. The managed fallback uses .NET regular expressions and `File.ReadAllLinesAsync`.

Direct `rg --json` probes on this host showed that a NUL on the match line or later in a small file suppresses that file's matches. `SearchToolsTests.GrepSkipsNulBinaryFilesLikeRipgrep` checks the same cases plus a plain-text control, and PiSharp now skips files with a NUL. A second probe put NUL at byte offset 1,200,007: ripgrep had already emitted an earlier match, while PiSharp's complete-file NUL scan suppresses it. That late-NUL streaming difference remains. Linux CI run 36102536924 passed for the small-file handling slice. Other effects of the path/file caps, regex dialect, invalid UTF-8, permission and traversal behavior need broader differential coverage. A bundled ripgrep executable is available locally; `fd` is absent, so the pinned default `find` process could not be run.
