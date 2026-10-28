#!/usr/bin/env python3
"""Build the source-pinned RPC capability inventory from the reviewed Pi tree.

This reads only the named, tracked source files from the pinned checkout. It does
not use the checkout's worktree file list as an inventory, so unrelated local
files cannot silently enter the result.
"""
from __future__ import annotations

import json
import re
import subprocess
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[3]
PI = ROOT / "artifacts/parity/pi-ownership-oracle"
OUTPUT = ROOT / "docs/parity/fixtures/rpc-protocol-command-parity/inventory.json"
PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"
BASE_SHA = "646db89bd396b8089b1b9a058fa96b1489f4d0cd"

COMMANDS = [
    "prompt", "steer", "follow_up", "abort", "clear_queue", "new_session",
    "get_state", "set_model", "cycle_model",
    "get_available_models", "set_thinking_level", "cycle_thinking_level",
    "get_available_thinking_levels", "set_steering_mode", "set_follow_up_mode",
    "compact", "set_auto_compaction", "set_auto_retry", "abort_retry", "bash",
    "abort_bash", "get_session_stats", "export_html", "switch_session", "fork",
    "clone", "get_fork_messages", "get_entries", "get_tree",
    "get_last_assistant_text", "set_session_name", "get_messages", "get_commands",
]

EVENT_SOURCES = {
    "agent_start": "agent", "agent_end": "session", "turn_start": "agent",
    "turn_end": "agent", "message_start": "agent", "message_update": "agent",
    "message_end": "agent", "tool_execution_start": "agent",
    "tool_execution_update": "agent", "tool_execution_end": "agent",
    "agent_settled": "session", "queue_update": "session",
    "compaction_start": "session", "entry_appended": "session",
    "session_info_changed": "session", "thinking_level_changed": "session",
    "compaction_end": "session", "auto_retry_start": "session",
    "auto_retry_end": "session", "summarization_retry_scheduled": "session",
    "summarization_retry_attempt_start": "session",
    "summarization_retry_finished": "session", "bash_execution_update": "session",
    "extension_error": "rpc",
}

OWNERS = {
    "prompt": ["src/PiSharp.Cli/Protocols/Rpc/RpcMode.cs", "src/PiSharp.Cli/Protocols/Rpc/RpcPromptCommandHandler.cs"],
    "steer": ["src/PiSharp.Cli/Protocols/Rpc/RpcMode.cs", "src/PiSharp.Cli/Protocols/Rpc/RpcPromptCommandHandler.cs"],
    "follow_up": ["src/PiSharp.Cli/Protocols/Rpc/RpcMode.cs", "src/PiSharp.Cli/Protocols/Rpc/RpcPromptCommandHandler.cs"],
    "abort": ["src/PiSharp.Cli/Protocols/Rpc/RpcMode.cs", "src/PiSharp.Cli/Protocols/Rpc/RpcPromptCommandHandler.cs"],
    "clear_queue": ["src/PiSharp.Cli/Protocols/Rpc/RpcMode.cs", "src/PiSharp.Cli/Protocols/Rpc/RpcPromptCommandHandler.cs"],
    "new_session": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionCommandHandler.cs"],
    "get_state": ["src/PiSharp.Cli/Protocols/Rpc/RpcStateCommandHandler.cs"],
    "get_messages": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionCommandHandler.cs", "src/PiSharp.Cli/Protocols/Rpc/RpcSessionMessageProjector.cs"],
    "set_model": ["src/PiSharp.Cli/Protocols/Rpc/RpcModelCommandHandler.cs"],
    "cycle_model": ["src/PiSharp.Cli/Protocols/Rpc/RpcModelCommandHandler.cs"],
    "get_available_models": ["src/PiSharp.Cli/Protocols/Rpc/RpcModelCommandHandler.cs", "src/PiSharp.Cli/Protocols/Rpc/RpcModelProjector.cs"],
    "set_thinking_level": ["src/PiSharp.Cli/Protocols/Rpc/RpcModelCommandHandler.cs"],
    "cycle_thinking_level": ["src/PiSharp.Cli/Protocols/Rpc/RpcModelCommandHandler.cs"],
    "get_available_thinking_levels": ["src/PiSharp.Cli/Protocols/Rpc/RpcModelCommandHandler.cs"],
    "set_steering_mode": ["src/PiSharp.Cli/Protocols/Rpc/RpcQueueModeCommandHandler.cs"],
    "set_follow_up_mode": ["src/PiSharp.Cli/Protocols/Rpc/RpcQueueModeCommandHandler.cs"],
    "compact": ["src/PiSharp.Cli/Protocols/Rpc/RpcCompactionCommandHandler.cs"],
    "set_auto_compaction": ["src/PiSharp.Cli/Protocols/Rpc/RpcCompactionCommandHandler.cs"],
    "set_auto_retry": ["src/PiSharp.Cli/Protocols/Rpc/RpcRetryCommandHandler.cs"],
    "abort_retry": ["src/PiSharp.Cli/Protocols/Rpc/RpcRetryCommandHandler.cs"],
    "bash": ["src/PiSharp.Cli/Protocols/Rpc/RpcMode.cs"],
    "abort_bash": ["src/PiSharp.Cli/Protocols/Rpc/RpcMode.cs"],
    "get_session_stats": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionStatsProjector.cs"],
    "export_html": ["src/PiSharp.Cli/Protocols/Rpc/RpcMode.cs"],
    "switch_session": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionCommandHandler.cs"],
    "fork": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionCommandHandler.cs"],
    "clone": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionCommandHandler.cs"],
    "get_fork_messages": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionCommandHandler.cs"],
    "get_entries": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionCommandHandler.cs", "src/PiSharp.Cli/Protocols/Rpc/RpcSessionEntryProjector.cs"],
    "get_tree": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionCommandHandler.cs", "src/PiSharp.Cli/Protocols/Rpc/RpcSessionEntryProjector.cs"],
    "get_last_assistant_text": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionCommandHandler.cs"],
    "set_session_name": ["src/PiSharp.Cli/Protocols/Rpc/RpcSessionCommandHandler.cs"],
    "get_commands": ["src/PiSharp.Cli/Protocols/Rpc/RpcCommandDiscoveryHandler.cs"],
}

