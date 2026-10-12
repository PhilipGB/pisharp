# Conversation-run slice

H04 separates per-attempt provider history/tool/nested-result journaling, physical
request identity/billing attribution and MAF stream progress/checkpoint/disposal.
ConversationRun retains prompt acceptance, retry/compaction orchestration and
explicit canonical history validation/settlement ordering. Shared runtime-state
locks protect parallel result capture and turn closure. Public APIs, native/Pi
payloads and direct framework use remain; no project/interface/package was added.

Pre-change protection: 36 lifecycle/durable/history tests. The new initial-save
failure test first expected turn_failed; untouched production returned the existing
prompt_rejected plus settlement. The expectation was corrected before production
edits and the raw attempt is retained in `characterization-first-expectation/`.
The resulting test protects no prompt acceptance/provider request and subsequent
run reuse. No production behavior was altered to rescue a failing characterization.

Each journal/attribution/stream extraction stage passed 233 focused tests. Complete
.NET passed 1,363 rows with zero skips; all original 1,353 logical groups and 1,356
rows survive in `broad-dotnet/preservation-comparison.json`, with seven added
characterization groups. Formatting and warnings-as-errors build pass.

All pilot process differential/virtual variant invocations were reused at frozen
Pi. Complete extension-resource reload and read/Bash fixtures run calibration/pair
and both semantic assertions pass. All 28 affected identities retain accepted
baseline outcomes. The old invalid-criteria classifier failure is unchanged;
expectations and frozen outcome sets were not widened. MCP/Node/Python retain
full pilot evidence; this is a bounded affected-scope slice, not a new full
cross-language checkpoint. No test/project movement required mappings.

`verification.json`, source hashes, TRX and process/terminal logs/reports record
scope and setup. Bulk streams are hashed/omitted with compact assertions retained.
Whitespace-bearing raw files are losslessly gzip-compressed with receipts.
Permanent capability flow is documented in `docs/capabilities/agent-execution.md`.
