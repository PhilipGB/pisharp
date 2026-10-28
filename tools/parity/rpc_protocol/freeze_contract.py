#!/usr/bin/env python3
"""Create the immutable, case-ID-derived RPC parity acceptance contract."""
from __future__ import annotations

import hashlib
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
INVENTORY_PATH = ROOT / "docs/parity/fixtures/rpc-protocol-command-parity/inventory.json"
CONTRACT_PATH = ROOT / "tools/parity/rpc_protocol/contract.json"
INDEX_PATH = ROOT / "docs/parity/fixtures/rpc-protocol-command-parity/required-case-index.json"
PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"
BASE_SHA = "646db89bd396b8089b1b9a058fa96b1489f4d0cd"
BRANCH = "milestone/rpc-protocol-command-parity"

# Rows are immutable case declarations: slug<TAB>controlled input<TAB>observable pass condition.
# Counts are always derived from these IDs, never used to size or prune this list.
CASES = {
"P0": """
reviewed-head\tFetch PR #28 metadata and compare head to c019cd0111106b70a83d56e30d86932c987291e1\tPR head, reviewed source 41e9b7e7ebdea70b878e409204cd8b3d177144e2 and docs-only descendants match the frozen review.
exact-head-ci\tRead push run 40747257587 and PR run 40747270521\tBoth automatic workflows are successful on the reviewed exact head and every mandatory job passed.
published-acceptance\tLoad committed PR #28 evidence and its verifier output\tThe source-bound prerequisite reports 22/22 checkpoints, 211/211 mandatory cases, zero failures/skips, and E0 CI0 PUB0 pass.
merge-state\tFetch PR #28, merge metadata and current main\tPR #28 is merged, ready before merge, has no unresolved blocker, and its actual merge SHA is 646db89bd396b8089b1b9a058fa96b1489f4d0cd.
reviewed-code-ancestry\tCheck merge parents and ancestry for reviewed head and implementation source\tThe reviewed head and implementation source are ancestors of the actual merge commit; changes after implementation are documentation/evidence only.
merge-base-verification\tFetch origin/main and inspect milestone branch merge-base\torigin/main contains the actual PR #28 merge and the new branch starts at that exact merge commit.
""",
"P1": """
branch-name-and-base\tInspect current branch and first parent\tCurrent branch is milestone/rpc-protocol-command-parity and begins at actual merge 646db89bd396b8089b1b9a058fa96b1489f4d0cd.
baseline-main-ancestry\tCompare branch ancestry with fetched origin/main\tThe reviewed PR #28 merge is reachable and no unrelated base commit is substituted.
pinned-pi-sha\tRead the pinned Pi checkout commit and source-pin file\tPinned Pi is exactly b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6 and all inventoried files are tracked at that SHA.
baseline-tree\tRecord baseline tree, solution and package versions\tThe clean baseline is attributable to the actual merge; later contract/tooling changes are separately visible as this branch's commits.
maf-version\tRead runtime project/package lock and SDK metadata\tThe existing Microsoft Agent Framework package version and .NET 10 target are recorded without introducing another execution framework.
""",
"V0": """
all-command-schemas\tParse pinned RpcCommand and RpcResponse type unions and command docs\tAll 33 request variants have one response success schema, docs section, source location, PiSharp owner, existing test references and checkpoint assignment.
all-event-families\tParse AgentEvent, AgentSessionEvent, RPC-only event and JSON docs\tAll 24 event families have schema variants, source locations and JSON event documentation; inherited and overridden fields are identified.
extension-ui-schemas\tParse extension UI request/response types and RPC UI documentation\tAll 9 methods and value/confirmed/cancelled response forms are inventoried with dialog versus fire-and-forget behavior.
state-and-error-projections\tParse RpcSessionState, shared RpcResponse error and parse-error docs\tAll 12 state fields and success/error/absent-id semantics are source-linked; unknown and invalid request behavior remains explicitly testable.
protocol-operation-coverage\tCompare JSONL framing, correlation, event interleave, UI response and EOF sources\tAll 5 record families and 7 protocol operations map to pinned sources and required R1/R2/R14/R15 cases.
owner-and-gap-register\tCross-check owner paths, existing tests, fixtures and prior residual audits\tExisting references are labeled as baseline only; historical mismatches and unverified areas are not presented as verified parity.
""",
"V1": """
valid-positive-control\tVerify a complete synthetic case result with distinct Pi/PiSharp captures and matching strict observation\tVerifier accepts one fully valid source-bound positive-control case with raw capture digests and all required fields.
wrong-pi-sha\tMutate evidence pinned Pi SHA by one hex digit\tVerifier rejects evidence whose Pi source pin differs from the frozen contract.
wrong-implementation-sha\tMutate implementation source SHA to a non-ancestor\tVerifier rejects an implementation SHA that is not the tested source ancestor.
stale-evidence\tChange current source identity after generating evidence\tVerifier rejects evidence stale against current production, test or parity-tool sources.
mutated-jsonl-framing\tChange LF record delimiter or split a JSON string on U+2028\tVerifier rejects framing evidence that differs from the captured protocol contract.
missing-response\tRemove a required correlated command response from PiSharp capture\tVerifier rejects a missing command response.
duplicate-response\tDuplicate one response record for a single accepted request\tVerifier rejects the duplicate response.
wrong-correlation-id\tChange a response id while leaving request unchanged\tVerifier rejects the mismatched or unexpectedly present correlation id.
reordered-lifecycle\tSwap message_end and turn_end in the captured event stream\tVerifier rejects lifecycle events whose required order changed.
dropped-stream-fragment\tRemove one text or reasoning delta from one side\tVerifier rejects dropped streaming fragments.
altered-tool-arguments\tMutate one serialized tool argument while preserving the call id\tVerifier rejects altered tool-call arguments.
incorrect-queue-state\tChange a full queue_update snapshot or pending count\tVerifier rejects incorrect steering/follow-up queue state.
incorrect-session-selection\tChange selected leaf/session path in an otherwise valid tree result\tVerifier rejects the wrong selected session or branch.
incorrect-token-cost-totals\tChange usage token or cost totals in stats evidence\tVerifier rejects incorrect token/cost totals including branch-summary accounting.
incorrect-cancellation-settlement\tRemove or alter cancellation settlement events\tVerifier rejects incorrect abort/cancel or settlement observations.
unexecuted-case\tMark one mandatory result executed=false\tVerifier rejects a required case without an executed process observation.
skipped-case\tMark one mandatory result skipped=true\tVerifier rejects any skipped mandatory case.
unexpected-case-id\tAdd a result whose ID is absent from the frozen case list\tVerifier rejects unexpected case IDs.
missing-raw-capture\tRemove a required Pi or PiSharp process capture reference\tVerifier rejects evidence without raw process captures.
altered-artifact-hash\tMutate one referenced capture after recording its digest\tVerifier rejects artifact hash mismatch.
failed-ci\tSet one required exact-head workflow job to failure\tVerifier rejects failed mandatory CI.
wrong-head-ci\tProvide successful workflow metadata for a different commit\tVerifier rejects successful CI that does not match the final PR head and base.
changed-contract\tChange a contract input, expected observation or case ID\tVerifier rejects any change to the frozen contract digest or required case identity.
""",
"R1": """
lf-delimited-record\tSend two JSON object commands separated only by LF\tEach object is parsed once and yields exactly one response without merging adjacent records.
utf8-split-codepoint\tSplit a multibyte UTF-8 character between stdin chunks in a JSON string\tDecoded request preserves the exact Unicode scalar and remains valid JSON.
optional-crlf\tSend valid request records terminated by CRLF\tThe CR immediately before LF is removed and each request succeeds.
unicode-separators-in-string\tInclude U+2028 and U+2029 inside a JSON string followed by LF\tSeparators remain string content and do not split or corrupt the record.
incremental-and-multiple-chunks\tSplit one record over chunks and place multiple records in a later chunk\tReader handles incomplete and multiple records in original order.
incomplete-record-completion\tSend a partial object, pause, then send remaining bytes and LF\tNo premature parse occurs; the completed record is dispatched once.
large-valid-record\tSend a valid request with a 1 MiB UTF-8 message body\tThe process accepts valid input without truncation or framing corruption.
malformed-json-recovery\tSend malformed JSON, then a valid get_state record\tA parse error is emitted and the subsequent valid record is still handled.
empty-and-blank-input\tSend empty stdin and blank LF records\tEOF exits cleanly; blank/malformed record behavior matches pinned Pi and does not corrupt later commands.
eof-incomplete-final-record\tClose stdin after a complete JSON object without trailing LF\tFinal record handling and shutdown match the pinned LF reader contract.
stdout-stderr-purity\tTrigger startup diagnostics and normal RPC responses\tEvery stdout line is a valid protocol record and diagnostics are confined to stderr.
""",
"R2": """
correlated-response-id\tSend valid command with fixed string id req-001\tExactly one response repeats the exact id and command name.
uncorrelated-response-omits-id\tSend valid command without id\tResponse omits id rather than emitting null or an empty value.
id-type-validation\tSend id values null, number, object and empty string\tAcceptance or error behavior matches the pinned parser; no wrong-type id is silently correlated.
success-response-shape\tRun commands with and without documented data payloads\tSuccess response contains precisely type=response, command, success=true and documented data presence.
error-response-shape\tRun unknown, invalid and semantically failing requests\tEach failure has success=false and error string with pinned command/id presence rules.
absent-versus-null-fields\tCompare omitted optional fields with explicit null fields\tResponse and event projection preserves the upstream absent/null distinction.
unknown-command-recovery\tSend unknown type followed by get_state\tUnknown command receives one error and later command still receives its response.
invalid-request-shapes\tSend scalar, array, missing type and wrong-type required fields\tValidation errors match pinned behavior and process remains protocol-framed.
missing-required-properties\tOmit message, command, sessionPath, entryId and name in separate requests\tEach missing-required-field result matches the pinned validation/error contract.
overlapping-correlated-requests\tSend multiple fixed-ID read commands before consuming output\tEvery response correlates to its request regardless of completion order, with no duplicate or lost response.
long-command-acceptance\tIssue compact and prompt with deterministic delayed local provider\tCommand acceptance response timing is distinguished from later lifecycle completion.
active-compaction-command\tIssue a state read and a valid command during blocked compaction\tResponse ordering and active-operation behavior match Pi without corrupting the run.
""",
"R3": """
idle-prompt-started\tSend prompt while idle to deterministic loopback provider\tResponse reports started, then provider/message events and one agent_settled occur.
prompt-handled-without-run\tSend an installed extension or slash prompt handled synchronously\tDisposition is handled and no agent_settled is awaited for a run that never started.
busy-prompt-default-behavior\tSend prompt without streamingBehavior while provider streaming\tQueue/steering default and disposition match pinned Pi.
busy-prompt-explicit-steer\tSend prompt with streamingBehavior=steer during active provider stream\tPrompt is routed as steering input with the correct disposition and queue event.
busy-prompt-explicit-followup\tSend prompt with streamingBehavior=followUp during active provider stream\tPrompt is routed as follow-up input with the correct disposition and queue event.
prompt-during-tool-execution\tSend prompt while a deterministic tool is blocked at a barrier\tAcceptance and subsequent provider/tool order match the pinned session lifecycle.
subscribe-before-fast-completion\tSend prompt to immediate fake provider while continuously reading stdout\tNo start, message or settlement event is lost around command acceptance.
acceptance-before-completion\tUse delayed provider and compare request response with completion events\tPrompt response means accepted/queued only and precedes final agent_settled.
provider-failure-after-acceptance\tAccept prompt then return deterministic provider error\tPrompt response remains success and failure is reported through message/events as upstream specifies.
abort-prompt-run\tAbort an active blocked provider request using a correlated abort command\tPartial content, message/turn/agent settlement and abort response match pinned ordering.
""",
"R4": """
steer-command-idle\tSend steer while idle with fixed id and a message\tAccepted disposition and immediate or queued behavior match pinned Pi.
followup-command-idle\tSend follow_up while idle with fixed id and a message\tAccepted disposition and delivery timing match pinned Pi.
steering-all-mode\tSet steering mode all and enqueue multiple messages during a turn\tAll steering messages are delivered together in upstream order before the next provider request.
steering-one-at-a-time-mode\tSet steering mode one-at-a-time and enqueue multiple messages\tOnly one steering message is delivered at each eligible boundary and remainder stays queued.
followup-all-mode\tSet follow-up mode all and queue several messages\tAll follow-up messages are delivered at the upstream-defined turn boundary in order.
followup-one-at-a-time-mode\tSet follow-up mode one-at-a-time and queue several messages\tOne follow-up is delivered per eligible boundary and remaining queue state is exact.
steer-followup-precedence\tQueue both classes while provider and tool work are active\tSteering/follow-up precedence and next provider request history match pinned Pi.
multiple-concurrent-queue-updates\tIssue several queued commands with distinct ids while busy\tEach queue_update contains complete current arrays and pending counts are consistent.
clear-queue-snapshot\tPopulate both queues then issue clear_queue\tResponse returns removed steering/followUp arrays and following state shows both empty.
queue-cancel-and-restore\tAbort active work with both queues populated then start next prompt\tCancellation and queue retention/clearing match upstream semantics.
tool-finishes-before-next-request\tQueue a steer and follow-up behind a barrier-controlled tool call\tTool result is recorded before the next provider request consumes queued input.
mode-change-persistence\tChange both queue modes and restart/reload the same session\tState and subsequent delivery mode match the upstream persistence contract.
""",
"R5": """
agent-start-end-settled\tRun one successful provider response\tOne agent_start precedes agent_end and one agent_settled follows all automatic work.
turn-start-end\tRun one assistant turn with deterministic response\tturn_start precedes turn_end and carries final assistant message and toolResults.
message-role-lifecycle\tCapture user, assistant and tool-result records in a tool turn\tmessage_start/message_end payload roles and final messages match pinned JSON schema.
streaming-text-fragments\tUse deterministic provider text split into known fragments\tmessage_update text_start/text_delta/text_end order and deltas match exactly.
streaming-reasoning-fragments\tUse deterministic provider reasoning with known fragments\tThinking contentIndex and fragments are preserved and not normalized away.
cumulative-usage-and-final-message\tStream updates with cumulative usage followed by final message\tUsage fields and authoritative message_end payload match upstream values.
retry-event-order\tTrigger one deterministic provider auto-retry then success\tauto_retry_start/end and agent_end/settlement ordering, fields and attempts match Pi.
post-content-failure\tEmit text before deterministic provider failure\tPartial assistant message and error/done lifecycle match upstream without losing content.
cancellation-lifecycle\tCancel during stream after one fragment\tCancellation yields exact required message_end, turn_end, agent_end and agent_settled sequence.
session-and-model-event-order\tRename session and change thinking/model during active idle boundary\tsession_info_changed/thinking_level_changed precede correlated responses at the pinned order.
session-entry-event-payload\tEmit a custom entry from deterministic extension code and capture entry_appended\tentry_appended contains exact persisted entry payload and ordering with subsequent session reads matches Pi.
""",
"R6": """
tool-call-start-arguments\tProvider streams one tool call with fixed id and JSON args\ttoolcall_start exposes correct contentIndex, id, toolName and arguments.
tool-argument-fragments\tSplit serialized tool arguments across deterministic deltas\tEach toolcall_delta fragment and ordering are preserved exactly.
tool-call-end-authoritative\tComplete streamed tool call with canonical final argument object\ttoolcall_end carries exact complete ToolCall and supersedes fragments.
tool-execution-start-update-end\tRun blocked deterministic tool emitting one partial result\tStart/update/end share toolCallId/name and contain exact args/partial/final result/isError fields.
sequential-tool-continuation\tRequest two sequential tool calls using local provider fixture\tEach tool result precedes the next provider request and call history matches Pi.
parallel-tool-continuation\tRequest parallel tools with deterministic barriers\tExecution overlap is allowed and externally observed event/call ordering matches pinned Pi.
tool-error-continuation\tRun a tool that returns an error result then provider continues\tisError/result and subsequent provider history match Pi.
provider-failure-after-tool\tComplete a tool then fail next provider request\tNo tool call/result is duplicated and failure lifecycle matches pinned behavior.
partial-assistant-with-tool\tStream assistant text and tool-call fragments in one response\tContent indices, mixed block order, arguments and final message match exactly.
tool-call-id-and-parent-fields\tExecute nested tool invocation and capture correlated ids\ttoolCallId and optional parentToolCallId presence/value match session event schema.
""",
"R7": """
get-state-idle-schema\tCall get_state on a fresh session\tAll documented model/thinking/streaming/compacting/queue/session/name/count fields have exact types and absent/null behavior.
get-state-active-stream\tCall get_state while a provider stream and queued input are blocked\tisStreaming, pendingMessageCount and session identity reflect the same process snapshot.
get-messages-system-projection\tCall get_messages in a session with generated system sections\tSystem message content and sections match pinned Pi's observable projection.
get-messages-compacted-context-edits\tLoad session containing compaction summary and context edits then query\tRetained messages and compactionSummary role match Pi's session.messages semantics.
get-messages-active-partial\tQuery messages during an active assistant stream\tOnly upstream-visible completed messages are returned and partial content remains in lifecycle events where required.
get-entries-full-and-leaf\tCall get_entries without since in linear and branched sessions\tEntries and leafId match persisted selected tree exactly.
get-entries-cursor-boundaries\tCall get_entries with valid, first, last, missing and malformed since cursor values\tCursor inclusivity/errors and returned leafId match pinned source.
get-tree-branches-and-labels\tCall get_tree over a branched session with labels\tParent relationships, selected child, labels and leafId match Pi.
get-fork-messages-content\tCall get_fork_messages from a selected branch\tEntry IDs and user-facing text content match upstream semantics.
get-last-assistant-text-values\tQuery empty session, completed text, tool-only and branched assistant history\tReturned text/null choice matches the active branch and pinned projection.
session-read-during-provider-work\tIssue all supported read commands while provider is blocked\tReads are responsive and use one coherent cloned tree snapshot without stale or partial persisted state.
session-read-after-reload\tRestart process on native and Pi JSONL session fixtures then issue read commands\tIDs, branch selection, entries, messages and counts persist and project correctly.
read-command-error-cursors\tRequest invalid cursor/session state then issue valid read\tOne error is returned and process remains usable with no unrelated state mutation.
""",
"R8": """
native-summary-generation\tTrigger native branch summary using deterministic fake summarizer with billed usage\tget_session_stats includes summary input/output/cache/cost values exactly once.
native-summary-persisted-reload\tPersist generated native summary, restart and call get_session_stats\tReloaded statistics include the same summary usage exactly once.
imported-pi-summary\tLoad pinned Pi JSONL branch_summary with piOriginalEntry and usage\tImported summary is counted according to Pi-compatible usage exactly once.
mixed-native-imported-summaries\tCombine native and imported summary entries with distinct usage values\tRPC total equals each distinct source contribution once with no omission or double count.
cost-and-token-fields\tPopulate input/output/cacheRead/cacheWrite tokens and every cost component\tRPC statistics match SessionStatistics token and cost totals field by field.
empty-and-zero-usage\tQuery empty session and summaries with explicit zero or absent usage\tMissing/zero values follow upstream nullable/default behavior without fabricated cost.
""",
"R9": """
available-model-schema-order\tQuery get_available_models with deterministic configured models and scopes\tModel objects, provider identity, metadata and order match pinned upstream.
set-model-exact-and-invalid\tSet exact provider/modelId then request unavailable identity\tSuccess selects exact model and invalid request returns pinned failure without unintended selection.
cycle-model-order-and-null\tCycle across configured models and empty model catalog\tOrder, scoped result, thinking level and null response behavior match Pi.
scoped-thinking-suffix\tSet scoped model reference with explicit :thinking suffix\tScope selection and requested thinking level match upstream precedence.
per-model-thinking-defaults\tSelect models with distinct configured thinking defaults\tSelection uses each model default and emits the correct event.
supported-and-unsupported-levels\tSet supported, unsupported and boundary thinking levels\tAccepted levels, clamping/error and available-level response match pinned Pi.
sparse-thinking-level-map\tUse sparse per-model thinking map and query/cycle values\tRPC values and persistence match Pi's sparse-map behavior.
busy-model-change\tChange and cycle model during blocked tool execution\tCommand succeeds at the same boundary and next physical provider request uses the new model.
thinking-event-response-order\tSet model and thinking level with correlated ids\tthinking_level_changed and command responses occur in exact documented order.
model-persistence-restart\tChange scoped model/thinking state, restart and query state\tPersisted scope and level survive reload and next provider request uses them.
invalid-model-no-mutation\tAttempt unavailable model while a run is blocked\tError fields match and active selection/provider history remain unchanged.
catalog-boundary\tRun all model RPC commands with local deterministic catalog only\tNo credential/authentication or global full-catalog behavior is silently claimed by this RPC milestone.
""",
"R10": """
manual-compact-success\tIssue compact with deterministic history and customInstructions\tcompact response, summary entry, usage and compaction events match pinned Pi.
manual-compact-failure-rollback\tMake deterministic summarizer fail during manual compact\tFailure response and history rollback/recovery match without corrupting session.
manual-compact-abort\tAbort manual compact while summarizer is blocked\tCancellation response/event order and retained context match Pi.
auto-compaction-setting\tToggle set_auto_compaction true/false and query get_state\tResponse shape and observable state update match upstream.
threshold-and-overflow-compaction\tTrigger automatic threshold and overflow compaction using deterministic limits\tReason, cut boundary, summary, retry and provider context match Pi.
auto-retry-events\tSet auto retry and return retryable local HTTP failure then success\tRetry count, delay, event fields and final settlement match pinned behavior.
abort-retry\tAbort during retry delay and query state\tRetry is cancelled once, emits required end/settlement, and command returns exact success shape.
compaction-and-retry-restart\tPersist compacted session and retry-related settings then restart\tSession context and persisted setting behavior match Pi without a second pipeline.
summarization-retry-events\tForce deterministic branch-summary and compaction summarizer retries\tsummarization_retry_scheduled, attempt_start source/reason and finished fields/order match pinned Pi.
""",
"R11": """
bash-success-result\tRun Bash command producing stdout, stderr and exit code\tResponse data fields and output truncation match pinned BashResult.
bash-stream-id-and-chunks\tRun long Bash output with fixed command id and multiple output chunks\tbash_execution_update chunks preserve order and repeat exactly the originating optional id.
bash-without-command-id\tRun Bash without id while capturing updates\tUpdate records omit id rather than null and do not acquire unrelated command IDs.
bash-exclude-from-context\tRun bash with excludeFromContext true then inspect session history\tOutput is returned but omitted from model context exactly as pinned.
bash-failure-semantics\tRun nonzero and launch-failing commands\tError/result/exit-code response and process survival match Pi.
abort-active-bash\tStart barrier-controlled Bash and issue abort_bash\tOne cancellation is observed, response is exact, and no late output corrupts framing.
bash-during-agent-run\tRun direct Bash while provider work is active\tCommand acceptance, event interleave and active run state match Pi.
shutdown-active-bash\tClose stdin while Bash is blocked\tProcess exits cleanly, terminates child work and emits no duplicate or partial JSON record.
""",
"R12": """
new-session-empty\tIssue new_session without parentSession\tReplacement identity, cancelled field and lifecycle events match pinned Pi.
new-session-parent-session\tIssue new_session with a valid parentSession and non-string variants\tParent session acceptance and truthy/wrong-type validation exactly match pinned runtime.
switch-native-session\tSwitch to valid native PiSharp JSONL fixture\tLoaded selection, identity, events and future ownership match Pi.
switch-pi-jsonl-session\tSwitch to valid Pi JSONL session with branch and metadata\tImporter selection and subsequent history match pinned Pi behavior.
switch-missing-or-malformed-target\tSwitch to missing and malformed targets while old session is active\tFailure leaves old working session usable and no invalid destination writes occur.
cross-project-rebind-trust\tSwitch across project directories with extension/resources fixture\tWorking directory, project trust and resource rebinding match pinned source.
fork-entry-and-branch-point\tFork at entryId on non-leaf branch with following entries\tFork point, selected branch and fork response text match upstream.
clone-current-branch\tClone a branched session with selected leaf\tClone copies selected history and response/event order match Pi.
replace-during-provider-stream\tSwitch/fork/clone while local provider is blocked mid-stream\tOld run settles before response, replacement is adopted, and no late writes target prior session.
replace-during-active-tool\tFork/clone while a non-idempotent Bash tool is blocked\tTool cancellation is settled and uncertain side effect is not replayed.
aborted-turn-settlement-order\tReplace session during active turn after partial assistant output\tagent_end/agent_settled and replacement response order match pinned contract.
no-stale-session-writes\tRelease blocked old run after replacement and inspect both files\tOnly correct session file changes and abandoned work cannot overwrite destination.
session-file-ownership\tCreate, switch, fork and clone across session directories\tEach file belongs to the selected session and project path without shared writer races.
subsequent-provider-history\tPrompt replacement session after each lifecycle operation\tPhysical local provider request contains exactly replacement's selected history.
failed-replacement-recovery\tCause replacement preflight failure then prompt old session\tOld session still accepts prompt and persists it to its original file.
""",
"R13": """
set-session-name-and-clear\tSet a session name and then clear it using empty name\tResponse, persisted name and optional/null projection match Pi.
session-info-event-order\tChange session name with correlated id\tsession_info_changed payload precedes or follows response exactly as pinned.
export-default-path\tExport a nonempty active session without outputPath\tResponse path and written HTML content match pinned naming and content.
export-explicit-path\tExport with an explicit outputPath while agent run is active\tResponse and file ownership/path behavior match upstream without stale snapshots.
export-errors\tExport to invalid/unwritable path and empty session\tError response and process state match Pi.
get-commands-builtins\tQuery prompt and skill command declarations\tName, description, source and sourceInfo schema/order match upstream.
get-commands-extension\tLoad extension command and query while active session runs\tExtension command metadata is present exactly once with source information.
commands-after-reload\tRestart with configured extension/prompt/skill commands and query\tDiscovery and source metadata persist and no stale resource appears.
""",
"R14": """
ui-select-roundtrip\tInvoke select dialog with fixed options, respond by matching request id\tRequest schema, value result, timeout field and continuation match Pi.
ui-confirm-roundtrip\tInvoke confirm dialog and answer true and false in separate runs\tconfirmed result and boolean semantics match Pi.
ui-input-roundtrip\tInvoke input dialog with and without placeholder then return value\tOptional placeholder and value response are projected precisely.
ui-editor-roundtrip\tInvoke editor dialog with prefill and submit edited text\tPrefill and response value match pinned behavior.
ui-notify-fire-and-forget\tInvoke notify with info, warning and error variants\tEach request is emitted once with correct optional notifyType and does not await response.
ui-set-status\tSet and clear extension status text\tstatusKey/statusText presence and fire-and-forget behavior match Pi.
ui-set-widget\tSet, relocate and clear string-array widget\twidgetLines and placement fields match; unsupported component factories are handled as documented.
ui-set-title\tSet window title from RPC extension\tOne setTitle request contains exact text and no response is expected.
ui-set-editor-text\tSet editor contents while RPC client has active provider stream\tOne set_editor_text request preserves exact text and doesn't interfere with commands.
ui-unique-concurrent-ids\tOpen select and input dialogs concurrently\tEvery request has unique id and each pending promise is matched to only its own response.
ui-out-of-order-responses\tRespond to two pending dialogs in reverse order\tResults resolve to correct original call independent of arrival order.
ui-timeout-default\tAllow dialog timeout to expire without client response\tPinned default resolution occurs once and later provider work continues.
ui-cancel-late-unknown\tCancel a dialog then send late and unknown ids\tLate/unknown response handling matches Pi with no misrouted resolution or process failure.
ui-unload-and-shutdown\tUnload extension and then close stdin while dialog is pending\tPending interaction settles/cancels and process exits without abandoned task.
ui-unsupported-methods\tInvoke custom, terminal-input and degraded RPC UI methods\tDocumented undefined/no-op/degraded behavior is preserved and reported accurately.
extension-handler-error-event\tThrow from a deterministic extension event handler during an RPC prompt\textension_error contains extensionPath, event and error and does not corrupt later command records.
ui-isolated-from-command-correlation\tKeep provider streaming while resolving UI request and issuing correlated command\tUI IDs never consume, alter or duplicate normal command response IDs.
""",
"R15": """
normal-eof\tSend valid request then close stdin while idle\tRuntime disposes and process exits with no hang or malformed output.
explicit-process-termination\tTerminate RPC child during idle and active phases\tExit status, partial output and pending settlement match supported shutdown behavior.
cancel-provider-turn-on-shutdown\tClose stdin during blocked provider turn\tCancellation reaches provider/runtime, pending command settles and no task is abandoned.
pending-command-shutdown\tIssue several commands including long-running compact then close stdin\tEach accepted request settles or is explicitly terminated per Pi with no duplicate response.
pending-extension-dialog-shutdown\tClose stdin with active extension UI dialog\tDialog cancellation and process disposal complete within bounded deadline.
active-tool-cancellation\tClose stdin while non-idempotent tool blocks\tTool process is cancelled/settled without automatic replay or stale session write.
partial-input-shutdown\tClose stdin midway through a JSON record\tIncomplete bytes follow pinned final-record behavior and process exits cleanly.
output-backpressure\tStop reading stdout while producing large event stream then resume\tBackpressure is honored without deadlock, loss, duplication or malformed JSONL.
broken-pipe-handling\tClose stdout reader and stderr reader independently during output\tProcess handles broken pipes predictably and disposes child/runtime resources.
rapid-command-burst\tSend 100 deterministic read/config requests rapidly with unique IDs\tEvery accepted request receives one correctly correlated response and state remains valid.
multiple-pending-shutdown\tHold provider, Bash and UI response simultaneously then initiate shutdown\tAll owned operations settle/cancel with no hangs, orphan tasks or duplicate responses.
process-disposal-repeat\tRun repeated start/request/EOF cycles with isolated directories\tHandles, ports and session leases are released after every process.
partial-write-record-integrity\tForce tiny output buffer during long Unicode events\tEvery emitted record is complete UTF-8 JSON followed by LF or is handled as process termination.
""",
"D0": """
calibrate-transport\tRun identical framing corpus twice on pinned Pi process\tPi stdout/stderr and exit observations are stable after only declared nondeterministic normalization.
calibrate-command-responses\tRun every deterministic command smoke scenario twice on pinned Pi\tResponse schemas, ids, error cases and ordering are stable across runs.
calibrate-prompt-events\tRun prompt/stream/tool lifecycle scenarios twice on pinned Pi\tEvent order, fragments, tool arguments and settlement are stable.
calibrate-queue-state\tRun queue and busy-prompt scenarios twice on pinned Pi\tQueue snapshots, dispositions and next-request history are stable.
calibrate-read-projections\tRun state/read commands on fixed native and Pi JSONL fixtures twice\tMessage/tree/entry/state/stats projections are stable.
calibrate-model-thinking\tRun model/thinking commands with deterministic catalog twice\tSelected model, thinking maps and event/response order are stable.
calibrate-session-lifecycle\tRun new/switch/fork/clone scenarios from identical fixture roots twice\tPersistence, branch selection and provider history are stable.
calibrate-extension-ui\tRun all dialog and fire-and-forget UI methods twice with fixed responses\tUI request schemas, IDs, values and event order are stable.
paired-independent-observations\tExecute complete mandatory Pi/PiSharp process corpus at exact pins\tEvery raw side is distinct and independently captured; no dispatcher mock substitutes for process execution.
""",
"Q0": """
dotnet-format\tRun dotnet format PiSharp.slnx --verify-no-changes\tFormatting verification succeeds with no modifications.
dotnet-build-warnings-as-errors\tRun dotnet build PiSharp.slnx --warnaserror\tFull solution builds with zero warnings and zero errors.
full-dotnet-suite\tRun dotnet test PiSharp.slnx\tAll tests pass with zero failures and zero unexplained skips.
all-rpc-process-tests\tRun all tests under Cli/Protocols/Rpc\tAll RPC process, projection, event and UI tests pass.
focused-runtime-and-session\tRun focused session persistence, replacement, model and statistics tests\tAll affected session/runtime/model/statistics regressions pass without skips.
focused-compaction-tools-extensions\tRun focused compaction, Bash/tool, retry and extension UI tests\tAll focused capability gates pass without skips.
json-event-regressions\tRun JSON protocol and event streaming suites\tStreaming fragments, errors, tool events and event serialization regressions pass.
linux-process-and-pty\tRun required Linux process and PTY coverage in CI-compatible environment\tProcess shutdown, pipes, sessions and terminal integration pass without unexplained skips.
python-parity-suites\tRun all required Python parity suite commands from task and repository guide\tAll required Python suites pass with recorded command/count and no unclassified failure.
node-parity-suites\tRun all required Node parity suite commands from task and repository guide\tAll required Node suites pass with recorded command/count and no unclassified failure.
mcp-conformance\tBuild and run MCP conformance suite\tNo new failures relative to explicitly classified baseline and all mandatory cases pass.
pinned-pi-rpc-suites\tRun relevant pinned Pi tests from tracked sources and record fullName for every pending test\tAll 200 credential-free tests pass; exactly the 20 inventory-listed test identities are skipped for credentials, with zero failures and no skipped case counted as passing.
new-rpc-differential-and-controls\tRun every frozen R and D case and all V1 negative controls\tAll mandatory cases pass and verifier rejects every corrupted evidence mutation.
docs-link-validation\tRun repository documentation path/reference validator\tAll documents and references pass with exact validation totals recorded.
prior-milestone-regression-audit\tRun preserved session, coding tools, terminal, multimodal, package and compaction gates\tNo prior verified capability regresses; managed-package corpus branch skip is explicitly recorded.
""",
"E0": """
inventory-source-pin\tRebuild/check inventory from clean tracked pinned Pi source files\tInventory command/event/schema counts and every upstream file hash match the frozen Pi SHA.
contract-case-index\tRecompute required case IDs and counts from contract\tEvery checkpoint count derives from unique immutable IDs and the separate index is identical.
implementation-source-identity\tCompare implementation source SHA, final head and source/test/tool diff\tTested implementation source is an ancestor and production/tests/parity tooling are unchanged afterward.
raw-capture-manifest\tValidate each case references stdin/stdout/stderr/exit/state captures as applicable\tAll required raw artifacts exist and their manifest paths/hashes are valid.
artifact-hash-integrity\tRehash all committed evidence artifacts and manifests\tEvery artifact digest matches its source-bound manifest.
normalization-register\tInspect all normalizers and compare before/after observations\tOnly allowed temporary roots/generated IDs/timestamps are normalized; protected semantic fields remain exact.
safety-difference-register\tReview each claimed intentional safety difference and linked regression\tEvery safety difference has precise rationale, raw evidence and dedicated regression; unresolved mismatch count is zero.
test-reference-integrity\tResolve every regression test path and named test in reports\tEvery claimed test exists and is tied to covered case IDs.
quality-summary-identity\tRecompute suite totals from raw quality reports\tQuality summary matches exact source SHA and all required suite evidence.
repo-ledger-updates\tInspect feature matrix, execution ledger, continuation and TODO updates\tAll four required inventories report actual status, source pin, merge SHA and limitations consistently.
""",
"CI0": """
push-workflow-exact-head\tRead automatic push workflow for final branch head\tAll mandatory push jobs succeeded on exact implementation/evidence head.
pull-request-workflow-exact-head\tRead automatic PR workflow for final PR head\tAll mandatory PR jobs succeeded on the exact PR head, no earlier green run substituted.
workflow-base-and-merge-base\tCompare PR base SHA with latest main containing PR #28 merge\tPR base is current and includes the actual prerequisite merge.
job-completeness\tCompare required workflow jobs against CI contract\tEvery mandatory job ran successfully and no required job was skipped or cancelled.
no-duplicate-manual-dispatch\tReview workflow event records and publication timeline\tCI evidence is automatic and no redundant manual dispatch is treated as acceptance.
""",
"PUB0": """
draft-pr-open-unmerged\tInspect actual assigned PR metadata after push\tNew PR targets latest main, is open and draft, and remains unmerged.
pr-description-evidence\tRead PR description at final head\tDescription includes verified scope, actual SHAs, checkpoint/case totals, CI runs and limitations.
publication-verifier-current-evidence\tRun publication verifier against explicit current evidence directory\tFull verifier passes current artifact and source identities with no stale evidence.
e0-ci0-pub0-independent\tRun E0, CI0 and PUB0 finalizers independently after all prior gates\tEach finalizer passes independently against current exact head and base.
single-contributor-review-state\tInspect repo policy and PR review state\tPR is ready for independent review and not self-approved; no merge is performed for PR #29.
final-source-and-pin-report\tRe-read actual remote/main/branch/head and pinned source SHA\tFinal report uses observed PR number, branch, merge SHA, implementation SHA, head SHA and exact verification counts.
""",
}