TESTS = {
    "prompt": ["RpcPromptLifecycleTests.cs", "RpcAgentLifecycleProcessTests.cs", "RpcSettlementBarrierTests.cs"],
    "steer": ["RpcIdleQueueProcessTests.cs", "RpcModeTests.cs"],
    "follow_up": ["RpcIdleQueueProcessTests.cs", "RpcModeTests.cs"],
    "abort": ["RpcAgentLifecycleProcessTests.cs", "RpcSettlementBarrierTests.cs", "RpcModeTests.cs"],
    "clear_queue": ["RpcIdleQueueProcessTests.cs", "RpcModeTests.cs"],
    "new_session": ["RpcSessionProcessTests.cs", "RpcSessionReplacementProcessTests.cs"],
    "get_state": ["RpcStateTests.cs", "RpcModeTests.cs"],
    "get_messages": ["RpcReadCommandsProcessTests.cs", "RpcSessionMessageProjectorTests.cs"],
    "set_model": ["RpcModelProcessTests.cs", "RpcActiveModelSelectionProcessTests.cs"],
    "cycle_model": ["RpcModelProcessTests.cs"],
    "get_available_models": ["RpcModelProcessTests.cs"],
    "set_thinking_level": ["RpcThinkingLevelMapProcessTests.cs", "RpcModelProcessTests.cs"],
    "cycle_thinking_level": ["RpcModelProcessTests.cs"],
    "get_available_thinking_levels": ["RpcThinkingLevelMapProcessTests.cs"],
    "set_steering_mode": ["RpcIdleQueueProcessTests.cs", "RpcModeTests.cs"],
    "set_follow_up_mode": ["RpcIdleQueueProcessTests.cs", "RpcModeTests.cs"],
    "compact": ["RpcCompactionProcessTests.cs"],
    "set_auto_compaction": ["RpcCompactionProcessTests.cs", "RpcModeTests.cs"],
    "set_auto_retry": ["RpcRetryProcessTests.cs"],
    "abort_retry": ["RpcRetryProcessTests.cs"],
    "bash": ["RpcBashProcessTests.cs", "BashShutdownProcessTests.cs"],
    "abort_bash": ["RpcBashProcessTests.cs", "BashShutdownProcessTests.cs"],
    "get_session_stats": ["RpcSessionStatsProjectorTests.cs", "RpcReadCommandsProcessTests.cs"],
    "export_html": ["RpcExportHtmlProcessTests.cs"],
    "switch_session": ["RpcSessionProcessTests.cs", "RpcSessionReplacementProcessTests.cs", "RpcMissingSessionTargetProcessTests.cs"],
    "fork": ["RpcSessionProcessTests.cs", "RpcActiveToolForkProcessTests.cs", "RpcSessionReplacementProcessTests.cs"],
    "clone": ["RpcSessionProcessTests.cs", "RpcActiveCloneProcessTests.cs", "RpcClonePreflightProcessTests.cs"],
    "get_fork_messages": ["RpcReadCommandsProcessTests.cs"],
    "get_entries": ["RpcReadCommandsProcessTests.cs", "RpcSessionEntryProjectorTests.cs"],
    "get_tree": ["RpcReadCommandsProcessTests.cs", "RpcSessionEntryProjectorTests.cs"],
    "get_last_assistant_text": ["RpcReadCommandsProcessTests.cs"],
    "set_session_name": ["RpcSessionProcessTests.cs"],
    "get_commands": ["RpcCommandDiscoveryProcessTests.cs", "RpcExtensionCommandProcessTests.cs"],
}

FIXTURES = {
    "abort": ["docs/parity/fixtures/rpc-abort-baseline.md"],
    "clone": ["docs/parity/fixtures/rpc-clone-baseline.md"],
    "compact": ["docs/parity/fixtures/rpc-compact-baseline.md"],
    "cycle_model": ["docs/parity/fixtures/rpc-cycle-model-baseline.md"],
    "export_html": ["docs/parity/fixtures/rpc-export-html-active-baseline.md"],
    "get_commands": ["docs/parity/fixtures/rpc-get-commands-baseline.md"],
    "get_messages": ["docs/parity/fixtures/rpc-get-messages-system-baseline.md"],
    "get_session_stats": ["docs/parity/fixtures/rpc-get-session-stats-baseline.md", "docs/parity/fixtures/session-rpc-projection-b06-2026-10-24.json"],
    "new_session": ["docs/parity/fixtures/rpc-new-session-baseline.md"],
    "set_model": ["docs/parity/fixtures/rpc-scoped-model-thinking-baseline.md"],
    "set_thinking_level": ["docs/parity/fixtures/rpc-thinking-level-map-baseline.md"],
    "switch_session": ["docs/parity/fixtures/rpc-switch-abort-baseline.md"],
    "tool_execution": ["docs/parity/fixtures/rpc-toolcall-delta-baseline.md"],
}

COMMAND_CHECKPOINT = {
    "prompt": "R3", "steer": "R4", "follow_up": "R4", "abort": "R3",
    "clear_queue": "R4", "new_session": "R12", "get_state": "R7",
    "get_messages": "R7", "set_model": "R9", "cycle_model": "R9",
    "get_available_models": "R9", "set_thinking_level": "R9",
    "cycle_thinking_level": "R9", "get_available_thinking_levels": "R9",
    "set_steering_mode": "R4", "set_follow_up_mode": "R4", "compact": "R10",
    "set_auto_compaction": "R10", "set_auto_retry": "R10", "abort_retry": "R10",
    "bash": "R11", "abort_bash": "R11", "get_session_stats": "R8",
    "export_html": "R13", "switch_session": "R12", "fork": "R12", "clone": "R12",
    "get_fork_messages": "R7", "get_entries": "R7", "get_tree": "R7",
    "get_last_assistant_text": "R7", "set_session_name": "R13", "get_commands": "R13",
}

