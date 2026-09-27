# PiSharp JSONL lifecycle contract (experimental)

This contract belongs to PiSharp, not Pi's JSON or RPC wire protocol. `--mode json` prints one `session` record, then LF-framed `event` records. `--mode rpc` accepts LF-framed command objects and emits correlated `response` records plus the same `event` records. JSONL stdout is reserved for records. Each event has `{ "type": "event", "format": "pisharp", "data": { "Type": "...", "Text": null, "Tool": null, "OperationId": null, "IsError": null, "Error": null, "InputTokens": null, "OutputTokens": null, "CachedInputTokens": null, "ReasoningTokens": null, "TotalTokens": null, "Cost": null } }`; null-valued fields may be included. JSON separators are literal LF bytes, not Unicode line separators. The session header's `version` is the current PiSharp session document format, **not** a claim of protocol compatibility.

| Event `data.Type` | Meaning |
|---|---|
| `prompt_accepted` | A prompt has been checkpointed when the session is durable; a model request can now begin. |
| `context_compacted` | An opt-in pre-prompt budget summarized previous turns and saved model context, before accepting the pending prompt. Raw history remains. |
| `prompt_rejected` | The prompt could not be accepted or persisted; no successful run is claimed. |
| `model_request_started`, `model_text_delta`, `model_request_completed` | Actual provider request, its text deltas, and its normal completion. A multi-tool run can contain multiple requests. |
| `model_request_failed`, `model_request_interrupted` | Provider failure/cancellation; no normal request completion is emitted for that request. |
| `model_retry_scheduled` | A transient provider request failed before yielding any content and will be retried. Requests that yielded text or any other content are never automatically retried. |
| `tool_execution_started`, `tool_execution_finished` | Actual local tool invocation and its outcome, correlated by `OperationId`; `IsError` is true for a failed tool. A durable invocation begins only after its intention is checkpointed. |
| `tool_outcome_unknown` | The tool ran but its result could not be durably saved; **do not retry automatically**. |
| `reasoning_delta`, `usage` | Provider-dependent content surfaced by MAF. `usage` has structured token counts and configured USD cost; absent provider counts/pricing remain unknown. |
| `prompt_queued`, `queue_update` | Text was queued while a run was active, or the pending steering/follow-up snapshot changed. `Tool` identifies `steering` or `follow_up`; `queue_update.Text` is a JSON object containing both arrays. |
| `turn_completed`, `agent_run_completed`, `agent_settled` | Successful turn, successful agent run, then all work settled. Only successful runs emit the first two. |
| `turn_failed`, `turn_interrupted` | Run ended abnormally. `agent_settled` still follows after cleanup, but no successful completion is implied. |

PiSharp retries a transient provider exception at most twice with a short delay, but only when that provider attempt yielded no content. This retry wraps each MAF model request, including continuation requests after tools; it never retries tool execution and never retries a request after partial text, reasoning, or tool-call content.

## RPC prompts and queues

`prompt` success follows preflight and returns `data.disposition` (`started`, `queued` or `handled`). A started prompt response precedes `prompt_accepted`; this acknowledges acceptance, not completion or durability. Wait for lifecycle events and `agent_settled` to observe completion. Text prompts received during a run require `streamingBehavior: "steer"` or `"followUp"`. Explicit `steer` is delivered before the next provider request after the current response/tool batch; `follow_up` waits until steering and current work drain. Both require an active run and return `data.disposition: "queued"`.

Registered native extension commands are recognized before resource expansion, image rejection and ordinary active-run queueing. Their returned text is emitted as a PiSharp `format: "pisharp"` `extension_command_output` event before the handled response. A thrown handler emits Pi-shaped `extension_error` and still returns `handled`.

`clear_queue` atomically returns `{steering, followUp}` and removes input not yet sent to the model. Queue changes emit top-level `{type: "queue_update", steering, followUp}` snapshots; the internal `prompt_queued` lifecycle notice is not exposed. `abort` cancels the active turn. Pending input remains available to `clear_queue`.

