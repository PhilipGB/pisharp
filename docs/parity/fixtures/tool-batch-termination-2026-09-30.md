# Tool-batch termination — 2026-09-30

Current Pi `3e9451238337071b74ba5cdd53f1ab7cf4100ae8` stops a finalized tool batch only when every result has `terminate: true`, for both sequential and parallel execution. Installed MEAI 10.10.0 (source commit `02107c65bab30aad9e35b5133ed643eaa77bccd8`) stops when any invocation context terminates. PiSharp previously handled only FunctionCount=1.

Fail-first provider-loop coverage returned two requests for two terminating tools where one was required; all three mixed/non-terminating cases passed. Completion signals prove both sibling calls start and the run remains unfinished while one sibling is withheld. There are no synchronization sleeps.

ToolBatchTermination owns per-history/per-iteration aggregation shared by root DurableToolFunction wrappers. Each finalized sibling records its decision; only the final all-terminating completion signals MEAI. Nested calls do not vote in the root batch, exceptions do not fabricate a terminating result, and unknown siblings prevent a partial vote from stopping continuation. Weak history keys isolate runs without retaining session history.

Deterministic provider-loop checks cover four flag combinations, mixed then all-terminating batches, explicit returned errors, unknown siblings and thrown failures, with balanced persisted call/results and native reload. Focused orchestration/Codemode checks pass 38/38. The paired process fixture compares five flag sequences against both current Pi sequential and parallel loops (ten matches), including lifecycle result flags and provider request counts. Run `python3 tools/parity/tool-batch-differential.py --pi /path/to/pi --output /tmp/tool-batch.json`. The captured fixture is tool-batch-termination-process-2026-09-30.json.

Pi's canonical toolResult messages omit the transient termination flag; PiSharp retains it as extra canonical/interchange metadata. This bounded shape difference does not alter provider-facing results or batch continuation. Broader orchestration/extension/provider/auth and final audit work remains.

Final format verification and warnings-as-errors build pass with zero warnings/errors; full solution tests pass 982/982 with zero skips. Exact-head publication/CI is pending.