COMMAND_CASES = {
    "prompt": ["R3.idle-prompt-started", "R3.prompt-handled-without-run", "R3.busy-prompt-default-behavior", "R3.acceptance-before-completion", "R3.provider-failure-after-acceptance"],
    "steer": ["R4.steer-command-idle", "R4.steering-all-mode", "R4.steering-one-at-a-time-mode", "R4.steer-followup-precedence", "R4.multiple-concurrent-queue-updates"],
    "follow_up": ["R4.followup-command-idle", "R4.followup-all-mode", "R4.followup-one-at-a-time-mode", "R4.steer-followup-precedence", "R4.multiple-concurrent-queue-updates"],
    "abort": ["R3.abort-prompt-run", "R4.queue-cancel-and-restore", "R15.cancel-provider-turn-on-shutdown"],
    "clear_queue": ["R4.clear-queue-snapshot", "R4.multiple-concurrent-queue-updates"],
    "new_session": ["R12.new-session-empty", "R12.new-session-parent-session", "R12.session-file-ownership"],
    "get_state": ["R7.get-state-idle-schema", "R7.get-state-active-stream"],
    "get_messages": ["R7.get-messages-system-projection", "R7.get-messages-compacted-context-edits", "R7.get-messages-active-partial", "R7.session-read-after-reload"],
    "set_model": ["R9.set-model-exact-and-invalid", "R9.scoped-thinking-suffix", "R9.busy-model-change", "R9.model-persistence-restart"],
    "cycle_model": ["R9.cycle-model-order-and-null", "R9.busy-model-change"],
    "get_available_models": ["R9.available-model-schema-order", "R9.catalog-boundary"],
    "set_thinking_level": ["R9.supported-and-unsupported-levels", "R9.sparse-thinking-level-map", "R9.thinking-event-response-order"],
    "cycle_thinking_level": ["R9.supported-and-unsupported-levels", "R9.sparse-thinking-level-map"],
    "get_available_thinking_levels": ["R9.supported-and-unsupported-levels", "R9.sparse-thinking-level-map"],
    "set_steering_mode": ["R4.steering-all-mode", "R4.steering-one-at-a-time-mode", "R4.mode-change-persistence"],
    "set_follow_up_mode": ["R4.followup-all-mode", "R4.followup-one-at-a-time-mode", "R4.mode-change-persistence"],
    "compact": ["R10.manual-compact-success", "R10.manual-compact-failure-rollback", "R10.manual-compact-abort", "R10.threshold-and-overflow-compaction"],
    "set_auto_compaction": ["R10.auto-compaction-setting", "R10.threshold-and-overflow-compaction", "R10.compaction-and-retry-restart"],
    "set_auto_retry": ["R10.auto-retry-events", "R10.compaction-and-retry-restart"],
    "abort_retry": ["R10.abort-retry", "R10.auto-retry-events"],
    "bash": ["R11.bash-success-result", "R11.bash-stream-id-and-chunks", "R11.bash-without-command-id", "R11.bash-failure-semantics", "R11.bash-during-agent-run"],
    "abort_bash": ["R11.abort-active-bash", "R11.shutdown-active-bash", "R15.active-tool-cancellation"],
    "get_session_stats": ["R8.native-summary-generation", "R8.native-summary-persisted-reload", "R8.imported-pi-summary", "R8.mixed-native-imported-summaries", "R8.cost-and-token-fields", "R8.empty-and-zero-usage"],
    "export_html": ["R13.export-default-path", "R13.export-explicit-path", "R13.export-errors"],
    "switch_session": ["R12.switch-native-session", "R12.switch-pi-jsonl-session", "R12.switch-missing-or-malformed-target", "R12.replace-during-provider-stream", "R12.replace-during-active-tool", "R12.failed-replacement-recovery"],
    "fork": ["R12.fork-entry-and-branch-point", "R12.replace-during-provider-stream", "R12.replace-during-active-tool", "R12.subsequent-provider-history"],
    "clone": ["R12.clone-current-branch", "R12.replace-during-provider-stream", "R12.replace-during-active-tool", "R12.subsequent-provider-history"],
    "get_fork_messages": ["R7.get-fork-messages-content", "R7.get-tree-branches-and-labels"],
    "get_entries": ["R7.get-entries-full-and-leaf", "R7.get-entries-cursor-boundaries", "R7.session-read-after-reload"],
    "get_tree": ["R7.get-tree-branches-and-labels", "R7.session-read-after-reload"],
    "get_last_assistant_text": ["R7.get-last-assistant-text-values", "R7.get-messages-active-partial"],
    "set_session_name": ["R13.set-session-name-and-clear", "R13.session-info-event-order", "R13.commands-after-reload"],
    "get_commands": ["R13.get-commands-builtins", "R13.get-commands-extension", "R13.commands-after-reload"],
}

EVENT_CASES = {
    "agent_start": ["R5.agent-start-end-settled"],
    "agent_end": ["R5.agent-start-end-settled", "R5.cancellation-lifecycle"],
    "agent_settled": ["R5.agent-start-end-settled", "R5.cancellation-lifecycle"],
    "turn_start": ["R5.turn-start-end"], "turn_end": ["R5.turn-start-end"],
    "message_start": ["R5.message-role-lifecycle"], "message_update": ["R5.streaming-text-fragments", "R5.streaming-reasoning-fragments", "R5.cumulative-usage-and-final-message"],
    "message_end": ["R5.message-role-lifecycle", "R5.post-content-failure", "R5.cancellation-lifecycle"],
    "tool_execution_start": ["R6.tool-execution-start-update-end"], "tool_execution_update": ["R6.tool-execution-start-update-end"], "tool_execution_end": ["R6.tool-execution-start-update-end", "R6.tool-error-continuation"],
    "queue_update": ["R4.multiple-concurrent-queue-updates", "R4.clear-queue-snapshot"],
    "compaction_start": ["R10.manual-compact-success", "R10.threshold-and-overflow-compaction"], "compaction_end": ["R10.manual-compact-success", "R10.manual-compact-failure-rollback"],
    "entry_appended": ["R5.session-entry-event-payload"], "session_info_changed": ["R5.session-and-model-event-order", "R13.session-info-event-order"],
    "thinking_level_changed": ["R5.session-and-model-event-order", "R9.thinking-event-response-order"],
    "auto_retry_start": ["R5.retry-event-order", "R10.auto-retry-events"], "auto_retry_end": ["R5.retry-event-order", "R10.auto-retry-events"],
    "summarization_retry_scheduled": ["R10.summarization-retry-events"], "summarization_retry_attempt_start": ["R10.summarization-retry-events"], "summarization_retry_finished": ["R10.summarization-retry-events"],
    "bash_execution_update": ["R11.bash-stream-id-and-chunks", "R11.bash-without-command-id"],
    "extension_error": ["R14.extension-handler-error-event"],
}