## Runtime and model commands

`set_model` accepts string `provider` and `modelId`, validates the exact model, requires an authenticated runtime, records the selection and rebuilds the provider/client/agent runtime. `cycle_model` advances through available provider/model pairs. Both work during active runs; a changed model applies to the next provider request. `set_model` returns the projected model; `cycle_model` returns `null` when there is no alternative, otherwise `{model, thinkingLevel, isScoped}`.

`set_thinking_level` and `cycle_thinking_level` can also change the level during an active run; the new level applies to the next provider request. A changed effective level emits `{type: "thinking_level_changed", level}` before the correlated response. Repeating the current level emits no event. `get_available_thinking_levels` returns `{levels}`; PiSharp reports `["off"]` for models without reasoning and seven levels for models that advertise reasoning. The logical `max` maps to M.E.AI extra-high effort; provider-specific value maps are not modeled.

`get_state` is available during a run and includes `thinkingLevel`, `isStreaming`, `isCompacting`, session identity, and queue state. Invalid or unavailable selections return one correlated failure without changing the active runtime. `get_available_models` queries the configured OpenAI-compatible `/models` endpoint and returns bounded model metadata; endpoint failures are reported explicitly. `get_commands` returns discovered prompt and skill names. RPC prompts beginning with `/skill:<name>` or a discovered `/<template>` are expanded before acknowledgement; invalid skill names fail preflight. Print, JSON and terminal prompts use the same resource resolver.

## Session reads, compaction and export

`get_messages`, `get_fork_messages`, `get_last_assistant_text`, `get_entries` and `get_tree` read a cloned conversation snapshot while a provider run is active. The snapshot includes accepted and persisted messages, but not a partial assistant message before `message_end`. When a run is aborted after partial output, the settled snapshot exposes it as a Pi-shaped assistant message with `stopReason: "aborted"` and `errorMessage: "Request was aborted"`; the next provider request also receives that partial assistant context. `set_session_name` is idle-only and persists a Pi `session_info` entry before emitting `session_info_changed` and its success response. Session replacement commands coordinate cancellation and replacement with the active run; their preflight and event ordering are command-specific.

`compact` accepts optional text `customInstructions`. If a run is active, it cancels and settles that run before compaction. A successful response contains `summary`, `firstKeptEntryId`, `tokensBefore`, `estimatedTokensAfter`, optional `usage`, and `details`; the raw transcript remains in session history while the model-context projection uses the summary. `compaction_start` and `compaction_end` bracket the operation and precede its response. A paired Pi process fixture matches active-abort and event ordering, response keys and persisted compaction, while split-turn summaries, first-kept cut points, usage values and token estimates still differ; see [the compact differential](parity/fixtures/rpc-compact-baseline.md). Too-small or already-compacted sessions return a correlated failure.

`export_html` accepts an optional nonempty `outputPath`; otherwise it uses `pisharp-<session-prefix>.html` in the session working directory. It works during provider streaming by exporting a canonical tree snapshot, so a partial streamed assistant message is excluded. The export is private, covers all branches and does not overwrite an existing file; success returns `{path}`. The output contains sensitive content.

`get_session_stats` also works during active runs. It returns Pi-shaped message, token and cost totals over persisted session entries, plus optional `contextUsage`; context-token estimates are approximate and can differ from Pi. Cost is `null` if any usage record lacks configured pricing.

## Stream and persistence notes

Tool and provider output may contain sensitive data; treat JSONL as secret-bearing. Events are transient and are not replayed from a session file after reconnect; use `get_entries` to discover persisted state. Lifecycle output uses a bounded 256-event producer/consumer buffer. A slow consumer stalls the run rather than dropping events, and disposing the stream cancels its producer. Richer event metadata, concurrency and remaining commands still need implementation.