CHECKPOINTS = [
    ("P0", "PR #28 review, exact-head CI and verified merge", ["GitHub PR #28 metadata, commit ancestry, push run 40747257587, PR run 40747270521, PR #28 source-bound acceptance evidence", "docs/parity/fixtures/compaction-context-management-parity-2026-10-28.md" if False else "PR #28 published description and committed evidence"]),
    ("P1", "Actual merge ancestry, new branch and pinned sources", ["git merge/base/ancestry for 646db89bd396b8089b1b9a058fa96b1489f4d0cd", "tools/parity/pi-reference.txt", "src/PiSharp.Runtime/PiSharp.Runtime.csproj"]),
    ("V0", "Exhaustive RPC command, event and schema inventory", ["pinned Pi rpc-types.ts, rpc-mode.ts, jsonl.ts, agent/types.ts and agent-session.ts", "pinned Pi rpc.md, rpc-commands.md, json.md, rpc-extension-ui.md, message-types.md", "docs/parity/fixtures/rpc-protocol-command-parity/inventory.json"]),
    ("V1", "Positive controls and verifier negative controls", ["tools/parity/rpc_protocol/verify.py", "tools/parity/rpc_protocol/contract.json", "docs/parity/fixtures/rpc-protocol-command-parity/required-case-index.json"]),
    ("R1", "JSONL framing and transport", ["Pi packages/coding-agent/docs/rpc.md#Framing", "Pi packages/coding-agent/src/modes/rpc/jsonl.ts", "PiSharp src/PiSharp.Cli/Protocols/Rpc/RpcMode.cs", "PiSharp src/PiSharp.Cli/Protocols/Json/JsonLineWriter.cs"]),
    ("R2", "Request IDs, response shapes and validation", ["Pi packages/coding-agent/src/modes/rpc/rpc-types.ts#RpcCommand/RpcResponse", "Pi packages/coding-agent/src/modes/rpc/rpc-mode.ts", "Pi packages/coding-agent/docs/rpc.md#Errors"]),
    ("R3", "Prompt acceptance and asynchronous lifecycle", ["Pi packages/coding-agent/docs/rpc-commands.md#prompt", "Pi packages/coding-agent/src/modes/rpc/rpc-mode.ts", "Pi packages/coding-agent/src/core/agent-session.ts" ]),
    ("R4", "Steering, follow-up and queue semantics", ["Pi packages/coding-agent/docs/rpc-commands.md#steer/#follow_up/#clear_queue", "Pi packages/coding-agent/src/core/agent-session.ts", "PiSharp RpcPromptCommandHandler.cs and RpcQueueModeCommandHandler.cs"]),
    ("R5", "Agent, turn and message events", ["Pi packages/coding-agent/docs/json.md#Agent and turn events/#Message events", "Pi packages/agent/src/types.ts#AgentEvent", "Pi packages/coding-agent/src/core/agent-session.ts#AgentSessionEvent"]),
    ("R6", "Tool-call events and continuation", ["Pi packages/coding-agent/docs/json.md#Tool execution events", "Pi packages/agent/src/types.ts#AssistantMessageEvent", "Pi packages/agent/src/agent.ts"]),
    ("R7", "State and session read commands", ["Pi packages/coding-agent/docs/rpc-commands.md#get_state/#get_messages/#get_entries/#get_tree/#get_fork_messages/#get_last_assistant_text", "Pi packages/coding-agent/src/core/agent-session.ts", "Pi packages/coding-agent/src/core/session-manager.ts"]),
    ("R8", "Session statistics, usage and cost reporting", ["Pi packages/coding-agent/docs/rpc-commands.md#get_session_stats", "Pi packages/coding-agent/src/core/agent-session.ts#SessionStats", "PiSharp RpcSessionStatsProjector.cs and ConversationSession.cs"]),
    ("R9", "Model selection and thinking commands", ["Pi packages/coding-agent/docs/rpc-commands.md#set_model/#cycle_model/#thinking", "Pi packages/coding-agent/src/modes/rpc/rpc-types.ts", "PiSharp RpcModelCommandHandler.cs and ModelRuntimeController.cs"]),
    ("R10", "Compaction and retry commands", ["Pi packages/coding-agent/docs/rpc-commands.md#compact/#set_auto_compaction/#set_auto_retry/#abort_retry", "Pi packages/coding-agent/src/core/agent-session.ts", "PiSharp RpcCompactionCommandHandler.cs and RpcRetryCommandHandler.cs"]),
    ("R11", "Bash execution and cancellation", ["Pi packages/coding-agent/docs/rpc-commands.md#bash/#abort_bash", "Pi packages/coding-agent/src/core/bash-executor.ts", "PiSharp RpcMode.cs and BashExecutor.cs"]),
    ("R12", "Session creation, switching, fork and clone", ["Pi packages/coding-agent/docs/rpc-commands.md#new_session/#switch_session/#fork/#clone", "Pi packages/coding-agent/src/core/session-manager.ts", "PiSharp RpcSessionCommandHandler.cs and session store"]),
    ("R13", "Session naming, export and command discovery", ["Pi packages/coding-agent/docs/rpc-commands.md#export_html/#set_session_name/#get_commands", "Pi packages/coding-agent/src/modes/rpc/rpc-types.ts", "PiSharp RpcCommandDiscoveryHandler.cs and RpcSessionCommandHandler.cs"]),
    ("R14", "RPC extension UI", ["Pi packages/coding-agent/docs/rpc-extension-ui.md", "Pi packages/coding-agent/src/modes/rpc/rpc-mode.ts#extensionUi", "Pi packages/coding-agent/src/modes/rpc/rpc-types.ts#RpcExtensionUIRequest/RpcExtensionUIResponse"]),
    ("R15", "Failure, cancellation, concurrency and shutdown", ["Pi packages/coding-agent/docs/rpc.md#Framing/#Shutdown/#Errors", "Pi packages/coding-agent/src/modes/rpc/rpc-mode.ts", "Pi packages/coding-agent/src/modes/rpc/rpc-client.ts"]),
    ("D0", "Independent pinned-Pi differential acceptance", ["Pinned Pi process at b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6", "PiSharp CLI process at implementation source SHA", "all R1-R15 raw process artifacts"]),
    ("Q0", "Full quality and regression verification", ["Repository AGENTS.md build/test commands", ".github/workflows/ci.yml", "task section 8 quality and regression gates"]),
    ("E0", "Source-bound committed evidence validation", ["tools/parity/rpc_protocol/contract.json", "tools/parity/rpc_protocol/verify.py", "docs/parity/fixtures/rpc-protocol-command-parity/"]),
    ("CI0", "Exact-head automatic CI verification", ["GitHub push and pull_request workflow metadata for final exact head", ".github/workflows/ci.yml"]),
    ("PUB0", "Draft PR publication and final acceptance", ["GitHub PR for milestone/rpc-protocol-command-parity", "final source-bound evidence receipt", "task sections 10-12"]),
]