UI_CASES = {
    "select": ["R14.ui-select-roundtrip", "R14.ui-unique-concurrent-ids", "R14.ui-out-of-order-responses"],
    "confirm": ["R14.ui-confirm-roundtrip", "R14.ui-unique-concurrent-ids", "R14.ui-out-of-order-responses"],
    "input": ["R14.ui-input-roundtrip", "R14.ui-unique-concurrent-ids", "R14.ui-timeout-default"],
    "editor": ["R14.ui-editor-roundtrip", "R14.ui-unique-concurrent-ids", "R14.ui-cancel-late-unknown"],
    "notify": ["R14.ui-notify-fire-and-forget"], "setStatus": ["R14.ui-set-status"],
    "setWidget": ["R14.ui-set-widget"], "setTitle": ["R14.ui-set-title"],
    "set_editor_text": ["R14.ui-set-editor-text"],
}

PROTOCOL_CASES = {
    "parse_jsonl_command": ["R1.lf-delimited-record", "R1.utf8-split-codepoint", "R1.optional-crlf", "R1.unicode-separators-in-string", "R1.incremental-and-multiple-chunks", "R1.incomplete-record-completion", "R1.large-valid-record", "R1.malformed-json-recovery", "R1.empty-and-blank-input", "R1.eof-incomplete-final-record"],
    "correlate_command_response": ["R2.correlated-response-id", "R2.uncorrelated-response-omits-id", "R2.id-type-validation", "R2.overlapping-correlated-requests"],
    "emit_parse_error": ["R1.malformed-json-recovery", "R2.unknown-command-recovery", "R2.invalid-request-shapes", "R2.missing-required-properties"],
    "interleave_session_events": ["R2.overlapping-correlated-requests", "R3.acceptance-before-completion", "R5.agent-start-end-settled"],
    "resolve_extension_ui_dialog": ["R14.ui-select-roundtrip", "R14.ui-confirm-roundtrip", "R14.ui-input-roundtrip", "R14.ui-editor-roundtrip", "R14.ui-unique-concurrent-ids", "R14.ui-out-of-order-responses", "R14.ui-timeout-default", "R14.ui-cancel-late-unknown"],
    "orderly_eof_shutdown": ["R1.empty-and-blank-input", "R1.eof-incomplete-final-record", "R15.normal-eof", "R15.pending-command-shutdown", "R15.pending-extension-dialog-shutdown"],
    "stdout_stderr_separation": ["R1.stdout-stderr-purity", "R15.broken-pipe-handling"],
}

MISMATCHES_BY_COMMAND = {
    "get_messages": ["RPC-SYSMSG-SECTIONS"],
    "new_session": ["RPC-NEW-SESSION-PARENT-TYPE"],
    "get_session_stats": ["RPC-CONTEXT-USAGE-ESTIMATE"],
    "set_model": ["RPC-SCOPED-THINKING-SUFFIX"],
}

DOC_FILES = [
    "packages/coding-agent/docs/rpc.md",
    "packages/coding-agent/docs/rpc-commands.md",
    "packages/coding-agent/docs/json.md",
    "packages/coding-agent/docs/rpc-extension-ui.md",
    "packages/coding-agent/docs/message-types.md",
]
PI_FILES = [
    "packages/coding-agent/src/modes/rpc/rpc-types.ts",
    "packages/coding-agent/src/modes/rpc/rpc-mode.ts",
    "packages/coding-agent/src/modes/rpc/jsonl.ts",
    "packages/coding-agent/src/modes/rpc/rpc-client.ts",
    "packages/coding-agent/src/core/agent-session.ts",
    "packages/coding-agent/src/core/session-manager.ts",
    "packages/agent/src/types.ts",
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts",
    "packages/coding-agent/test/compaction-extensions.test.ts",
    "packages/coding-agent/test/compaction.test.ts",
]

CREDENTIAL_GATED_TESTS = [
    ("packages/coding-agent/test/agent-session-tree-navigation.test.ts", "AgentSession tree navigation e2e should navigate to user message and put text in editor"),
    ("packages/coding-agent/test/agent-session-tree-navigation.test.ts", "AgentSession tree navigation e2e should navigate to non-user message without editor text"),
    ("packages/coding-agent/test/agent-session-tree-navigation.test.ts", "AgentSession tree navigation e2e should create branch summary when navigating with summarize=true"),
    ("packages/coding-agent/test/agent-session-tree-navigation.test.ts", "AgentSession tree navigation e2e should attach summary to correct parent when navigating to nested user message"),
    ("packages/coding-agent/test/agent-session-tree-navigation.test.ts", "AgentSession tree navigation e2e should attach summary to selected node when navigating to assistant message"),
    ("packages/coding-agent/test/agent-session-tree-navigation.test.ts", "AgentSession tree navigation e2e should handle abort during summarization"),
    ("packages/coding-agent/test/agent-session-tree-navigation.test.ts", "AgentSession tree navigation e2e should not create summary when navigating without summarize option"),
    ("packages/coding-agent/test/agent-session-tree-navigation.test.ts", "AgentSession tree navigation e2e should handle navigation to same position (no-op)"),
    ("packages/coding-agent/test/agent-session-tree-navigation.test.ts", "AgentSession tree navigation e2e should support custom summarization instructions"),
    ("packages/coding-agent/test/agent-session-tree-navigation.test.ts", "AgentSession tree navigation - branch scenarios should navigate between branches correctly"),
    ("packages/coding-agent/test/compaction-extensions.test.ts", "Compaction extensions should emit before_compact and compact events"),
    ("packages/coding-agent/test/compaction-extensions.test.ts", "Compaction extensions should allow extensions to cancel compaction"),
    ("packages/coding-agent/test/compaction-extensions.test.ts", "Compaction extensions should allow extensions to provide custom compaction"),
    ("packages/coding-agent/test/compaction-extensions.test.ts", "Compaction extensions should include entries in compact event after compaction is saved"),
    ("packages/coding-agent/test/compaction-extensions.test.ts", "Compaction extensions should continue with default compaction if extension throws error"),
    ("packages/coding-agent/test/compaction-extensions.test.ts", "Compaction extensions should call multiple extensions in order"),
    ("packages/coding-agent/test/compaction-extensions.test.ts", "Compaction extensions should pass correct data in before_compact event"),
    ("packages/coding-agent/test/compaction-extensions.test.ts", "Compaction extensions should use extension compaction even with different values"),
    ("packages/coding-agent/test/compaction.test.ts", "LLM summarization should generate a compaction result for the large session"),
    ("packages/coding-agent/test/compaction.test.ts", "LLM summarization should produce valid session after compaction"),
]


