# PiSharp JSONL lifecycle contract (experimental)

This contract belongs to PiSharp, not Pi's JSON or RPC wire protocol. `--mode json` prints one `session` record, then LF-framed `event` records. `--mode rpc` accepts LF-framed command objects and emits correlated `response` records plus the same `event` records. JSONL stdout is reserved for records. Each event has `{ "type": "event", "format": "pisharp", "data": { "Type": "...", "Text": null, "Tool": null, "OperationId": null, "IsError": null, "Error": null } }`; null-valued fields may be included. JSON separators are literal LF bytes, not Unicode line separators. The session header's `version` is the current PiSharp session document format, **not** a claim of protocol compatibility.

| Event `data.Type` | Meaning |
|---|---|
| `prompt_accepted` | A prompt has been checkpointed when the session is durable; a model request can now begin. |
| `prompt_rejected` | The prompt could not be accepted or persisted; no successful run is claimed. |
| `model_request_started`, `model_text_delta`, `model_request_completed` | Actual provider request, its text deltas, and its normal completion. A multi-tool run can contain multiple requests. |
| `model_request_failed`, `model_request_interrupted` | Provider failure/cancellation; no normal request completion is emitted for that request. |
| `tool_execution_started`, `tool_execution_finished` | Actual local tool invocation and its outcome, correlated by `OperationId`; `IsError` is true for a failed tool. A durable invocation begins only after its intention is checkpointed. |
| `tool_outcome_unknown` | The tool ran but its result could not be durably saved; **do not retry automatically**. |
| `reasoning_delta`, `usage` | Provider-dependent content surfaced by MAF. These are not guaranteed for every model. |
| `turn_completed`, `agent_run_completed`, `agent_settled` | Successful turn, successful agent run, then all work settled. Only successful runs emit the first two. |
| `turn_failed`, `turn_interrupted` | Run ended abnormally. `agent_settled` still follows after cleanup, but no successful completion is implied. |

`prompt` RPC response `success: true` acknowledges acceptance **by the RPC command loop**, not completion or durability; wait for `prompt_accepted` or `prompt_rejected` and then `agent_settled`. `abort` cancels the active run. `get_commands` returns currently discovered prompt and skill names; a `prompt` beginning with `/skill:<name>` or a discovered `/<template>` is expanded before acknowledgement (invalid skill names reject). Print, JSON and terminal prompts use the same resource resolver. Idle-only state/entry/tree/name commands reject while a run is active. See `README.md` for the supported command subset. Tool and provider output may contain sensitive data; treat JSONL as secret-bearing. Events are transient and not replayed from a session file on reconnect; use `get_entries` to discover persisted state. Lifecycle output has a bounded 256-event producer/consumer buffer; a slow consumer stalls the run rather than dropping events, and disposing the stream cancels the producer. Richer event metadata, concurrency and remaining commands need further implementation.