EXPECTED = {
    "P0": "Every prerequisite assertion is proven from actual GitHub/commit/evidence state; the actual merge SHA is never inferred.",
    "P1": "The new branch and source pins are the verified prerequisite base and exact requested Pi revision.",
    "V0": "The inventory covers every pinned command, event, projection, response/error schema, extension UI method and process operation.",
    "V1": "A positive control passes and every frozen negative control is rejected by the executable verifier.",
    "R1": "PiSharp and pinned Pi produce equivalent framing, encoding, recovery and stream-separation observations.",
    "R2": "Each request receives exactly one correct response with exact optional-field and correlation semantics.",
    "R3": "Prompt acceptance is distinct from run completion and lifecycle observations match Pi at process boundary.",
    "R4": "Queue contents, modes, precedence, delivery and cancellation match pinned Pi.",
    "R5": "All agent/turn/message lifecycle and streaming event payloads/order match pinned Pi.",
    "R6": "Tool-call fragments, execution events, arguments, results and continuation match pinned Pi.",
    "R7": "State and session projections match pinned Pi on idle, active, branched, compacted and reloaded sessions.",
    "R8": "Native, imported and mixed summary usage and all token/cost totals match Pi exactly once.",
    "R9": "Model/thinking RPC results, events, persistence and next provider requests match the deterministic Pi oracle.",
    "R10": "Compaction/retry behavior remains on the existing application pipeline and matches Pi process observations.",
    "R11": "Bash response, streaming, cancellation and shutdown behavior match Pi without duplicate execution.",
    "R12": "Session replacement/fork/clone preserves correct ownership, branch history and active-run settlement.",
    "R13": "Names, exports and discovered commands have the exact Pi RPC schemas and persistence behavior.",
    "R14": "The bidirectional UI subprotocol matches method schema, ID routing, concurrency and cancellation semantics.",
    "R15": "Process cleanup and concurrent protocol operation produce no hangs, lost/duplicate replies or invalid JSONL.",
    "D0": "Every required process pair passes independently captured same-input differential and repeated Pi calibration.",
    "Q0": "Every named quality/regression gate succeeds with no failures or unexplained skips.",
    "E0": "Committed reports, raw artifacts and hashes are reproducible and bound to the implementation actually tested.",
    "CI0": "Automatic push and PR CI both pass on exact final head and current base, with every required job present.",
    "PUB0": "The actual new PR is open, draft and unmerged, and its final publication verifier succeeds.",
}