def text_at(base: Path, relative: str) -> str:
    path = base / relative
    # Require source paths to be tracked; the ownership oracle has an unrelated
    # untracked test file and must not influence contract discovery.
    subprocess.run(["git", "-C", str(base), "ls-files", "--error-unmatch", relative],
                   check=True, capture_output=True, text=True)
    return path.read_text(encoding="utf-8")


def loc(text: str, needle: str) -> int:
    offset = text.find(needle)
    if offset < 0:
        return 0
    return text.count("\n", 0, offset) + 1


def blocks(text: str, marker: str, stop_marker: str | None) -> list[tuple[int, str]]:
    start = text.index(marker)
    end = text.index(stop_marker, start) if stop_marker else len(text)
    lines = text[start:end].splitlines()
    found: list[tuple[int, str]] = []
    current: list[str] = []
    current_line = 0
    brace_depth = 0
    for index, line in enumerate(lines):
        if re.match(r"\s*\|\s*\{", line):
            if current:
                found.append((current_line, "\n".join(current)))
            current = [line]
            current_line = text.count("\n", 0, text.index(marker)) + index + 1
            brace_depth = line.count("{") - line.count("}")
            if brace_depth == 0:
                found.append((current_line, "\n".join(current)))
                current = []
        elif current:
            current.append(line)
            brace_depth += line.count("{") - line.count("}")
            if brace_depth == 0:
                found.append((current_line, "\n".join(current)))
                current = []
                brace_depth = 0
    if current:
        found.append((current_line, "\n".join(current)))
    return found


def section(text: str, title: str, level: int = 3) -> tuple[int, str]:
    pattern = re.compile(rf"^(#{{{level}}})\s+{re.escape(title)}\s*$", re.M)
    match = pattern.search(text)
    if not match:
        return 0, ""
    tail = text[match.end():]
    end = re.search(rf"^#{{1,{level}}}\s+", tail, re.M)
    body = tail[:end.start()] if end else tail
    line = text.count("\n", 0, match.start()) + 1
    return line, body.strip()


def short_line(text: str, needle: str, relative: str) -> dict[str, Any]:
    line = loc(text, needle)
    return {"path": relative, "line": line}


