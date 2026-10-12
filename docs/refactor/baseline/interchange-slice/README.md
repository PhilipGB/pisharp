# Session-interchange slice

H06 separates import migration, entry/content conversion, ordered private export
and run-window projection. Internal consumers use the actual codec/projector;
the public interchange API, assembly, native/Pi formats and project graph remain.
No tests/projects moved or names changed, so logical/invocation mappings are intact.

The invalid-image/original-entry characterization passed against the unmodified
interchange implementation. Pre-change characterization: 23 tests; focused RPC,
session, interchange, compaction, recovery and tool contracts: 203 tests; complete
.NET: 1,359 tests, zero skips. `broad-dotnet/preservation-comparison.json` preserves
all 1,353 baseline .NET groups and original 1,356 rows, with three added groups.
Warnings-as-errors build and formatting pass.

The complete transcript-resume fixture covers eight dimensions/themes/modes in
both calibration and paired modes. Calibration matches; paired presentation
differences remain exactly within the frozen baseline outcomes. Its 16 logical
identities pass `terminal-preservation-comparison.json`; no fixtures, normalization
rules or expectations changed. No package/project/dependency boundary changed;
MCP, Node and Python are unaffected by this slice and retain the full pilot evidence.

`verification.json`, `source-manifest.json`, raw TRX/logs and terminal reports
record commands, scope and candidate source. Bulk streams are hashed/omitted with
compact assertion captures retained. Whitespace-bearing raw files, when present,
are losslessly gzip-compressed with hash receipts. Permanent capability documentation
is in `docs/capabilities/session-interchange.md`.