PASS = "Every required case ID occurs exactly once with executed=true, passed=true and skipped=false; all raw captures exist and verify; source identities match; all assertions meet the case's comparison rule. A missing, duplicate, failed, skipped, stale or unresolved case fails the checkpoint."

NORMALIZATIONS = {
    "R7.get-state-idle-schema": ["Map only the generated sessionId token consistently within each process capture; compare its type and all references; replace the isolated temporary session root in sessionFile."],
    "R8.native-summary-generation": ["Map generated sessionId and isolated temporary session root consistently; preserve all usage and cost values exactly."],
    "R8.native-summary-persisted-reload": ["Map the sessionId from the same fixture consistently; preserve summary entries and usage/cost values exactly."],
    "R8.imported-pi-summary": ["Replace only the temporary fixture root path; preserve imported entry contents and usage/cost values exactly."],
    "R8.mixed-native-imported-summaries": ["Map the generated sessionId consistently; preserve all individual and aggregate usage/cost values exactly."],
    "R8.cost-and-token-fields": ["Map the generated sessionId consistently; preserve all individual and aggregate usage/cost values exactly."],
    "R12.new-session-empty": ["Map generated session IDs consistently across response, state and persisted session header; replace only the temporary root path."],
    "R12.new-session-parent-session": ["Map generated session IDs consistently across response, state and persisted session header; replace only the temporary root path."],
    "R12.switch-native-session": ["Replace only temporary fixture root paths; preserve session identity, selected branch and content."],
    "R12.switch-pi-jsonl-session": ["Replace only temporary fixture root paths; preserve imported session IDs and selected branch."],
    "R12.cross-project-rebind-trust": ["Replace explicitly isolated temporary project root paths; preserve trust values and resource ordering."],
    "R12.fork-entry-and-branch-point": ["Map generated destination session ID consistently and replace only temporary destination root; preserve source entry IDs and branch links."],
    "R12.clone-current-branch": ["Map generated destination session ID consistently and replace only temporary destination root; preserve selected branch and source history."],
    "R12.replace-during-provider-stream": ["Map generated replacement session ID consistently across state and session header; preserve event order and provider history."],
    "R12.replace-during-active-tool": ["Map generated replacement session ID consistently across state and session header; preserve tool side-effect count and event order."],
    "R12.session-file-ownership": ["Replace only distinct temporary root prefixes while preserving relative file ownership and content hashes."],
    "R12.subsequent-provider-history": ["Map generated destination session ID consistently; preserve all provider request messages and tool call history exactly."],
    "R12.failed-replacement-recovery": ["Replace only the old session's isolated temporary root path; preserve old session ID and all history."],
    "R13.export-default-path": ["Replace only temporary output directory prefixes; preserve filename semantics and exported HTML content."],
    "R13.export-explicit-path": ["Replace only isolated temporary explicit path prefixes; preserve file bytes and response path shape."],
    "D0.calibrate-session-lifecycle": ["Map generated session IDs consistently, replace isolated temporary roots, and exclude timestamps only; preserve branch links and history."],
    "D0.calibrate-read-projections": ["Replace temporary roots and map generated IDs only where the same identity is consistently referenced; preserve messages, tree and usage."],
}