def main() -> None:
    actual = subprocess.run(["git", "-C", str(PI), "rev-parse", "HEAD"],
                            check=True, capture_output=True, text=True).stdout.strip()
    if actual != PI_SHA:
        raise SystemExit(f"pinned Pi checkout mismatch: expected {PI_SHA}, got {actual}")
    rpc_types_path = "packages/coding-agent/src/modes/rpc/rpc-types.ts"
    rpc_types = text_at(PI, rpc_types_path)
    command_blocks = blocks(rpc_types, "export type RpcCommand =", "// RPC Slash Command")
    command_by_name: dict[str, tuple[int, str]] = {}
    for line, raw in command_blocks:
        match = re.search(r'type:\s*"([a-z_]+)"', raw)
        if match:
            command_by_name[match.group(1)] = (line, raw.strip())
    if list(command_by_name) != COMMANDS:
        raise SystemExit(f"command type list differs: {list(command_by_name)}")

    response_blocks = blocks(rpc_types, "export type RpcResponse =", "// ============================================================================\n// Extension UI Events")
    response_by_command: dict[str, tuple[int, str]] = {}
    for line, raw in response_blocks:
        match = re.search(r'command:\s*"([a-z_]+)"', raw)
        if match:
            response_by_command[match.group(1)] = (line, raw.strip())
    error_block = next((b for b in response_blocks if "success: false" in b[1]), None)
    if error_block is None or set(response_by_command) != set(COMMANDS):
        raise SystemExit("response definitions do not cover every command and a shared error response")

    commands_doc_path = "packages/coding-agent/docs/rpc-commands.md"
    commands_doc = text_at(PI, commands_doc_path)
    pi_mode_path = "packages/coding-agent/src/modes/rpc/rpc-mode.ts"
    pi_mode = text_at(PI, pi_mode_path)
    command_inventory = []
    for name in COMMANDS:
        req_line, req_schema = command_by_name[name]
        resp_line, resp_schema = response_by_command[name]
        doc_line, doc_body = section(commands_doc, name)
        if not doc_body:
            raise SystemExit(f"missing upstream command documentation for {name}")
        owners = [
            {
                "path": relative,
                "line": loc((ROOT / relative).read_text(encoding="utf-8"), f"class {Path(relative).stem}") or 1,
            }
            for relative in OWNERS[name]
        ]
        tests = []
        for test_name in TESTS[name]:
            relative = f"tests/PiSharp.Tests/Cli/Protocols/Rpc/{test_name}"
            test_text = (ROOT / relative).read_text(encoding="utf-8")
            tests.append({"path": relative, "line": loc(test_text, f"class {Path(relative).stem}") or 1})
        command_inventory.append({
            "name": name,
            "checkpoint": COMMAND_CHECKPOINT[name],
            "mandatoryVerificationCaseIds": COMMAND_CASES[name],
            "requestSchema": req_schema,
            "responseSchema": resp_schema,
            "documentedSemantics": doc_body,
            "upstreamSources": [
                {"path": rpc_types_path, "line": req_line, "symbol": "RpcCommand"},
                {"path": rpc_types_path, "line": resp_line, "symbol": "RpcResponse"},
                {"path": commands_doc_path, "line": doc_line, "symbol": f"{name} command contract"},
                short_line(pi_mode, f'case "{name}"', pi_mode_path),
            ],
            "pisharpOwners": owners,
            "existingTests": tests,
            "existingDifferentialFixtures": FIXTURES.get(name, []),
            "baselineAssessment": "Existing routes/tests are inventory evidence only; parity remains unverified until this milestone's process differential passes.",
            "priorEvidenceClassification": "partial-prior-fixture" if name in FIXTURES else "existing-route-and-test-only",
            "knownReviewRequirement": "" if name != "get_session_stats" else "Native branch-summary usage is counted exactly once, including persisted reload, imported summaries and mixed sessions.",
            "confirmedOrHistoricalMismatchIds": MISMATCHES_BY_COMMAND.get(name, []),
            "missingEvidence": ["fresh exact-pin paired Pi/PiSharp process capture", "same-Pi repeated calibration", "all mandatory case results for the assigned checkpoint"],
        })

    agent_path = "packages/agent/src/types.ts"
    session_path = "packages/coding-agent/src/core/agent-session.ts"
    agent_text = text_at(PI, agent_path)
    session_text = text_at(PI, session_path)
    json_path = "packages/coding-agent/docs/json.md"
    json_doc = text_at(PI, json_path)
    agent_blocks = blocks(agent_text, "export type AgentEvent =", None)
    session_blocks = blocks(session_text, "export type AgentSessionEvent =", "\n\t| { type: \"bash_execution_update\"")
    event_defs: dict[str, dict[str, Any]] = {}
    for line, raw in agent_blocks:
        match = re.search(r'type:\s*"([a-z_]+)"', raw)
        if match:
            name = match.group(1)
            event_defs[name] = {"name": name, "schemaVariants": [raw.strip()], "sources": [{"path": agent_path, "line": line, "symbol": "AgentEvent"}]}
    for line, raw in session_blocks:
        match = re.search(r'type:\s*"([a-z_]+)"', raw)
        if match:
            name = match.group(1)
            if name in event_defs:
                event_defs[name]["schemaVariants"].append(raw.strip())
            else:
                event_defs[name] = {"name": name, "schemaVariants": [raw.strip()], "sources": [{"path": session_path, "line": line, "symbol": "AgentSessionEvent"}]}
    # The last session event is intentionally given its concrete source schema;
    # it is the only event not captured by the generic union block boundary.
    bash_line = loc(session_text, '| { type: "bash_execution_update"; id?: string; delta: string }')
    event_defs["bash_execution_update"] = {
        "name": "bash_execution_update",
        "schemaVariants": ['| { type: "bash_execution_update"; id?: string; delta: string }'],
        "sources": [{"path": session_path, "line": bash_line, "symbol": "AgentSessionEvent"}],
    }
    error_line = loc(json_doc, '"type":"extension_error"')
    if not error_line:
        error_line = loc(json_doc, "extension_error")
    event_defs["extension_error"] = {
        "name": "extension_error",
        "schemaVariants": ['{ "type": "extension_error", "extensionPath": string, "event": string, "error": string }'],
        "sources": [{"path": json_path, "line": error_line, "symbol": "RPC-only JSON event"}, short_line(pi_mode, 'type: "extension_error"', pi_mode_path)],
    }
    if list(sorted(event_defs)) != list(sorted(EVENT_SOURCES)):
        raise SystemExit(f"event families differ: {sorted(event_defs)}")
    for name, event in event_defs.items():
        doc_line = loc(json_doc, name)
        event["documentation"] = {"path": json_path, "line": doc_line}
        event["mandatoryVerificationCaseIds"] = EVENT_CASES[name]
        event["inheritedSessionSchema"] = (
            "tool_execution_start/update/end add optional parentToolCallId when emitted for ctx.executeTool()."
            if name.startswith("tool_execution_") else
            "agent_end replaces the low-level AgentEvent with messages plus willRetry."
            if name == "agent_end" else
            "Inherited core AgentEvent fields are retained by AgentSessionEvent."
        )
        event["projectionOwner"] = "src/PiSharp.Cli/Protocols/Rpc/RpcEventProjector.cs"
        event["existingTests"] = [
            "tests/PiSharp.Tests/Cli/Protocols/Rpc/RpcEventProjectorTests.cs",
            "tests/PiSharp.Tests/Cli/Protocols/Rpc/RpcAgentLifecycleProcessTests.cs",
        ]
        event["assessment"] = "Source-defined event family; exact field and ordering parity is unverified until process-level cases pass."

    ui_path = rpc_types_path
    ui_start = rpc_types.index("export type RpcExtensionUIRequest =")
    ui_end = rpc_types.index("// Extension UI Commands", ui_start)
    ui_union = rpc_types[ui_start:ui_end]
    ui_methods = []
    for name in re.findall(r'method:\s*"([A-Za-z_]+)"', ui_union):
        if name not in [item["name"] for item in ui_methods]:
            line = loc(rpc_types, f'method: "{name}"')
            ui_methods.append({
                "name": name,
                "requestSchemaSource": next((raw.strip() for _, raw in blocks(rpc_types, "export type RpcExtensionUIRequest =", "// Extension UI Commands") if re.search(rf'method:\s*"{re.escape(name)}"', raw)), ""),
                "source": {"path": ui_path, "line": line, "symbol": "RpcExtensionUIRequest"},
                "mandatoryVerificationCaseIds": UI_CASES[name],
                "documentation": {"path": "packages/coding-agent/docs/rpc-extension-ui.md", "line": loc(text_at(PI, "packages/coding-agent/docs/rpc-extension-ui.md"), f"### {name}") or 1},
                "existingTests": ["tests/PiSharp.Tests/Cli/Protocols/Rpc/RpcExtensionUiTransportTests.cs", "tests/PiSharp.Tests/Cli/Protocols/Rpc/RpcExtensionContextProcessTests.cs"],
                "assessment": "Request/response behavior, timeout, cancellation and concurrency require new process differentials.",
            })
    if len(ui_methods) != 9:
        raise SystemExit(f"expected 9 RPC extension UI methods, found {len(ui_methods)}")

    state_match = re.search(r"export interface RpcSessionState \{([\s\S]*?)\n\}", rpc_types)
    if not state_match:
        raise SystemExit("missing RpcSessionState")
    state_fields = [line.strip() for line in state_match.group(1).splitlines() if line.strip() and not line.strip().startswith("//")]
    response_match = re.search(r"export type RpcExtensionUIResponse =([\s\S]*?);", rpc_types)
    request_types = re.search(r"export type RpcExtensionUIRequest =([\s\S]*?);", rpc_types)
    if not response_match or not request_types:
        raise SystemExit("extension UI request/response declarations not found")

    pisharp_owner_paths = sorted({path for paths in OWNERS.values() for path in paths} | {
        "src/PiSharp.Cli/Protocols/Rpc/RpcEventProjector.cs",
        "src/PiSharp.Cli/Protocols/Rpc/RpcEventWriter.cs",
        "src/PiSharp.Cli/Protocols/Rpc/RpcExtensionUiTransport.cs",
        "src/PiSharp.Cli/Protocols/Rpc/RpcSessionMessageProjector.cs",
        "src/PiSharp.Cli/Protocols/Rpc/RpcSessionEntryProjector.cs",
        "src/PiSharp.Cli/Protocols/Rpc/RpcSessionStatsProjector.cs",
        "src/PiSharp.Cli/Protocols/Json/JsonLineWriter.cs",
    })
    pisharp_owners = [
        {"path": relative, "line": loc((ROOT / relative).read_text(encoding="utf-8"), f"class {Path(relative).stem}") or 1}
        for relative in pisharp_owner_paths
    ]
    inventory = {
        "schemaVersion": 1,
        "status": "source-pinned-inventory-before-rpc-milestone-production-changes",
        "sourcePins": {
            "piRepository": "https://github.com/earendil-works/pi",
            "piCommit": PI_SHA,
            "pisharpBaseCommit": BASE_SHA,
            "sourceFileHashes": {},
        },
        "counts": {
            "commands": len(command_inventory),
            "eventFamilies": len(event_defs),
            "extensionUiMethods": len(ui_methods),
            "stateFields": len(state_fields),
            "responseSuccessVariants": len(response_by_command),
            "protocolRecordFamilies": 5,
            "protocolOperations": 7,
            "credentialGatedUpstreamCases": len(CREDENTIAL_GATED_TESTS),
        },
        "protocolRecordFamilies": [
            {"name": "rpc_command", "direction": "stdin", "schema": "RpcCommand", "source": {"path": rpc_types_path, "line": loc(rpc_types, "export type RpcCommand =")}, "framing": "one JSON object per LF-delimited record; optional preceding CR accepted"},
            {"name": "extension_ui_response", "direction": "stdin", "schema": response_match.group(0).strip(), "source": {"path": rpc_types_path, "line": loc(rpc_types, "export type RpcExtensionUIResponse =")}, "framing": "same JSONL stream, matched by request id"},
            {"name": "command_response", "direction": "stdout", "schema": "RpcResponse success variants and shared error variant", "source": {"path": rpc_types_path, "line": loc(rpc_types, "export type RpcResponse =")}, "framing": "one JSON object per LF-delimited record"},
            {"name": "session_event", "direction": "stdout", "schema": "JsonAgentSessionEvent and RPC-only extension_error", "source": {"path": "packages/coding-agent/docs/json.md", "line": 1}, "framing": "event records interleave with command responses"},
            {"name": "extension_ui_request", "direction": "stdout", "schema": request_types.group(0).strip(), "source": {"path": rpc_types_path, "line": loc(rpc_types, "export type RpcExtensionUIRequest =")}, "framing": "dialog request IDs are independent of command IDs"},
        ],
        "protocolOperations": [
            {"name": "parse_jsonl_command", "mandatoryVerificationCaseIds": PROTOCOL_CASES["parse_jsonl_command"], "source": {"path": "packages/coding-agent/src/modes/rpc/jsonl.ts", "line": loc(text_at(PI, "packages/coding-agent/src/modes/rpc/jsonl.ts"), "attachJsonlLineReader")}, "expected": "LF framing, optional CR stripping, UTF-8 incremental decode and incomplete final record delivery"},
            {"name": "correlate_command_response", "mandatoryVerificationCaseIds": PROTOCOL_CASES["correlate_command_response"], "source": {"path": "packages/coding-agent/src/modes/rpc/rpc-mode.ts", "line": loc(pi_mode, "const success =")}, "expected": "Repeat optional string id on command response; absent stays absent"},
            {"name": "emit_parse_error", "mandatoryVerificationCaseIds": PROTOCOL_CASES["emit_parse_error"], "source": {"path": "packages/coding-agent/src/modes/rpc/rpc-mode.ts", "line": loc(pi_mode, "Failed to parse command") or 1}, "expected": "Malformed JSON yields a failed parse response without id and process continues unless source proves otherwise"},
            {"name": "interleave_session_events", "mandatoryVerificationCaseIds": PROTOCOL_CASES["interleave_session_events"], "source": {"path": "packages/coding-agent/src/modes/rpc/rpc-mode.ts", "line": loc(pi_mode, "agent.subscribe") or 1}, "expected": "Session events may interleave with independent correlated command responses"},
            {"name": "resolve_extension_ui_dialog", "mandatoryVerificationCaseIds": PROTOCOL_CASES["resolve_extension_ui_dialog"], "source": {"path": rpc_types_path, "line": loc(rpc_types, "export type RpcExtensionUIResponse =")}, "expected": "Match response id to pending dialog; value, confirmed or cancelled shape"},
            {"name": "orderly_eof_shutdown", "mandatoryVerificationCaseIds": PROTOCOL_CASES["orderly_eof_shutdown"], "source": {"path": "packages/coding-agent/docs/rpc.md", "line": loc(text_at(PI, "packages/coding-agent/docs/rpc.md"), "## Shutdown")}, "expected": "Closing stdin requests orderly active runtime disposal and process exit"},
            {"name": "stdout_stderr_separation", "mandatoryVerificationCaseIds": PROTOCOL_CASES["stdout_stderr_separation"], "source": {"path": "packages/coding-agent/docs/rpc.md", "line": loc(text_at(PI, "packages/coding-agent/docs/rpc.md"), "Stdout is reserved")}, "expected": "stdout contains protocol records only; diagnostic output is on stderr"},
        ],
        "commandInventory": command_inventory,
        "eventInventory": [event_defs[name] for name in sorted(event_defs)],
        "extensionUiMethods": ui_methods,
        "stateProjection": {
            "name": "RpcSessionState",
            "fields": state_fields,
            "source": {"path": rpc_types_path, "line": loc(rpc_types, "export interface RpcSessionState")},
            "documentation": {"path": "packages/coding-agent/docs/rpc-commands.md", "line": loc(commands_doc, "### get_state")},
            "mandatoryVerificationCaseIds": COMMAND_CASES["get_state"],
            "knownProjectionQuestion": "System-message sections and active message fragments require explicit differential confirmation.",
        },
        "globalErrorAndCorrelationContract": {
            "successShapeSource": {"path": rpc_types_path, "line": loc(rpc_types, "export type RpcResponse =")},
            "errorResponseSchema": error_block[1].strip(),
            "parseErrorDocumentation": {"path": "packages/coding-agent/docs/rpc.md", "line": loc(text_at(PI, "packages/coding-agent/docs/rpc.md"), "Malformed JSON produces")},
            "unknownAndInvalidRequestSemantics": "Per-command upstream documentation and rpc-mode parser/dispatcher are included in the contract source set; exact process behavior is currently unverified.",
        },
        "pisharpSourceOwners": pisharp_owners,
        "knownCoverageLimitations": [
            "Existing route and test references establish baseline ownership only, not parity.",
            "Large RpcModeTests.cs is indexed as existing evidence but is not the default target for new tests.",
            "Prior RPC fixtures use earlier scoped Pi pins; the new acceptance contract requires fresh paired captures at the exact pinned SHA.",
            "Native branch-summary usage was fixed in PR #28 and is a mandatory exactly-once regression in R8.",
            "Pinned Pi checkout contains an unrelated untracked test file; source extraction reads only named tracked files above.",
        ],
        "credentialGatedUpstreamCases": [
            {
                "source": path,
                "testName": name,
                "classification": "credential-dependent interactive/LLM test; unverified, never counted as a pass",
                "evidence": "artifacts/parity/20261028T-review-fix-41e9b7e7/runs/Q0/pinned-pi-q0.json",
            }
            for path, name in CREDENTIAL_GATED_TESTS
        ],
        "mismatchRegister": [
            {
                "id": "RPC-SYSMSG-SECTIONS",
                "capability": "get_messages generated system message",
                "classification": "confirmed-observable-difference-in-existing-paired-fixture",
                "evidence": "docs/parity/fixtures/rpc-get-messages-system-baseline.md",
                "observation": "PiSharp exposes generated instructions in sections.preamble; pinned Pi distinguishes preamble, tools, rules, docs and cwd sections.",
                "requiredCheckpoint": "R7",
                "resolution": "Re-run exact pinned process comparison; correct the projection if still different and retain a dedicated process regression.",
            },
            {
                "id": "RPC-NEW-SESSION-PARENT-TYPE",
                "capability": "new_session.parentSession validation",
                "classification": "historical-observation-requires-exact-pin-revalidation",
                "evidence": "docs/parity/rpc-residual-audit-2026-09-28.md",
                "observation": "An earlier Pi revision accepted truthy non-string parentSession values while PiSharp enforced a string contract.",
                "requiredCheckpoint": "R12",
                "resolution": "Probe pinned b06c1f2 before deciding whether correction is required.",
            },
            {
                "id": "RPC-CONTEXT-USAGE-ESTIMATE",
                "capability": "get_session_stats.contextUsage",
                "classification": "known-approximation-in-existing-paired-fixture",
                "evidence": "docs/parity/fixtures/rpc-get-session-stats-baseline.md",
                "observation": "A previous paired fixture reported Pi contextUsage.tokens 1422 and PiSharp 817 for the same logical session; exact estimator parity was open.",
                "requiredCheckpoint": "R8",
                "resolution": "Repeat with equivalent persisted system state and determine whether an in-scope exact correction or explicitly verified safety difference is justified.",
            },
            {
                "id": "RPC-SCOPED-THINKING-SUFFIX",
                "capability": "set_model scoped :thinking selection",
                "classification": "previously-open-gap-requires-exact-pin-process-test",
                "evidence": "TODO.md RPC progress entry and docs/parity/fixtures/rpc-scoped-model-thinking-baseline.md",
                "observation": "Scoped model selection was compared, while explicit scoped :thinking suffix behavior remained open.",
                "requiredCheckpoint": "R9",
                "resolution": "Capture pinned Pi and PiSharp process behavior for explicit suffix and precedence cases.",
            },
            {
                "id": "RPC-PROVIDER-STREAMING-AND-CANCEL",
                "capability": "provider streaming, partial tool arguments and cancellation",
                "classification": "unverified-across-provider-families",
                "evidence": "docs/parity/rpc-residual-audit-2026-09-27.md",
                "observation": "Existing raw tool-call coverage is narrow; partial argument snapshots and broader cancellation/error differentials remain open.",
                "requiredCheckpoint": "R5/R6/R15",
                "resolution": "Use deterministic local providers for all mandatory stream/error/cancellation scenarios.",
            },
            {
                "id": "RPC-EXTENSION-UI-LIFECYCLE",
                "capability": "bidirectional RPC extension UI",
                "classification": "not-covered-by-prior-native-terminal-ui-verification",
                "evidence": "pinned Pi rpc-extension-ui.md and PiSharp RpcExtensionUiTransport.cs",
                "observation": "Terminal extension UI parity does not prove the separate RPC request/response subprotocol, concurrency or shutdown behavior.",
                "requiredCheckpoint": "R14",
                "resolution": "Verify all nine RPC UI methods, pending dialogs and cross-protocol correlation with real CLI processes.",
            },
            {
                "id": "RPC-ACTIVE-SESSION-REPLACEMENT",
                "capability": "switch/fork/clone during streaming and tool execution",
                "classification": "partial-prior-coverage-requires-full-matrix",
                "evidence": "docs/parity/fixtures/rpc-switch-abort-baseline.md and RPC active replacement process tests",
                "observation": "Selected active replacement flows have evidence, but the complete pinned-Pi matrix for active provider/tool work, stale writes and next provider history is required.",
                "requiredCheckpoint": "R12/R15",
                "resolution": "Run all declared provider, tool, persistence and recovery cases against pinned Pi.",
            },
        ],
    }
    files = set(DOC_FILES + PI_FILES)
    for relative in sorted(files):
        content = text_at(PI, relative).encode("utf-8")
        import hashlib
        inventory["sourcePins"]["sourceFileHashes"][relative] = hashlib.sha256(content).hexdigest()
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(inventory, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {OUTPUT.relative_to(ROOT)}: {len(command_inventory)} commands, {len(event_defs)} event families, {len(ui_methods)} extension UI methods")


if __name__ == "__main__":
    main()
