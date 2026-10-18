# Native Extension Events & Callback Context Receipt

The receipt path follows the milestone specification. Work began 2026-10-17 and
the verification date is recorded when complete; this copy is dated 2026-10-18.

## Scope and source pins

- Base: PR #11 merge `1a0165c3858d973948c9ac8eb698e75ed0fcc535`.
- Branch: `milestone/native-extension-events-context`.
- Pi source: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (unchanged).
- Product source SHA: `7fe25b4024e5441bb709dbc6fadd50c43411523f`.
- Test-only compaction-recorder correction: `772edfb685414bc9453fdcd0193ccc44402633a3`.
- Push and PR exact-head Linux CI for that code-and-test checkpoint:
  [39082700470](https://github.com/PhilipGB/pisharp/actions/runs/39082700470)
  and [39082703383](https://github.com/PhilipGB/pisharp/actions/runs/39082703383).

Pinned references inspected: [extension event types](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/types.ts), [extension runner](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/runner.ts), [agent session](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/agent-session.ts), [agent loop](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/agent/src/agent-loop.ts), and the pinned extension [API documentation](https://github.com/earendil-works/pi/tree/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/docs/extensions).

## Event contract

Callbacks run sequentially in resolved package registration order. Ordinary
handler exceptions are diagnosed with package source and do not prevent later
handlers or normal execution. Host cancellation uses the owning operation's
token; callbacks waiting on that token are released and detached faults are
observed. Payload messages and collections are detached snapshots. The typed
C# observers below are observational; existing host owners and tool mutation
hooks remain authoritative.

| Event | Trigger and payload | Ordering and frequency | Success, failure and cancellation | PiSharp boundary |
| --- | --- | --- | --- | --- |
| `session_info_changed` | A committed session name change; carries the new name, including `null` when cleared. | Once per effective name change, after canonical save and commit. | Failed save leaves the old name and emits nothing. Cancellation during callback dispatch stops the current observer sequence. | Published centrally by the canonical session owner for extension, startup, TUI and RPC name changes. |
| `session_compact` | Successful persisted compaction; includes reason, compaction result/entry metadata, `willRetry`, and `fromExtension`. | Once after successful persistence. | Not emitted for failure or abort. | `fromExtension` is false because this scope has no custom summary/replacement path. |
| `session_compact_failed` | Failed or aborted compaction; includes reason, `willRetry`, `aborted`, and error text when present. | Once at the owning compaction failure boundary. | Failure does not emit success; cancellation is recorded as aborted when the host has a durable failure outcome. | Compaction stays with the existing coordinator and does not create a second compaction path. |
| `agent_start` | Beginning of one logical agent run. | Before its first turn; not repeated for provider continuations or queued prompts handled by the same event pump. | May be absent when cancellation prevents run startup. | Exposes the active mode and callback capabilities through the shared Runtime event path. |
| `agent_end` | Logical run outcome and detached completed messages. | Once when the run pump reaches a terminal outcome. | Normal completion and provider failures are covered. A host abort can cancel terminal observer dispatch so the run settles promptly; pinned Pi guarantees an end event when its loop returns an aborted response, not when an exception escapes the loop. | PiSharp reports `Completed`, `Error`, or `Aborted` according to the observed terminal path; it does not synthesize Pi's mutable settlement phases. |
| `turn_start` | Zero-based authoritative turn index and timestamp. | At each model/tool continuation, after the preceding `turn_end` and any committed selection notifications. Retries that do not begin a logical turn do not increment the index. | Uses the active run token. | Index comes from the agent run's authoritative turn owner. |
| `turn_end` | Finalized assistant message, tool-result snapshots, canonical entry IDs where available, and aborted/error metadata. | After that provider response and its tool results are finalized; before the next turn starts. | Terminal callbacks may be canceled with the run. | Observational only; PiSharp does not expose mutable Pi `BoundaryState` fields. |
| `tool_execution_start` | Stable call ID, name, parent call ID when nested, and effective arguments. | Per actual tool execution, after PiSharp call hooks and before invoking the tool. | Calls blocked before execution have no start/end pair. Cancellation belongs to the tool operation. | Pi's start hook is earlier (before its preflight path); PiSharp deliberately reports the execution boundary after its existing blocking/mutation hook. |
| `tool_execution_update` | Stable identity plus actual partial-result data. | Only when the tool/provider path yields a real update; no synthetic updates. | Cancellation stops update observation with the owning tool operation. | Streaming paths publish bounded sequential updates; non-streaming tools emit none. |
| `tool_execution_end` | Stable identity, finalized result, `isError`, and duration when execution began. | Once after actual execution and the existing result hook. | A call blocked before execution has no end event. Executed failures have an error end result. | Pinned Pi's tool-end payload is post-hook; PiSharp likewise exposes the finalized post-result-hook value, not the raw envelope. |
| `model_select` | Effective selected model, previous model, and source (`Set`, `Cycle`, or `Restore`). | After the selection becomes authoritative; during an active run it is dispatched after old turn end and runtime activation, before the next turn start/request. | Rejected or unchanged effective selections emit no event. | Active-run changes retain the existing queued runtime transition and do not replace the provider under an in-flight request. |
| `thinking_level_select` | Effective selected and previous thinking levels. | Together with the committed model transition when needed; follows capability clamping. | Rejected or unchanged effective levels emit no event. | Uses the existing thinking owner and transition queue. |

### Safe callback context

`PiSharpExtensionContext.Capabilities` is callback-scoped and revoked on return.
It provides detached session/model/thinking/tool/command/settings/flag snapshots,
context usage and effective system prompt where known, plus operations routed
through existing host ownership. TUI/RPC contexts reuse the existing controls;
print/JSON and direct Runtime contexts expose read state and only operations
available in that phase. Session state writes use the canonical session bridge.
Reentrant compaction is rejected while a callback belongs to an active run.

## Verification record

Local validation on 2026-10-18:

- `dotnet restore PiSharp.slnx -p:NuGetAudit=false`: up to date.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore`: passed.
- `dotnet build PiSharp.slnx --no-restore --warnaserror`: passed, 0 warnings.
- `TMPDIR=/tmp dotnet test PiSharp.slnx --no-build --no-restore`: 2,305 passed,
  0 failed, 0 skipped. The `/tmp` override matches CI; the ambient local TMPDIR
  is under the home directory and makes two existing path-presentation assertions
  environment-dependent.
- Architecture tests: 7/7; terminal Node tests: 24/24; Python terminal parity:
  50/50; documentation paths: 141 documents and 544 references.
- MCP conformance: 8 previously classified failures unchanged; all other cases
  passed, with no new regression.
- Event integration wave, including the external compiled-package process,
  package reload/removal and ALC collection: 25/25 passed.
- The CI-discovered overflow-compaction ordering assertion combined typed
  callback delivery with the independently consumed run-event stream. The test
  now records and asserts each stream's ordering separately; all six focused
  compaction tests and the full suite pass.

This bounded milestone does not complete the broad Extensions / .NET API parity
row.

## Explicit limits retained

- No generic `pi.on(...)`, dynamic registration, `agent_before_settle`, mutable
  turn boundary state, or `agent_settled`.
- No session before-switch/fork veto, tree navigation interception, or custom
  compaction replacement.
- No provider request/response wire hooks, input rewriting, or `user_bash` hook.
- No advanced UI/rendering, provider OAuth/custom streams, or TypeScript support.
- Broad extension parity remains `in_progress`; the final global parity audit is
  deferred.