def main() -> None:
    inventory = json.loads(INVENTORY_PATH.read_text(encoding="utf-8"))
    if inventory["sourcePins"]["piCommit"] != PI_SHA or inventory["sourcePins"]["pisharpBaseCommit"] != BASE_SHA:
        raise SystemExit("inventory source pins do not match the prerequisite baseline")
    checkpoints = []
    flat_cases = []
    for checkpoint_id, title, sources in CHECKPOINTS:
        rows = [line for line in CASES[checkpoint_id].strip().splitlines() if line.strip()]
        cases = []
        for row in rows:
            slug, inputs, expected = row.split("\t", 2)
            case_id = f"{checkpoint_id}.{slug}"
            case = {
                "id": case_id,
                "inputs": inputs,
                "expectedObservation": expected,
                "comparisonRules": {
                    "mode": "strict",
                    "normalizations": [],
                    "protected": ["request IDs", "event order", "field presence", "response type", "tool arguments", "queue state", "session contents", "usage and cost"],
                },
                "sourceReferences": sources,
                "runner": f"python3 tools/parity/rpc_protocol/runner.py run --case-id {case_id} --pi-checkout artifacts/parity/pi-ownership-oracle --evidence-dir <current-run-directory>",
                "evidencePath": f"cases/{case_id}/",
                "passFailCriteria": "The deterministic scenario is executed against two stable pinned-Pi process runs and an independently started PiSharp CLI process using the same inputs; complete raw stdin/stdout/stderr/exit/state artifacts are retained; the expected observable condition is asserted; semantic fields listed above are never normalized.",
            }
            cases.append(case)
            flat_cases.append(case_id)
        if not cases:
            raise SystemExit(f"checkpoint {checkpoint_id} has no required cases")
        checkpoints.append({
            "id": checkpoint_id,
            "name": title,
            "requiredCaseIds": [case["id"] for case in cases],
            "runner": f"python3 tools/parity/rpc_protocol/runner.py run --checkpoint {checkpoint_id} --pi-checkout artifacts/parity/pi-ownership-oracle --evidence-dir <current-run-directory>",
            "expectedObservations": EXPECTED[checkpoint_id],
            "passFailCriteria": PASS,
            "evidencePath": f"checkpoints/{checkpoint_id}.json",
            "sourceReferences": sources,
        })
    case_ids = flat_cases
    if len(case_ids) != len(set(case_ids)):
        raise SystemExit("duplicate case ID in frozen declarations")
    available_case_ids = set(case_ids)
    inventory_references = []
    for item in inventory["commandInventory"] + inventory["eventInventory"] + inventory["extensionUiMethods"] + inventory["protocolOperations"]:
        inventory_references.extend(item.get("mandatoryVerificationCaseIds", []))
    inventory_references.extend(inventory["stateProjection"].get("mandatoryVerificationCaseIds", []))
    missing_case_refs = sorted(set(inventory_references) - available_case_ids)
    if missing_case_refs:
        raise SystemExit(f"inventory refers to case IDs absent from the frozen contract: {missing_case_refs}")
    if not all(item.get("mandatoryVerificationCaseIds") for item in inventory["commandInventory"]):
        raise SystemExit("one or more RPC commands have no linked mandatory cases")
    if not all(item.get("mandatoryVerificationCaseIds") for item in inventory["eventInventory"]):
        raise SystemExit("one or more RPC event families have no linked mandatory cases")
    if not all(item.get("mandatoryVerificationCaseIds") for item in inventory["extensionUiMethods"]):
        raise SystemExit("one or more RPC extension UI methods have no linked mandatory cases")
    contract = {
        "schemaVersion": 1,
        "milestone": "rpc-protocol-command-parity",
        "status": "frozen-before-production-implementation",
        "sourcePins": {
            "piRepository": "https://github.com/earendil-works/pi",
            "piCommit": PI_SHA,
            "pisharpBaseCommit": BASE_SHA,
            "branch": BRANCH,
            "prerequisitePullRequest": 28,
            "prerequisiteActualMergeCommit": BASE_SHA,
            "implementationSourceCommit": None,
            "finalPrHeadCommit": None,
        },
        "inventory": {
            "path": "docs/parity/fixtures/rpc-protocol-command-parity/inventory.json",
            "sha256": hashlib.sha256(INVENTORY_PATH.read_bytes()).hexdigest(),
            "commands": inventory["counts"]["commands"],
            "eventFamilies": inventory["counts"]["eventFamilies"],
            "extensionUiMethods": inventory["counts"]["extensionUiMethods"],
            "credentialGatedUpstreamCases": inventory["counts"]["credentialGatedUpstreamCases"],
        },
        "checkpointCount": len(checkpoints),
        "requiredCaseCount": len(case_ids),
        "requiredCheckpointIds": [item[0] for item in CHECKPOINTS],
        "requiredCaseIds": case_ids,
        "caseIdentityRule": "The required denominator is the unique case ID list generated from this contract before production implementation. Results cannot add, remove, rename, skip or redefine a case.",
        "rawCaptureRequirements": [
            "Pi and PiSharp stdin bytes, stdout bytes, stderr bytes and exit status",
            "provider request/response bytes or canonical JSON from deterministic loopback fixtures",
            "timestamped ordered protocol records and state snapshots",
            "persisted session artifacts for state/lifecycle cases",
            "raw artifact SHA-256 manifest and exact source identities",
        ],
        "comparisonPolicy": {
            "default": "strict JSON structural comparison with event order preserved",
            "allowedNormalization": ["temporary root paths", "generated IDs only where the case explicitly maps identity consistently", "timestamps only where not semantically observable"],
            "neverNormalize": ["request correlation", "event order", "response type", "success/error fields", "message roles", "tool-call arguments", "stream fragments", "queue state", "compaction boundaries", "usage/cost", "session contents", "provider request history"],
        },
        "checkpoints": checkpoints,
        "cases": [case for checkpoint_id, _, _ in CHECKPOINTS for case in [
            {
                "id": f"{checkpoint_id}.{row.split(chr(9), 1)[0]}",
                "inputs": row.split(chr(9), 2)[1],
                "expectedObservation": row.split(chr(9), 2)[2],
            }
            for row in [line for line in CASES[checkpoint_id].strip().splitlines() if line.strip()]
        ]],
    }
    # The compact case list above is the immutable identity source. The rich
    # runner/source details live in each checkpoint's caseDefinitions field.
    definitions: dict[str, list[dict[str, object]]] = {}
    for checkpoint_id, _, _ in CHECKPOINTS:
        rows = [line for line in CASES[checkpoint_id].strip().splitlines() if line.strip()]
        defs = []
        checkpoint = next(cp for cp in checkpoints if cp["id"] == checkpoint_id)
        for row in rows:
            slug, inputs, expected = row.split("\t", 2)
            case_id = f"{checkpoint_id}.{slug}"
            defs.append({
                "id": case_id,
                "inputs": inputs,
                "expectedObservation": expected,
                "comparisonRules": {"mode": "equivalent" if NORMALIZATIONS.get(case_id) else "strict", "normalizations": NORMALIZATIONS.get(case_id, []), "protected": contract["comparisonPolicy"]["neverNormalize"]},
                "sourceReferences": checkpoint["sourceReferences"],
                "runner": f"python3 tools/parity/rpc_protocol/runner.py run --case-id {case_id} --pi-checkout artifacts/parity/pi-ownership-oracle --evidence-dir <current-run-directory>",
                "evidencePath": f"cases/{case_id}/",
                "passFailCriteria": "The deterministic scenario runs against two stable pinned-Pi process runs and a separately started PiSharp CLI process with identical inputs; full process captures are hash-verified; the expected observation is asserted; protected semantic fields are never normalized.",
            })
        definitions[checkpoint_id] = defs
    for checkpoint in contract["checkpoints"]:
        checkpoint["caseDefinitions"] = definitions[checkpoint["id"]]
    contract["cases"] = [case for checkpoint_id, _, _ in CHECKPOINTS for case in definitions[checkpoint_id]]
    CONTRACT_PATH.write_text(json.dumps(contract, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    index = {
        "schemaVersion": 1,
        "milestone": contract["milestone"],
        "contractSha256": hashlib.sha256(CONTRACT_PATH.read_bytes()).hexdigest(),
        "checkpointIds": contract["requiredCheckpointIds"],
        "caseCount": len(case_ids),
        "caseIds": case_ids,
    }
    INDEX_PATH.write_text(json.dumps(index, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"frozen {len(checkpoints)} checkpoints / {len(case_ids)} case IDs; contract sha256 {index['contractSha256']}")


if __name__ == "__main__":
    main()
