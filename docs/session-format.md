# Pi session JSONL interchange

PiSharp keeps its own canonical C# conversation tree. This adapter reads and writes Pi JSONL so sessions can cross the process boundary without requiring the runtime to use Pi's internal session model.

## Import

The importer accepts Pi session versions 1, 2 and 3. It migrates v1 entries to generated IDs and a linear parent chain, maps the v1 compaction index to an entry ID, and converts legacy v1/v2 `hookMessage` roles to `custom`. It retains original Pi records so records outside PiSharp's runtime projection can be exported again.

Messages, assistant reasoning and tool calls/results, images, Bash records, model changes, compaction, context edits, branch summaries and session metadata are projected where supported. The active path builds the MAF message context; context edits and compaction entries affect that projection. Import is bounded to 128 MiB per file and 16 MiB per line. Blank lines, malformed JSON lines, non-record JSON, and oversized lines are skipped; a missing/unsupported header, duplicate ID or forward/missing parent fails import.

The CLI accepts a `.jsonl` path as `--session`, or `/import <path>` after explicit confirmation. The Pi session's recorded `cwd` must exist and match the current PiSharp project directory. Cross-project import that changes the running workspace is not implemented yet.

## Export

`/export-jsonl [path]` writes Pi v3 JSONL. It serializes every branch parent-first and appends a `session_info` entry when needed so Pi's last-leaf selection restores PiSharp's selected head. Imported records are retained, while native PiSharp messages and Bash execution records are projected into Pi-compatible entries. Export creates a new file and never overwrites an existing path; on Unix, the file and its containing directory use user-only permissions.

The interchange tests cover v1-v3 import, v1/v2 migrations, branch/context-edit/compaction round trips, original record retention, active-head restoration, malformed inputs, naming, exclusive private export, and cancelled-write cleanup. PTY tests cover confirmation/cancel/retry, startup `--session`, restored transcript visibility, and `/export-jsonl`. This is scoped behavior evidence; full Pi session-manager equivalence, cross-project import, crash recovery, concurrent access and broad live Pi differentials remain open.
