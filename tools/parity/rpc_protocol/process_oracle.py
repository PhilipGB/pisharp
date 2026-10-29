#!/usr/bin/env python3
"""Capture paired pinned-Pi/PiSharp JSONL process observations for RPC cases."""
from __future__ import annotations

from dataclasses import dataclass, replace
import base64
import hashlib
import html
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import signal
import shutil
import select
import socket
import subprocess
import tempfile
import threading
import time
import uuid
from typing import Any
from html.parser import HTMLParser

from verify import VerificationError

ROOT = Path(__file__).resolve().parents[3]
PI_SOURCE_RESOLVER = "packages/coding-agent/src/experimental/source-resolver.ts"
PI_CLI = "packages/coding-agent/src/cli.ts"
PISHARP_DLL = ROOT / "src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll"
TEST_EXTENSION_PROJECT = ROOT / "tests/PiSharp.Tests/PiSharp.Tests.csproj"
TEST_EXTENSION_DLL = ROOT / "tests/PiSharp.Tests/bin/Debug/net10.0/PiSharp.Tests.dll"
NATIVE_ENTRY_EXTENSION_PROJECT = ROOT / "tests/fixtures/NativeExtensionFixture/NativeExtensionFixture.csproj"
NATIVE_ENTRY_EXTENSION_DLL = ROOT / "tests/fixtures/NativeExtensionFixture/bin/Debug/net10.0/NativeExtensionFixture.dll"
RPC_PARITY_EXTENSION_PROJECT = ROOT / "tests/fixtures/RpcParityExtensionFixture/RpcParityExtensionFixture.csproj"
RPC_PARITY_EXTENSION_DLL = ROOT / "tests/fixtures/RpcParityExtensionFixture/bin/Debug/net10.0/RpcParityExtensionFixture.dll"
VOLATILE_FIELDS = {"sessionFile", "sessionId", "timestamp", "durationMs"}


class _AbsentValue:
    def __repr__(self) -> str:
        return "<absent>"


_ABSENT = _AbsentValue()


@dataclass(frozen=True)
class FixtureProviderStep:
    delay_seconds: float = 0.0
    text: str = "fixture answer"
    status_code: int = 200
    error_message: str | None = None
    tool_name: str | None = None
    tool_arguments: str | None = None
    tool_calls: tuple[tuple[str, str], ...] = ()
    tool_argument_fragments: tuple[str, ...] = ()
    include_usage: bool = True
    hold_after_partial: bool = False
    hold_before_response: bool = False
    text_fragments: tuple[str, ...] = ()
    reasoning_fragments: tuple[str, ...] = ()
    usage_updates: tuple[tuple[int, int, int], ...] = ()
    finish_reason: str = "stop"
    flush_stream_chunks: bool = False
    stream_chunk_delay_seconds: float = 0.0
    fail_after_partial: bool = False


@dataclass(frozen=True)
class InputRun:
    chunks: tuple[tuple[bytes, float], ...]
    expected_response_ids: tuple[Any, ...] | None = None
    expected_response_commands: tuple[str | None, ...] | None = None
    expected_successes: tuple[bool, ...] | None = None
    expected_data_presence: tuple[bool, ...] | None = None
    expected_error_presence: tuple[bool, ...] | None = None
    expected_response_dispositions: tuple[str | None, ...] | None = None
    expected_exit_code: int | None = 0
    forbidden_persisted_session_text: tuple[str, ...] = ()
    expected_provider_disconnects: int | None = None
    assert_process_group_disposed: bool = False
    bind_rpc_tool_gate_path: bool = False
    preserve_response_error_text: bool = False
    response_only: bool = False
    provider_response_delays: tuple[float, ...] = ()
    provider_steps: tuple[FixtureProviderStep, ...] = ()
    provider_api: str = "openai-completions"
    reasoning: bool = False
    retry_settings: tuple[tuple[str, Any], ...] = ()
    provider_waits: tuple[tuple[int, int, float], ...] = ()
    stdout_waits: tuple[tuple[int, str, int, float], ...] = ()
    stdout_read_chunk_size: int = 64 * 1024
    stdout_pause_before_input_index: int | None = None
    stdout_pause_after_input_index: int | None = None
    stdout_pause_seconds: float = 0.0
    close_stdout_after_input_index: int | None = None
    close_stderr_after_input_index: int | None = None
    terminate_after_input_index: int | None = None
    termination_signal: str = "SIGTERM"
    ui_responses: tuple[tuple[str, str, dict[str, Any]], ...] = ()
    ui_wait_all_requests: bool = False
    ui_response_after_input_index: int = 0
    ui_unknown_response_ids: tuple[str, ...] = ()
    ui_duplicate_late_response: bool = False
    ui_expected_requests: tuple[dict[str, Any], ...] = ()
    tool_start_waits: tuple[tuple[int, float], ...] = ()
    release_tool_gate_after_input_index: int | None = None
    release_provider_after_input_index: int | None = None
    resume_session: bool = False
    reuse_workspace_previous: bool = False
    no_session: bool = False
    rpc_command_resources: bool = False
    session_fixture_name: str | None = None
    isolate_export_output_directory: bool = False
    session_fixture_jsonl: str | None = None
    session_fixture_native_json: str | None = None
    fixture_files: tuple[tuple[str, str], ...] = ()
    pi_fixture_files: tuple[tuple[str, str], ...] = ()
    pisharp_fixture_files: tuple[tuple[str, str], ...] = ()
    pi_input_replacements: tuple[tuple[str, str], ...] = ()
    pisharp_input_replacements: tuple[tuple[str, str], ...] = ()
    capture_session_files: bool = False
    compare_session_files: bool = True
    capture_export_files: bool = False
    compare_export_files: bool = False
    export_file_expectations: tuple[tuple[str, str, tuple[str, ...], tuple[str, ...]], ...] = ()
    expected_persisted_session_text: tuple[str, ...] = ()
    session_fixture_files: tuple[tuple[str, str], ...] = ()
    session_file_expectations: tuple[tuple[str, tuple[str, ...], tuple[str, ...]], ...] = ()
    session_file_state_expectations: tuple[
        tuple[str, str | None, tuple[str, ...], tuple[str, ...]], ...
    ] = ()
    tool_side_effect_expectations: tuple[tuple[str, str], ...] = ()
    expected_project_commands: tuple[tuple[str, tuple[tuple[str, str, str, str, str, str], ...]], ...] = ()
    expected_command_names: tuple[tuple[str, tuple[str, ...]], ...] = ()
    expected_project_contexts: tuple[tuple[str, bool], ...] = ()
    project_rebind_fixture: bool = False
    normalize_workspace_paths: bool = False
    normalize_extension_paths: bool = False
    reuse_session_fixture_path: bool = False
    expected_min_provider_requests: int = 0
    lifecycle_event_types: tuple[str, ...] = ()
    preserve_event_payloads: bool = False
    preserve_event_payload_types: tuple[str, ...] = ()
    preserve_agent_end_messages: bool = False
    preserve_message_update_deltas_only: bool = False
    preserve_response_data_commands: tuple[str, ...] = ()
    preserve_session_identity: bool = False
    normalize_session_entry_ids: bool = False
    preserve_system_message_payloads: bool = False
    forbidden_lifecycle_event_types: tuple[str, ...] = ()
    minimum_lifecycle_event_counts: tuple[tuple[str, int], ...] = ()
    assistant_terminal_stop_reason: str | None = None
    require_assistant_error_message: bool = False
    preserve_assistant_terminal_shape: bool = False
    use_handled_extension_command: bool = False
    use_entry_appending_extension: bool = False
    use_rpc_navigation_extension: bool = False
    use_rpc_ui_extension: bool = False
    rpc_ui_extension_error: bool = False
    rpc_ui_stream_editor: bool = False
    enable_bash_tool: bool = False
    active_tool_names: tuple[str, ...] = ()
    use_rpc_tool_extension: bool = False
    compare_provider_requests: bool = False
    provider_request_expectations: tuple[tuple[int, tuple[str, ...], tuple[str, ...]], ...] = ()
    expected_provider_model_ids: tuple[str, ...] | None = None
    compare_stderr: bool = True
    model_context_window: int | None = 4096
    bind_fixture_working_directory: bool = False
    omit_no_tools: bool = False
    model_catalog: tuple[dict[str, Any], ...] | None = None
    model_scope: tuple[str, ...] = ()
    settings_fixture: dict[str, Any] | None = None
    initial_model_id: str | None = None
    initial_model_provider: str = "fixture"
    omit_cli_model_selection: bool = False
    preserve_state_flags: bool = False
    preserve_queue_state: bool = False
    preserve_queue_response_data: bool = False
    state_flag_expectations: tuple[tuple[str, str, bool], ...] = ()
    state_expectations: tuple[tuple[str, str, Any], ...] = ()
    expected_marker_order: tuple[str, ...] = ()
    all_responses_succeed: bool = False
    require_diagnostic: bool = False
    require_all_stdout_json: bool = False
    require_empty_stderr: bool = False
    require_no_output: bool = False
    response_data_expectations: tuple[tuple[str, str, Any], ...] = ()
    provider_request_text_excludes: tuple[str, ...] = ()


def _request(request_id: str | None, request_type: str = "get_state", **fields: Any) -> bytes:
    value: dict[str, Any] = {"type": request_type}
    if request_id is not None:
        value["id"] = request_id
    value.update(fields)
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def _line(request_id: str | None, request_type: str = "get_state", **fields: Any) -> bytes:
    return _request(request_id, request_type, **fields) + b"\n"


def _rpc_tool_arguments(value: str, *, delay_ms: int = 0, emit_partial: bool = False,
                        fail: bool = False) -> str:
    return json.dumps({"value": value, "delayMs": delay_ms, "emitPartial": emit_partial, "fail": fail},
                      separators=(",", ":"))


def _one(payload: bytes, *, expected_ids: tuple[str | None, ...], **flags: Any) -> tuple[InputRun, ...]:
    return (InputRun(((payload, 0.0),), expected_response_ids=expected_ids, **flags),)


R1_SCENARIOS: dict[str, tuple[InputRun, ...]] = {}

R1_SCENARIOS["R1.lf-delimited-record"] = _one(
    _line("lf-1") + _line("lf-2"), expected_ids=("lf-1", "lf-2"), all_responses_succeed=True,
)

utf8 = _line("utf8", ignored="café")
utf8_offset = utf8.index("é".encode("utf-8")) + 1
R1_SCENARIOS["R1.utf8-split-codepoint"] = (
    InputRun(((utf8[:utf8_offset], 0.0), (utf8[utf8_offset:], 0.05)),
             expected_response_ids=("utf8",), all_responses_succeed=True),
)

R1_SCENARIOS["R1.optional-crlf"] = _one(
    _request("crlf") + b"\r\n", expected_ids=("crlf",), all_responses_succeed=True,
)
R1_SCENARIOS["R1.unicode-separators-in-string"] = _one(
    _line("separators", ignored="before\u2028middle\u2029after"),
    expected_ids=("separators",), all_responses_succeed=True,
)

incremental_one = _line("incremental-1")
incremental_two = _line("incremental-2")
R1_SCENARIOS["R1.incremental-and-multiple-chunks"] = (
    InputRun(((incremental_one[:9], 0.0), (incremental_one[9:], 0.05), (incremental_two, 0.05)),
             expected_response_ids=("incremental-1", "incremental-2"), all_responses_succeed=True),
)

incomplete = _line("completed-later")
R1_SCENARIOS["R1.incomplete-record-completion"] = (
    InputRun(((incomplete[:len(incomplete) // 2], 0.0), (incomplete[len(incomplete) // 2:], 0.05)),
             expected_response_ids=("completed-later",), all_responses_succeed=True),
)
R1_SCENARIOS["R1.large-valid-record"] = _one(
    _line("one-mib", "set_session_name", name="x" * (1024 * 1024)),
    expected_ids=("one-mib",), all_responses_succeed=True,
)
R1_SCENARIOS["R1.malformed-json-recovery"] = (
    InputRun(((b'{"id":\n', 0.0), (_line("recovered"), 0.05)),
             expected_response_ids=(_ABSENT, "recovered"), expected_successes=(False, True),
             require_diagnostic=True),
)
R1_SCENARIOS["R1.empty-and-blank-input"] = (
    InputRun((), expected_response_ids=(), require_no_output=True),
    InputRun(((b"\n\n" + _line("after-blanks"), 0.0),),
             expected_response_ids=(_ABSENT, _ABSENT, "after-blanks"), expected_successes=(False, False, True)),
)
eof = _request("eof-final")
R1_SCENARIOS["R1.eof-incomplete-final-record"] = (
    InputRun(((eof, 0.0),), expected_response_ids=("eof-final",), all_responses_succeed=True),
)
R1_SCENARIOS["R1.stdout-stderr-purity"] = _one(
    _line("stdout-only"), expected_ids=("stdout-only",), all_responses_succeed=True,
    require_all_stdout_json=True, require_empty_stderr=True,
)


R2_SCENARIOS: dict[str, tuple[InputRun, ...]] = {}
R2_SCENARIOS["R2.correlated-response-id"] = _one(
    _line("req-001"), expected_ids=("req-001",), expected_response_commands=("get_state",),
    expected_successes=(True,), expected_data_presence=(True,),
)
R2_SCENARIOS["R2.uncorrelated-response-omits-id"] = _one(
    _line(None), expected_ids=(_ABSENT,), expected_response_commands=("get_state",),
    expected_successes=(True,), expected_data_presence=(True,),
)
R2_SCENARIOS["R2.id-type-validation"] = _one(
    b'{"id":null,"type":"get_state"}\n'
    b'{"id":9,"type":"get_state"}\n'
    b'{"id":{"x":1},"type":"get_state"}\n'
    b'{"id":"","type":"get_state"}\n',
    expected_ids=(None, 9, {"x": 1}, ""),
    expected_response_commands=("get_state",) * 4,
    expected_successes=(True,) * 4,
    expected_data_presence=(True,) * 4,
)
R2_SCENARIOS["R2.success-response-shape"] = (
    InputRun(((_line("state", "get_state"), 0.0),), expected_response_ids=("state",),
             expected_response_commands=("get_state",), expected_successes=(True,),
             expected_data_presence=(True,), expected_error_presence=(False,)),
    InputRun(((_line("abort", "abort"), 0.0),), expected_response_ids=("abort",),
             expected_response_commands=("abort",), expected_successes=(True,),
             expected_data_presence=(False,), expected_error_presence=(False,)),
    InputRun(((_line("name", "set_session_name", name="R2"), 0.0),),
             expected_response_ids=("name",), expected_response_commands=("set_session_name",),
             expected_successes=(True,), expected_data_presence=(False,), expected_error_presence=(False,)),
)
R2_SCENARIOS["R2.error-response-shape"] = (
    InputRun(((_line("bad-model", "set_model", provider="fixture", modelId="missing"), 0.0),),
             expected_response_ids=("bad-model",), expected_response_commands=("set_model",),
             expected_successes=(False,), expected_data_presence=(False,), expected_error_presence=(True,)),
    InputRun(((_line("bad-command", "unknown_command"), 0.0),),
             expected_response_ids=("bad-command",), expected_response_commands=("unknown_command",),
             expected_successes=(False,), expected_data_presence=(False,), expected_error_presence=(True,)),
)
R2_SCENARIOS["R2.absent-versus-null-fields"] = (
    InputRun(((_line("absent-since", "get_entries"), 0.0),), expected_response_ids=("absent-since",),
             expected_response_commands=("get_entries",), expected_successes=(True,),
             expected_data_presence=(True,), expected_error_presence=(False,)),
    InputRun(((_line("null-since", "get_entries", since=None), 0.0),),
             expected_response_ids=("null-since",), expected_response_commands=("get_entries",),
             expected_successes=(False,), expected_data_presence=(False,), expected_error_presence=(True,)),
)
R2_SCENARIOS["R2.unknown-command-recovery"] = _one(
    _line("unknown", "unknown_command") + _line("after", "get_state"),
    expected_ids=("unknown", "after"),
    expected_response_commands=("unknown_command", "get_state"),
    expected_successes=(False, True), expected_data_presence=(False, True),
    expected_error_presence=(True, False),
)
R2_SCENARIOS["R2.invalid-request-shapes"] = (
    InputRun(((b'"scalar"\n', 0.0),), expected_response_ids=(_ABSENT,), expected_successes=(False,)),
    InputRun(((b'[]\n', 0.0),), expected_response_ids=(_ABSENT,), expected_successes=(False,)),
    InputRun(((b'{"id":"missing-type"}\n', 0.0),), expected_response_ids=("missing-type",),
             expected_response_commands=(None,), expected_successes=(False,)),
    InputRun(((_line("wrong-name", "set_session_name", name=9), 0.0),),
             expected_response_ids=("wrong-name",), expected_successes=(False,)),
)
R2_SCENARIOS["R2.missing-required-properties"] = (
    InputRun(((_line("prompt", "prompt"), 0.0),), expected_response_ids=("prompt",),
             expected_response_commands=("prompt",), expected_successes=(False,)),
    InputRun(((_line("bash", "bash"), 0.0), (b"", 2.0)), expected_response_ids=("bash",),
             expected_response_commands=("bash",), expected_successes=(True,), expected_data_presence=(True,)),
    InputRun(((_line("switch", "switch_session"), 0.0),), expected_response_ids=("switch",),
             expected_response_commands=("switch_session",), expected_successes=(False,)),
    InputRun(((_line("fork", "fork"), 0.0),), expected_response_ids=("fork",),
             expected_response_commands=("fork",), expected_successes=(False,)),
    InputRun(((_line("name", "set_session_name"), 0.0),), expected_response_ids=("name",),
             expected_response_commands=("set_session_name",), expected_successes=(False,),
             preserve_response_error_text=True),
)
R2_SCENARIOS["R2.overlapping-correlated-requests"] = _one(
    _line("overlap-state-1", "get_state") + _line("overlap-state-2", "get_state") +
    _line("overlap-models", "get_available_models"),
    expected_ids=("overlap-state-1", "overlap-state-2", "overlap-models"),
    expected_response_commands=("get_state", "get_state", "get_available_models"),
    expected_successes=(True, True, True), expected_data_presence=(True, True, True),
)
R2_SCENARIOS["R2.long-command-acceptance"] = (
    InputRun(
        chunks=(
            (_line("long-auto-off", "set_auto_compaction", enabled=False), 0.0),
            (_line("long-prompt-1", "prompt", message="first provider-backed turn"), 0.0),
            (_line("long-state-1", "get_state"), 0.0),
            (_line("long-prompt-2", "prompt", message="second provider-backed turn"), 0.0),
            (_line("long-compact", "compact"), 0.0),
            (_line("long-state-2", "get_state"), 0.0),
        ),
        expected_response_ids=("long-auto-off", "long-prompt-1", "long-state-1", "long-prompt-2",
                               "long-state-2", "long-compact"),
        expected_response_commands=("set_auto_compaction", "prompt", "get_state", "prompt", "get_state", "compact"),
        expected_successes=(True,) * 6,
        expected_data_presence=(False, True, True, True, True, True),
        expected_error_presence=(False,) * 6,
        provider_response_delays=(0.75, 0.75, 1.25, 1.25),
        provider_waits=((1, 1, 10.0), (3, 2, 10.0), (4, 3, 10.0)),
        stdout_waits=(
            (0, '"id":"long-auto-off"', 1, 10.0),
            (2, '"id":"long-state-1"', 1, 10.0),
            (2, '"type":"agent_settled"', 1, 15.0),
            (3, '"type":"agent_settled"', 2, 15.0),
            (4, '"type":"compaction_start"', 1, 10.0),
            (5, '"id":"long-state-2"', 1, 10.0),
            (5, '"type":"compaction_end"', 1, 20.0),
            (5, '"id":"long-compact"', 1, 10.0),
        ),
        expected_min_provider_requests=3,
        lifecycle_event_types=("agent_start", "agent_end", "agent_settled", "compaction_start", "compaction_end"),
        preserve_state_flags=True,
        state_flag_expectations=(("long-state-1", "isStreaming", True), ("long-state-2", "isCompacting", True)),
        expected_marker_order=(
            "response:long-auto-off", "response:long-prompt-1", "response:long-state-1", "event:agent_settled",
            "response:long-prompt-2", "event:agent_settled", "event:compaction_start",
            "response:long-state-2", "event:compaction_end", "response:long-compact",
        ),
    ),
)
R2_SCENARIOS["R2.active-compaction-command"] = (
    InputRun(
        chunks=(
            (_line("active-auto-off", "set_auto_compaction", enabled=False), 0.0),
            (_line("active-prompt-1", "prompt", message="seed first turn"), 0.0),
            (_line("active-prompt-2", "prompt", message="seed second turn"), 0.0),
            (_line("active-compact", "compact"), 0.0),
            (_line("active-state", "get_state"), 0.0),
            (_line("active-setting", "set_auto_compaction", enabled=True), 0.0),
        ),
        expected_response_ids=("active-auto-off", "active-prompt-1", "active-prompt-2", "active-state",
                               "active-setting", "active-compact"),
        expected_response_commands=("set_auto_compaction", "prompt", "prompt", "get_state",
                                    "set_auto_compaction", "compact"),
        expected_successes=(True,) * 6,
        expected_data_presence=(False, True, True, True, False, True),
        expected_error_presence=(False,) * 6,
        provider_response_delays=(0.0, 0.0, 1.5, 1.5, 1.5),
        provider_waits=((1, 1, 10.0), (2, 2, 10.0), (3, 3, 10.0)),
        stdout_waits=(
            (0, '"id":"active-auto-off"', 1, 10.0),
            (1, '"type":"agent_settled"', 1, 15.0),
            (2, '"type":"agent_settled"', 2, 15.0),
            (3, '"type":"compaction_start"', 1, 10.0),
            (4, '"id":"active-state"', 1, 10.0),
            (5, '"id":"active-setting"', 1, 10.0),
            (5, '"type":"compaction_end"', 1, 20.0),
            (5, '"id":"active-compact"', 1, 10.0),
        ),
        expected_min_provider_requests=3,
        lifecycle_event_types=("agent_start", "agent_end", "agent_settled", "compaction_start", "compaction_end"),
        preserve_state_flags=True,
        state_flag_expectations=(("active-state", "isCompacting", True),),
        expected_marker_order=(
            "response:active-auto-off", "response:active-prompt-1", "event:agent_settled",
            "response:active-prompt-2",
            "event:agent_settled", "event:compaction_start", "response:active-state",
            "response:active-setting", "event:compaction_end", "response:active-compact",
        ),
    ),
)


R3_SCENARIOS: dict[str, tuple[InputRun, ...]] = {}
R3_SCENARIOS["R3.idle-prompt-started"] = (
    InputRun(
        chunks=((_line("idle-prompt", "prompt", message="report the fixture response"), 0.0),),
        expected_response_ids=("idle-prompt",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",), provider_steps=(FixtureProviderStep(delay_seconds=0.2),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"idle-prompt"', 1, 10.0), (0, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        minimum_lifecycle_event_counts=(
            ("agent_start", 1), ("turn_start", 1), ("message_start", 2),
            ("message_update", 1), ("message_end", 2), ("turn_end", 1), ("agent_end", 1), ("agent_settled", 1),
        ),
        lifecycle_event_types=("agent_start", "turn_start", "message_start", "message_update", "message_end",
                               "turn_end", "agent_end", "agent_settled"),
        preserve_system_message_payloads=True,
        expected_marker_order=("response:idle-prompt", "event:agent_start", "event:agent_settled"),
    ),
)
R3_SCENARIOS["R3.prompt-handled-without-run"] = (
    InputRun(
        chunks=((_line("handled-prompt", "prompt", message="/fixture argument"), 0.0),),
        expected_response_ids=("handled-prompt",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("handled",), use_handled_extension_command=True,
        lifecycle_event_types=("agent_start", "agent_settled"), preserve_event_payloads=True,
        forbidden_lifecycle_event_types=("agent_start", "agent_settled"),
        stdout_waits=((0, '"id":"handled-prompt"', 1, 10.0),),
        expected_marker_order=("response:handled-prompt",),
    ),
)
R3_SCENARIOS["R3.busy-prompt-default-behavior"] = (
    InputRun(
        chunks=(
            (_line("busy-start", "prompt", message="start a delayed run"), 0.0),
            (_line("busy-default", "prompt", message="submit without queue behavior"), 0.0),
        ),
        expected_response_ids=("busy-start", "busy-default"),
        expected_response_commands=("prompt", "prompt"), expected_successes=(True, False),
        expected_data_presence=(True, False), expected_error_presence=(False, True),
        expected_response_dispositions=("started", None), preserve_response_error_text=True,
        provider_steps=(FixtureProviderStep(delay_seconds=1.0),), provider_waits=((0, 1, 10.0),),
        lifecycle_event_types=("agent_settled",),
        stdout_waits=(
            (0, '"id":"busy-start"', 1, 10.0),
            (1, '"id":"busy-default"', 1, 10.0),
            (1, '"type":"agent_settled"', 1, 15.0),
        ),
        expected_min_provider_requests=1, compare_provider_requests=True,
        expected_marker_order=("response:busy-start", "response:busy-default", "event:agent_settled"),
    ),
)

for behavior, case_id in (("steer", "R3.busy-prompt-explicit-steer"),
                          ("followUp", "R3.busy-prompt-explicit-followup")):
    prefix = "steer" if behavior == "steer" else "followup"
    R3_SCENARIOS[case_id] = (
        InputRun(
            chunks=(
                (_line(f"{prefix}-start", "prompt", message="start a delayed run"), 0.0),
                (_line(f"{prefix}-prompt", "prompt", message=f"queue a {behavior} message",
                       streamingBehavior=behavior), 0.0),
                (b"", 0.0),
            ),
            expected_response_ids=(f"{prefix}-start", f"{prefix}-prompt"),
            expected_response_commands=("prompt", "prompt"), expected_successes=(True, True),
            expected_data_presence=(True, True), expected_error_presence=(False, False),
            expected_response_dispositions=("started", "queued"),
            provider_response_delays=(0.5, 0.0), provider_waits=((0, 1, 10.0), (2, 2, 15.0)),
            lifecycle_event_types=("queue_update", "agent_settled"),
            stdout_waits=(
                (0, f'"id":"{prefix}-start"', 1, 10.0),
                (1, f'"id":"{prefix}-prompt"', 1, 10.0),
                (1, '"type":"queue_update"', 1, 10.0),
                (2, '"type":"agent_settled"', 1, 20.0),
            ),
            expected_min_provider_requests=2, compare_provider_requests=True,
            minimum_lifecycle_event_counts=(("queue_update", 1), ("agent_settled", 1)),
            expected_marker_order=(f"response:{prefix}-start", "event:queue_update", f"response:{prefix}-prompt",
                                   "event:queue_update", "event:agent_settled"),
        ),
    )

R3_SCENARIOS["R3.prompt-during-tool-execution"] = (
    InputRun(
        chunks=(
            (_line("tool-start", "prompt", message="call the fixture bash tool"), 0.0),
            (_line("tool-followup", "prompt", message="queue a message during the tool",
                   streamingBehavior="followUp"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("tool-start", "tool-followup"),
        expected_response_commands=("prompt", "prompt"), expected_successes=(True, True),
        expected_data_presence=(True, True), expected_error_presence=(False, False),
        expected_response_dispositions=("started", "queued"), enable_bash_tool=True,
        provider_steps=(
            FixtureProviderStep(tool_name="bash", tool_arguments=json.dumps({
                "command": 'gate=__RPC_TOOL_GATE__; echo started > "$gate.started"; '
                           'while [ ! -f "$gate.release" ]; do sleep 0.01; done; '
                           'echo barrier-released',
            })),
            FixtureProviderStep(text="the tool and follow-up completed"),
        ),
        provider_waits=((0, 1, 10.0), (2, 2, 20.0)),
        stdout_waits=(
            (0, '"id":"tool-start"', 1, 10.0),
            (0, '"type":"tool_execution_start"', 1, 15.0),
            (1, '"id":"tool-followup"', 1, 10.0),
            (1, '"type":"queue_update"', 1, 10.0),
            (2, '"type":"agent_settled"', 1, 25.0),
        ),
        release_tool_gate_after_input_index=1, expected_min_provider_requests=3, compare_provider_requests=True,
        lifecycle_event_types=("tool_execution_start", "tool_execution_end", "queue_update", "agent_settled"),
        minimum_lifecycle_event_counts=(
            ("tool_execution_start", 1), ("tool_execution_end", 1), ("queue_update", 1), ("agent_settled", 1),
        ),
        expected_marker_order=("response:tool-start", "event:tool_execution_start", "event:queue_update",
                               "response:tool-followup", "event:tool_execution_end", "event:queue_update",
                               "event:agent_settled"),
    ),
)

R3_SCENARIOS["R3.subscribe-before-fast-completion"] = (
    InputRun(
        chunks=((_line("fast-prompt", "prompt", message="complete immediately"), 0.0),),
        expected_response_ids=("fast-prompt",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",), provider_steps=(FixtureProviderStep(),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"fast-prompt"', 1, 10.0), (0, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("agent_start", "turn_start", "message_start", "message_update", "message_end",
                               "turn_end", "agent_end", "agent_settled"),
        minimum_lifecycle_event_counts=(
            ("agent_start", 1), ("turn_start", 1), ("message_start", 2), ("message_update", 1),
            ("message_end", 2), ("turn_end", 1), ("agent_end", 1), ("agent_settled", 1),
        ),
        expected_marker_order=("response:fast-prompt", "event:agent_start", "event:agent_settled"),
    ),
)
R3_SCENARIOS["R3.acceptance-before-completion"] = (
    InputRun(
        chunks=(
            (_line("accepted-prompt", "prompt", message="accept before delayed completion"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("accepted-prompt",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",), provider_steps=(FixtureProviderStep(delay_seconds=1.25),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"accepted-prompt"', 1, 10.0), (1, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("agent_start", "agent_settled"),
        expected_marker_order=("response:accepted-prompt", "event:agent_settled"),
    ),
)
R3_SCENARIOS["R3.provider-failure-after-acceptance"] = (
    InputRun(
        chunks=((_line("failed-prompt", "prompt", message="provider fails after acceptance"), 0.0),),
        expected_response_ids=("failed-prompt",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",), preserve_response_error_text=True,
        provider_steps=(FixtureProviderStep(status_code=400, error_message="fixture provider rejected request"),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"failed-prompt"', 1, 10.0), (0, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("agent_start", "message_end", "turn_end", "agent_end", "agent_settled"),
        assistant_terminal_stop_reason="error", require_assistant_error_message=True,
        preserve_assistant_terminal_shape=True,
        expected_marker_order=("response:failed-prompt", "event:agent_start", "event:message_end",
                               "event:turn_end", "event:agent_end", "event:agent_settled"),
    ),
)
R3_SCENARIOS["R3.abort-prompt-run"] = (
    InputRun(
        chunks=(
            (_line("abort-prompt", "prompt", message="abort after one partial response"), 0.0),
            (_line("abort-run", "abort"), 0.0),
        ),
        expected_response_ids=("abort-prompt", "abort-run"),
        expected_response_commands=("prompt", "abort"), expected_successes=(True, True),
        expected_data_presence=(True, False), expected_error_presence=(False, False),
        expected_response_dispositions=("started", None),
        provider_steps=(FixtureProviderStep(text="partial response before abort", hold_after_partial=True),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=(
            (0, '"id":"abort-prompt"', 1, 10.0),
            (0, "partial response before abort", 1, 10.0),
            (1, '"id":"abort-run"', 1, 15.0),
            (1, '"type":"agent_settled"', 1, 15.0),
        ),
        expected_min_provider_requests=1, compare_provider_requests=True,
        minimum_lifecycle_event_counts=(
            ("agent_start", 1), ("message_start", 2), ("message_update", 1),
            ("message_end", 2), ("turn_end", 1), ("agent_end", 1), ("agent_settled", 1),
        ),
        lifecycle_event_types=("message_update", "message_end", "turn_end", "agent_end", "agent_settled"),
        assistant_terminal_stop_reason="aborted", require_assistant_error_message=True,
        preserve_assistant_terminal_shape=True,
        expected_marker_order=("response:abort-prompt", "event:message_update", "event:message_end",
                               "event:turn_end", "event:agent_end", "event:agent_settled", "response:abort-run"),
    ),
)

for _case_id, _runs in tuple(R3_SCENARIOS.items()):
    R3_SCENARIOS[_case_id] = tuple(replace(run, model_context_window=65_536) for run in _runs)


R4_SCENARIOS: dict[str, tuple[InputRun, ...]] = {}
for _command, _case_id in (("steer", "R4.steer-command-idle"),
                           ("follow_up", "R4.followup-command-idle")):
    _prefix = "steer" if _command == "steer" else "followup"
    _expected_requests = 1 if _command == "steer" else 2
    R4_SCENARIOS[_case_id] = (
        InputRun(
            chunks=(
                (_line(f"{_prefix}-idle", _command, message=f"queued {_prefix} while idle"), 0.0),
                (_line(f"{_prefix}-idle-state", "get_state"), 0.0),
                (_line(f"{_prefix}-idle-prompt", "prompt", message="start queued work"), 0.0),
                (b"", 0.0),
            ),
            expected_response_ids=(f"{_prefix}-idle", f"{_prefix}-idle-state", f"{_prefix}-idle-prompt"),
            expected_response_commands=(_command, "get_state", "prompt"),
            expected_successes=(True, True, True), expected_data_presence=(True, True, True),
            expected_error_presence=(False, False, False),
            expected_response_dispositions=("queued", None, "started"),
            provider_steps=(FixtureProviderStep(text="initial response"),
                            FixtureProviderStep(text="processed queued input")),
            provider_waits=((2, _expected_requests, 10.0),),
            stdout_waits=((0, f'"id":"{_prefix}-idle"', 1, 10.0),
                          (1, f'"id":"{_prefix}-idle-state"', 1, 10.0),
                          (3, '"type":"agent_settled"', 1, 15.0)),
            expected_min_provider_requests=_expected_requests, compare_provider_requests=True,
            model_context_window=65_536,
            lifecycle_event_types=("queue_update", "agent_start", "agent_settled"),
            preserve_event_payload_types=("queue_update",), preserve_queue_state=True,
            minimum_lifecycle_event_counts=(("queue_update", 2), ("agent_start", 1), ("agent_settled", 1)),
            state_expectations=((f"{_prefix}-idle-state", "pendingMessageCount", 1),),
            expected_marker_order=(f"event:queue_update", f"response:{_prefix}-idle",
                                   f"response:{_prefix}-idle-state", f"response:{_prefix}-idle-prompt",
                                   "event:agent_start", "event:agent_settled"),
        ),
    )

for _steering_mode, _case_id in (("all", "R4.steering-all-mode"),
                                ("one-at-a-time", "R4.steering-one-at-a-time-mode")):
    _expected_requests = 2 if _steering_mode == "all" else 3
    R4_SCENARIOS[_case_id] = (
        InputRun(
            chunks=(
                (_line("steering-mode", "set_steering_mode", mode=_steering_mode), 0.0),
                (_line("steering-start", "prompt", message="start delayed steering turn"), 0.0),
                (_line("steering-a", "steer", message="first steering message"), 0.0),
                (_line("steering-b", "steer", message="second steering message"), 0.0),
                (_line("steering-pending", "get_state"), 0.0),
                (b"", 0.0),
                (_line("steering-state", "get_state"), 0.0),
                (b"", 0.0),
            ),
            expected_response_ids=("steering-mode", "steering-start", "steering-a", "steering-b",
                                   "steering-pending", "steering-state"),
            expected_response_commands=("set_steering_mode", "prompt", "steer", "steer",
                                        "get_state", "get_state"),
            expected_successes=(True,) * 6, expected_data_presence=(False, True, True, True, True, True),
            expected_error_presence=(False,) * 6,
            expected_response_dispositions=(None, "started", "queued", "queued", None, None),
            provider_steps=(FixtureProviderStep(hold_before_response=True, text="first completed turn"),
                            FixtureProviderStep(text="processed queued steering"),
                            FixtureProviderStep(text="processed next steering")),
            provider_waits=((1, 1, 10.0), (5, _expected_requests, 20.0)),
            stdout_waits=((0, '"id":"steering-mode"', 1, 10.0),
                          (1, '"id":"steering-start"', 1, 10.0),
                          (2, '"id":"steering-a"', 1, 10.0),
                          (3, '"id":"steering-b"', 1, 10.0),
                          (4, '"id":"steering-pending"', 1, 10.0),
                          (5, '"type":"agent_settled"', 1, 20.0),
                          (6, '"id":"steering-state"', 1, 10.0)),
            expected_min_provider_requests=_expected_requests, compare_provider_requests=True,
            release_provider_after_input_index=4,
            model_context_window=65_536,
            lifecycle_event_types=("queue_update", "agent_start", "agent_settled"),
            preserve_event_payload_types=("queue_update",), preserve_queue_state=True,
            minimum_lifecycle_event_counts=(("queue_update", 3), ("agent_start", 1), ("agent_settled", 1)),
            state_expectations=(("steering-pending", "steeringMode", _steering_mode),
                                ("steering-pending", "pendingMessageCount", 2),
                                ("steering-state", "pendingMessageCount", 0)),
            expected_marker_order=("response:steering-mode", "response:steering-start",
                                   "event:agent_start", "response:steering-a", "response:steering-b",
                                   "response:steering-pending", "event:agent_settled",
                                   "response:steering-state"),
        ),
    )

for _followup_mode, _case_id in (("all", "R4.followup-all-mode"),
                                ("one-at-a-time", "R4.followup-one-at-a-time-mode")):
    _expected_requests = 2 if _followup_mode == "all" else 3
    R4_SCENARIOS[_case_id] = (
        InputRun(
            chunks=(
                (_line("followup-mode", "set_follow_up_mode", mode=_followup_mode), 0.0),
                (_line("followup-start", "prompt", message="start delayed follow-up turn"), 0.0),
                (_line("followup-a", "follow_up", message="first follow-up message"), 0.0),
                (_line("followup-b", "follow_up", message="second follow-up message"), 0.0),
                (_line("followup-pending", "get_state"), 0.0),
                (b"", 0.0),
                (_line("followup-state", "get_state"), 0.0),
                (b"", 0.0),
            ),
            expected_response_ids=("followup-mode", "followup-start", "followup-a", "followup-b",
                                   "followup-pending", "followup-state"),
            expected_response_commands=("set_follow_up_mode", "prompt", "follow_up", "follow_up",
                                        "get_state", "get_state"),
            expected_successes=(True,) * 6, expected_data_presence=(False, True, True, True, True, True),
            expected_error_presence=(False,) * 6,
            expected_response_dispositions=(None, "started", "queued", "queued", None, None),
            provider_steps=(FixtureProviderStep(hold_before_response=True, text="first completed turn"),
                            FixtureProviderStep(text="processed queued follow-up"),
                            FixtureProviderStep(text="processed next follow-up")),
            provider_waits=((1, 1, 10.0), (5, _expected_requests, 20.0)),
            stdout_waits=((0, '"id":"followup-mode"', 1, 10.0),
                          (1, '"id":"followup-start"', 1, 10.0),
                          (2, '"id":"followup-a"', 1, 10.0),
                          (3, '"id":"followup-b"', 1, 10.0),
                          (4, '"id":"followup-pending"', 1, 10.0),
                          (5, '"type":"agent_settled"', 1, 25.0),
                          (6, '"id":"followup-state"', 1, 10.0)),
            expected_min_provider_requests=_expected_requests, compare_provider_requests=True,
            release_provider_after_input_index=4, model_context_window=65_536,
            lifecycle_event_types=("queue_update", "agent_start", "agent_settled"),
            preserve_event_payload_types=("queue_update",), preserve_queue_state=True,
            minimum_lifecycle_event_counts=(("queue_update", 4), ("agent_start", 1), ("agent_settled", 1)),
            state_expectations=(("followup-pending", "followUpMode", _followup_mode),
                                ("followup-pending", "pendingMessageCount", 2),
                                ("followup-state", "pendingMessageCount", 0)),
            expected_marker_order=("response:followup-mode", "response:followup-start",
                                   "event:agent_start", "response:followup-a", "response:followup-b",
                                   "response:followup-pending", "event:agent_settled",
                                   "response:followup-state"),
        ),
    )

R4_SCENARIOS["R4.steer-followup-precedence"] = (
    InputRun(
        chunks=(
            (_line("precedence-start", "prompt", message="start precedence turn"), 0.0),
            (_line("precedence-steer", "steer", message="steering must arrive first"), 0.0),
            (_line("precedence-followup", "follow_up", message="follow-up arrives after steering"), 0.0),
            (_line("precedence-pending", "get_state"), 0.0),
            (b"", 0.0),
            (b"", 0.0),
            (_line("precedence-state", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("precedence-start", "precedence-steer", "precedence-followup",
                               "precedence-pending", "precedence-state"),
        expected_response_commands=("prompt", "steer", "follow_up", "get_state", "get_state"),
        expected_successes=(True,) * 5, expected_data_presence=(True,) * 5,
        expected_error_presence=(False,) * 5,
        expected_response_dispositions=("started", "queued", "queued", None, None),
        provider_steps=(FixtureProviderStep(hold_before_response=True, text="first completed turn"),
                        FixtureProviderStep(text="processed steering"),
                        FixtureProviderStep(text="processed follow-up")),
        provider_waits=((0, 1, 10.0), (4, 3, 20.0)),
        stdout_waits=((0, '"id":"precedence-start"', 1, 10.0),
                      (1, '"id":"precedence-steer"', 1, 10.0),
                      (2, '"id":"precedence-followup"', 1, 10.0),
                      (3, '"id":"precedence-pending"', 1, 10.0),
                      (5, '"type":"agent_settled"', 1, 25.0),
                      (6, '"id":"precedence-state"', 1, 10.0)),
        expected_min_provider_requests=3, compare_provider_requests=True,
        release_provider_after_input_index=3, model_context_window=65_536,
        lifecycle_event_types=("queue_update", "agent_start", "agent_settled"),
        preserve_event_payload_types=("queue_update",), preserve_queue_state=True,
        minimum_lifecycle_event_counts=(("queue_update", 4), ("agent_start", 1), ("agent_settled", 1)),
        state_expectations=(("precedence-pending", "pendingMessageCount", 2),
                            ("precedence-state", "pendingMessageCount", 0)),
        expected_marker_order=("response:precedence-start", "event:agent_start",
                               "response:precedence-steer", "response:precedence-followup",
                               "response:precedence-pending", "event:agent_settled",
                               "response:precedence-state"),
    ),
)

R4_SCENARIOS["R4.multiple-concurrent-queue-updates"] = (
    InputRun(
        chunks=(
            (_line("multi-queue-start", "prompt", message="start queue update run"), 0.0),
            (_line("multi-queue-steer-a", "steer", message="steer alpha"), 0.0),
            (_line("multi-queue-followup-b", "follow_up", message="follow beta"), 0.0),
            (_line("multi-queue-steer-c", "steer", message="steer gamma"), 0.0),
            (_line("multi-queue-pending", "get_state"), 0.0),
            (b"", 0.0),
            (b"", 0.0),
            (_line("multi-queue-state", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("multi-queue-start", "multi-queue-steer-a", "multi-queue-followup-b",
                               "multi-queue-steer-c", "multi-queue-pending", "multi-queue-state"),
        expected_response_commands=("prompt", "steer", "follow_up", "steer", "get_state", "get_state"),
        expected_successes=(True,) * 6, expected_data_presence=(True,) * 6,
        expected_error_presence=(False,) * 6,
        expected_response_dispositions=("started", "queued", "queued", "queued", None, None),
        provider_steps=(FixtureProviderStep(hold_before_response=True, text="initial queue turn"),
                        FixtureProviderStep(text="processed steer alpha"),
                        FixtureProviderStep(text="processed steer gamma"),
                        FixtureProviderStep(text="processed follow beta")),
        provider_waits=((0, 1, 10.0), (5, 4, 25.0)),
        stdout_waits=((0, '"id":"multi-queue-start"', 1, 10.0),
                      (1, '"id":"multi-queue-steer-a"', 1, 10.0),
                      (2, '"id":"multi-queue-followup-b"', 1, 10.0),
                      (3, '"id":"multi-queue-steer-c"', 1, 10.0),
                      (4, '"id":"multi-queue-pending"', 1, 10.0),
                      (6, '"type":"agent_settled"', 1, 30.0),
                      (7, '"id":"multi-queue-state"', 1, 10.0)),
        expected_min_provider_requests=4, compare_provider_requests=True,
        release_provider_after_input_index=4, model_context_window=65_536,
        lifecycle_event_types=("queue_update", "agent_start", "agent_settled"),
        preserve_event_payload_types=("queue_update",), preserve_queue_state=True,
        minimum_lifecycle_event_counts=(("queue_update", 6), ("agent_start", 1), ("agent_settled", 1)),
        state_expectations=(("multi-queue-pending", "pendingMessageCount", 3),
                            ("multi-queue-state", "pendingMessageCount", 0)),
        expected_marker_order=("response:multi-queue-start", "event:agent_start",
                               "response:multi-queue-steer-a", "response:multi-queue-followup-b",
                               "response:multi-queue-steer-c", "response:multi-queue-pending",
                               "event:agent_settled", "response:multi-queue-state"),
    ),
)

R4_SCENARIOS["R4.clear-queue-snapshot"] = (
    InputRun(
        chunks=(
            (_line("clear-start", "prompt", message="start clear queue run"), 0.0),
            (_line("clear-steer", "steer", message="remove this steering message"), 0.0),
            (_line("clear-followup", "follow_up", message="remove this follow-up message"), 0.0),
            (_line("clear-pending", "get_state"), 0.0),
            (_line("clear-command", "clear_queue"), 0.0),
            (_line("clear-empty-state", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("clear-start", "clear-steer", "clear-followup", "clear-pending",
                               "clear-command", "clear-empty-state"),
        expected_response_commands=("prompt", "steer", "follow_up", "get_state", "clear_queue", "get_state"),
        expected_successes=(True,) * 6, expected_data_presence=(True,) * 6,
        expected_error_presence=(False,) * 6,
        expected_response_dispositions=("started", "queued", "queued", None, None, None),
        provider_steps=(FixtureProviderStep(hold_before_response=True, text="finished after clearing"),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"clear-start"', 1, 10.0),
                      (1, '"id":"clear-steer"', 1, 10.0),
                      (2, '"id":"clear-followup"', 1, 10.0),
                      (3, '"id":"clear-pending"', 1, 10.0),
                      (4, '"id":"clear-command"', 1, 10.0),
                      (5, '"id":"clear-empty-state"', 1, 10.0),
                      (6, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        release_provider_after_input_index=5, model_context_window=65_536,
        lifecycle_event_types=("queue_update", "agent_start", "agent_settled"),
        preserve_event_payload_types=("queue_update",), preserve_queue_state=True,
        preserve_queue_response_data=True,
        minimum_lifecycle_event_counts=(("queue_update", 3), ("agent_start", 1), ("agent_settled", 1)),
        state_expectations=(("clear-pending", "pendingMessageCount", 2),
                            ("clear-empty-state", "pendingMessageCount", 0)),
        expected_marker_order=("response:clear-start", "event:agent_start", "response:clear-steer",
                               "response:clear-followup", "response:clear-pending", "response:clear-command",
                               "response:clear-empty-state"),
    ),
)

R4_SCENARIOS["R4.queue-cancel-and-restore"] = (
    InputRun(
        chunks=(
            (_line("cancel-start", "prompt", message="start abort-with-queues run"), 0.0),
            (_line("cancel-steer", "steer", message="steering survives abort"), 0.0),
            (_line("cancel-followup", "follow_up", message="follow-up survives abort"), 0.0),
            (_line("cancel-abort", "abort"), 0.0),
            (_line("cancel-state", "get_state"), 0.0),
            (_line("cancel-next-prompt", "prompt", message="start the next prompt"), 0.0),
            (b"", 0.0),
            (_line("cancel-final-state", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("cancel-start", "cancel-steer", "cancel-followup", "cancel-abort",
                               "cancel-state", "cancel-next-prompt", "cancel-final-state"),
        expected_response_commands=("prompt", "steer", "follow_up", "abort", "get_state", "prompt", "get_state"),
        expected_successes=(True,) * 7, expected_data_presence=(True, True, True, False, True, True, True),
        expected_error_presence=(False,) * 7,
        expected_response_dispositions=("started", "queued", "queued", None, None, "started", None),
        provider_steps=(FixtureProviderStep(hold_before_response=True, text="aborted response"),
                        FixtureProviderStep(text="processed after abort"),
                        FixtureProviderStep(text="finished next prompt"),
                        FixtureProviderStep(text="processed restored follow-up")),
        provider_waits=((0, 1, 10.0), (5, 2, 15.0)),
        stdout_waits=((0, '"id":"cancel-start"', 1, 10.0),
                      (1, '"id":"cancel-steer"', 1, 10.0),
                      (2, '"id":"cancel-followup"', 1, 10.0),
                      (3, '"id":"cancel-abort"', 1, 20.0),
                      (4, '"id":"cancel-state"', 1, 10.0),
                      (5, '"id":"cancel-next-prompt"', 1, 10.0),
                      (5, '"type":"agent_settled"', 2, 20.0),
                      (7, '"id":"cancel-final-state"', 1, 10.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        model_context_window=65_536,
        lifecycle_event_types=("queue_update", "agent_start", "agent_settled"),
        preserve_event_payload_types=("queue_update",), preserve_queue_state=True,
        minimum_lifecycle_event_counts=(("queue_update", 3), ("agent_start", 2), ("agent_settled", 2)),
        preserve_queue_response_data=True,
        state_expectations=(("cancel-state", "pendingMessageCount", 2),
                            ("cancel-final-state", "pendingMessageCount", 0)),
        expected_marker_order=("response:cancel-start", "event:agent_start", "response:cancel-steer",
                               "response:cancel-followup", "event:agent_settled", "response:cancel-abort",
                               "response:cancel-state", "response:cancel-next-prompt"),
    ),
)

R4_SCENARIOS["R4.mode-change-persistence"] = (
    InputRun(
        chunks=(
            (_line("mode-set-steering", "set_steering_mode", mode="all"), 0.0),
            (_line("mode-set-followup", "set_follow_up_mode", mode="all"), 0.0),
            (_line("mode-seed-prompt", "prompt", message="save a resumable mode session"), 0.0),
            (b"", 0.0),
            (_line("mode-seed-state", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("mode-set-steering", "mode-set-followup", "mode-seed-prompt", "mode-seed-state"),
        expected_response_commands=("set_steering_mode", "set_follow_up_mode", "prompt", "get_state"),
        expected_successes=(True,) * 4, expected_data_presence=(False, False, True, True),
        expected_error_presence=(False,) * 4,
        expected_response_dispositions=(None, None, "started", None),
        provider_steps=(FixtureProviderStep(text="session ready"),),
        provider_waits=((2, 1, 10.0),),
        stdout_waits=((0, '"id":"mode-set-steering"', 1, 10.0),
                      (1, '"id":"mode-set-followup"', 1, 10.0),
                      (2, '"id":"mode-seed-prompt"', 1, 10.0),
                      (2, '"type":"agent_settled"', 1, 15.0),
                      (4, '"id":"mode-seed-state"', 1, 10.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        model_context_window=65_536,
        lifecycle_event_types=("agent_start", "agent_settled"),
        state_expectations=(("mode-seed-state", "steeringMode", "all"),
                            ("mode-seed-state", "followUpMode", "all"),
                            ("mode-seed-state", "pendingMessageCount", 0)),
        expected_marker_order=("response:mode-set-steering", "response:mode-set-followup",
                               "response:mode-seed-prompt", "event:agent_start",
                               "event:agent_settled", "response:mode-seed-state"),
    ),
    InputRun(
        chunks=(
            (_line("resume-mode-state", "get_state"), 0.0),
            (_line("resume-mode-prompt", "prompt", message="probe persisted queue modes"), 0.0),
            (_line("resume-mode-steer-a", "steer", message="persisted steering alpha"), 0.0),
            (_line("resume-mode-steer-b", "steer", message="persisted steering beta"), 0.0),
            (_line("resume-mode-followup-a", "follow_up", message="persisted follow-up alpha"), 0.0),
            (_line("resume-mode-followup-b", "follow_up", message="persisted follow-up beta"), 0.0),
            (_line("resume-mode-pending", "get_state"), 0.0),
            (b"", 0.0),
            (_line("resume-mode-final-state", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("resume-mode-state", "resume-mode-prompt", "resume-mode-steer-a",
                               "resume-mode-steer-b", "resume-mode-followup-a", "resume-mode-followup-b",
                               "resume-mode-pending", "resume-mode-final-state"),
        expected_response_commands=("get_state", "prompt", "steer", "steer", "follow_up", "follow_up",
                                    "get_state", "get_state"),
        expected_successes=(True,) * 8, expected_data_presence=(True,) * 8,
        expected_error_presence=(False,) * 8,
        expected_response_dispositions=(None, "started", "queued", "queued", "queued", "queued", None, None),
        provider_steps=(FixtureProviderStep(hold_before_response=True, text="resumed mode probe"),
                        FixtureProviderStep(text="processed both steering messages"),
                        FixtureProviderStep(text="processed both follow-up messages")),
        provider_waits=((1, 1, 15.0), (7, 3, 20.0)),
        stdout_waits=((0, '"id":"resume-mode-state"', 1, 10.0),
                      (1, '"id":"resume-mode-prompt"', 1, 10.0),
                      (2, '"id":"resume-mode-steer-a"', 1, 10.0),
                      (3, '"id":"resume-mode-steer-b"', 1, 10.0),
                      (4, '"id":"resume-mode-followup-a"', 1, 10.0),
                      (5, '"id":"resume-mode-followup-b"', 1, 10.0),
                      (6, '"id":"resume-mode-pending"', 1, 10.0),
                      (7, '"type":"agent_settled"', 1, 25.0),
                      (8, '"id":"resume-mode-final-state"', 1, 10.0)),
        release_provider_after_input_index=6,
        resume_session=True, reuse_workspace_previous=True,
        expected_min_provider_requests=3, compare_provider_requests=True,
        model_context_window=65_536,
        lifecycle_event_types=("queue_update", "agent_start", "agent_settled"),
        preserve_event_payload_types=("queue_update",), preserve_queue_state=True,
        minimum_lifecycle_event_counts=(("queue_update", 8), ("agent_start", 1), ("agent_settled", 1)),
        state_expectations=(("resume-mode-state", "steeringMode", "all"),
                            ("resume-mode-state", "followUpMode", "all"),
                            ("resume-mode-state", "pendingMessageCount", 0),
                            ("resume-mode-pending", "pendingMessageCount", 4),
                            ("resume-mode-final-state", "pendingMessageCount", 0)),
        expected_marker_order=("response:resume-mode-state", "response:resume-mode-prompt",
                               "event:agent_start", "response:resume-mode-steer-a",
                               "response:resume-mode-steer-b", "response:resume-mode-followup-a",
                               "response:resume-mode-followup-b", "response:resume-mode-pending",
                               "event:agent_settled", "response:resume-mode-final-state"),
    ),
)

R4_SCENARIOS["R4.tool-finishes-before-next-request"] = (
    InputRun(
        chunks=(
            (_line("tool-queue-start", "prompt", message="call the queue barrier tool"), 0.0),
            (_line("tool-queue-steer", "steer", message="steering behind tool result"), 0.0),
            (_line("tool-queue-followup", "follow_up", message="follow-up after steering turn"), 0.0),
            (_line("tool-queue-pending", "get_state"), 0.0),
            (b"", 0.0),
            (_line("tool-queue-final-state", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("tool-queue-start", "tool-queue-steer", "tool-queue-followup",
                               "tool-queue-pending", "tool-queue-final-state"),
        expected_response_commands=("prompt", "steer", "follow_up", "get_state", "get_state"),
        expected_successes=(True,) * 5, expected_data_presence=(True,) * 5,
        expected_error_presence=(False,) * 5,
        expected_response_dispositions=("started", "queued", "queued", None, None),
        enable_bash_tool=True,
        provider_steps=(
            FixtureProviderStep(tool_name="bash", tool_arguments=json.dumps({
                "command": 'gate=__RPC_TOOL_GATE__; echo started > "$gate.started"; '
                           'while [ ! -f "$gate.release" ]; do sleep 0.01; done; '
                           'echo barrier-released',
            })),
            FixtureProviderStep(text="tool result recorded before steering request"),
            FixtureProviderStep(text="follow-up delivered after steering turn"),
        ),
        provider_waits=((0, 1, 10.0), (4, 3, 25.0)),
        stdout_waits=((0, '"id":"tool-queue-start"', 1, 10.0),
                      (0, '"type":"tool_execution_start"', 1, 15.0),
                      (1, '"id":"tool-queue-steer"', 1, 10.0),
                      (2, '"id":"tool-queue-followup"', 1, 10.0),
                      (3, '"id":"tool-queue-pending"', 1, 10.0),
                      (4, '"type":"agent_settled"', 1, 30.0),
                      (5, '"id":"tool-queue-final-state"', 1, 10.0)),
        release_tool_gate_after_input_index=3,
        expected_min_provider_requests=3, compare_provider_requests=True,
        model_context_window=65_536,
        lifecycle_event_types=("tool_execution_start", "tool_execution_end", "queue_update",
                               "agent_start", "agent_settled"),
        preserve_event_payload_types=("queue_update",), preserve_queue_state=True,
        minimum_lifecycle_event_counts=(("tool_execution_start", 1), ("tool_execution_end", 1),
                                        ("queue_update", 4), ("agent_start", 1), ("agent_settled", 1)),
        state_expectations=(("tool-queue-pending", "pendingMessageCount", 2),
                            ("tool-queue-final-state", "pendingMessageCount", 0)),
        expected_marker_order=("response:tool-queue-start", "event:agent_start", "event:tool_execution_start",
                               "response:tool-queue-steer", "response:tool-queue-followup",
                               "response:tool-queue-pending", "event:tool_execution_end",
                               "event:agent_settled", "response:tool-queue-final-state"),
    ),
)


R5_SCENARIOS: dict[str, tuple[InputRun, ...]] = {}
R5_SCENARIOS["R5.agent-start-end-settled"] = (
    InputRun(
        chunks=((_line("lifecycle-prompt", "prompt", message="report the lifecycle response"), 0.0),),
        expected_response_ids=("lifecycle-prompt",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",), provider_steps=(FixtureProviderStep(text="lifecycle response"),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"lifecycle-prompt"', 1, 10.0),
                      (0, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("agent_start", "agent_end", "agent_settled"),
        preserve_event_payload_types=("agent_settled",),
        minimum_lifecycle_event_counts=(("agent_start", 1), ("agent_end", 1), ("agent_settled", 1)),
        expected_marker_order=("response:lifecycle-prompt", "event:agent_start", "event:agent_end",
                               "event:agent_settled"),
    ),
)
R5_SCENARIOS["R5.turn-start-end"] = (
    InputRun(
        chunks=((_line("turn-prompt", "prompt", message="report the final turn"), 0.0),),
        expected_response_ids=("turn-prompt",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",), provider_steps=(FixtureProviderStep(text="turn final"),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"turn-prompt"', 1, 10.0),
                      (0, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("turn_start", "turn_end"),
        preserve_event_payload_types=("turn_end",),
        minimum_lifecycle_event_counts=(("turn_start", 1), ("turn_end", 1)),
        expected_marker_order=("response:turn-prompt", "event:turn_start", "event:turn_end"),
    ),
)
R5_SCENARIOS["R5.message-role-lifecycle"] = (
    InputRun(
        chunks=((_line("roles-prompt", "prompt", message="run the role lifecycle tool"), 0.0),),
        expected_response_ids=("roles-prompt",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",), enable_bash_tool=True,
        provider_steps=(FixtureProviderStep(tool_name="bash", tool_arguments=json.dumps({"command": "echo role-result"})),
                        FixtureProviderStep(text_fragments=("tool result ", "received"), flush_stream_chunks=True,
                                            stream_chunk_delay_seconds=0.05)),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"roles-prompt"', 1, 10.0),
                      (0, '"type":"agent_settled"', 1, 20.0)),
        expected_min_provider_requests=2, compare_provider_requests=True,
        lifecycle_event_types=("message_start", "message_end", "turn_end"),
        preserve_event_payload_types=("message_start", "message_end", "turn_end"),
        minimum_lifecycle_event_counts=(("message_start", 4), ("message_end", 4), ("turn_end", 2)),
    ),
)
R5_SCENARIOS["R5.streaming-text-fragments"] = (
    InputRun(
        chunks=((_line("text-fragments", "prompt", message="preserve text fragments"), 0.0),),
        expected_response_ids=("text-fragments",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",),
        provider_steps=(FixtureProviderStep(text_fragments=("first ", "second"), flush_stream_chunks=True),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"text-fragments"', 1, 10.0),
                      (0, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("message_start", "message_update", "message_end"),
        preserve_message_update_deltas_only=True,
        preserve_event_payload_types=("message_start", "message_end"),
        minimum_lifecycle_event_counts=(("message_update", 4),),
    ),
)
R5_SCENARIOS["R5.streaming-reasoning-fragments"] = (
    InputRun(
        chunks=((_line("reasoning-fragments", "prompt", message="preserve reasoning fragments"), 0.0),),
        expected_response_ids=("reasoning-fragments",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",), reasoning=True,
        provider_steps=(FixtureProviderStep(reasoning_fragments=("think first ", "think second"),
                                            flush_stream_chunks=True),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"reasoning-fragments"', 1, 10.0),
                      (0, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("message_start", "message_update", "message_end"),
        preserve_message_update_deltas_only=True,
        preserve_event_payload_types=("message_start", "message_end"),
        minimum_lifecycle_event_counts=(("message_update", 4),),
    ),
)
R5_SCENARIOS["R5.cumulative-usage-and-final-message"] = (
    InputRun(
        chunks=((_line("cumulative-usage", "prompt", message="report cumulative provider usage"), 0.0),),
        expected_response_ids=("cumulative-usage",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",),
        provider_steps=(FixtureProviderStep(text_fragments=("usage ", "complete"),
                                            usage_updates=((8, 1, 9), (12, 2, 14)), flush_stream_chunks=True,
                                            stream_chunk_delay_seconds=0.05),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"cumulative-usage"', 1, 10.0),
                      (0, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("message_update", "message_end", "turn_end"),
        preserve_event_payload_types=("message_update", "message_end", "turn_end"),
        minimum_lifecycle_event_counts=(("message_end", 2), ("turn_end", 1)),
    ),
)
R5_SCENARIOS["R5.retry-event-order"] = (
    InputRun(
        chunks=((_line("retry-prompt", "prompt", message="retry once then complete"), 0.0),),
        expected_response_ids=("retry-prompt",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",),
        provider_steps=(FixtureProviderStep(status_code=503, error_message="503 transient fixture"),
                        FixtureProviderStep(text="retried successfully")),
        retry_settings=(("enabled", True), ("maxRetries", 1), ("baseDelayMs", 1), ("maxAgentDelayMs", 1)),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"retry-prompt"', 1, 10.0),
                      (0, '"type":"agent_settled"', 1, 20.0)),
        expected_min_provider_requests=2, compare_provider_requests=True,
        lifecycle_event_types=("auto_retry_start", "auto_retry_end", "agent_end", "agent_settled"),
        preserve_event_payload_types=("auto_retry_start", "auto_retry_end", "agent_settled"),
        minimum_lifecycle_event_counts=(("auto_retry_start", 1), ("auto_retry_end", 1),
                                        ("agent_end", 1), ("agent_settled", 1)),
    ),
)
R5_SCENARIOS["R5.post-content-failure"] = (
    InputRun(
        chunks=((_line("partial-failure", "prompt", message="emit content then fail"), 0.0),),
        expected_response_ids=("partial-failure",), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",),
        provider_steps=(FixtureProviderStep(text_fragments=("partial ", "content"), flush_stream_chunks=True,
                                            fail_after_partial=True),),
        retry_settings=(("enabled", False), ("maxRetries", 0)),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"partial-failure"', 1, 10.0),
                      (0, '"type":"agent_settled"', 1, 20.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        # Pi's SSE parser writes implementation-specific diagnostics to stderr; retain the
        # hashed raw streams while comparing the complete RPC error and lifecycle records.
        compare_stderr=False,
        lifecycle_event_types=("message_update", "message_end", "turn_end", "agent_end", "agent_settled"),
        preserve_message_update_deltas_only=True,
        preserve_event_payload_types=("message_end", "turn_end", "agent_end", "agent_settled"),
        minimum_lifecycle_event_counts=(("message_update", 3), ("message_end", 1),
                                        ("turn_end", 1), ("agent_end", 1), ("agent_settled", 1)),
    ),
)
R5_SCENARIOS["R5.cancellation-lifecycle"] = (
    InputRun(
        chunks=(
            (_line("cancel-lifecycle-prompt", "prompt", message="stream then cancel"), 0.0),
            (_line("cancel-lifecycle-abort", "abort"), 0.0),
        ),
        expected_response_ids=("cancel-lifecycle-prompt", "cancel-lifecycle-abort"),
        expected_response_commands=("prompt", "abort"), expected_successes=(True, True),
        expected_data_presence=(True, False), expected_error_presence=(False, False),
        expected_response_dispositions=("started", None),
        provider_steps=(FixtureProviderStep(text="partial response before abort", hold_after_partial=True),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"cancel-lifecycle-prompt"', 1, 10.0),
                      (0, '"partial response before abort"', 1, 10.0),
                      (1, '"type":"agent_settled"', 1, 20.0)),
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("message_update", "message_end", "turn_end", "agent_end", "agent_settled"),
        preserve_message_update_deltas_only=True,
        preserve_event_payload_types=("message_end", "turn_end", "agent_end", "agent_settled"),
        minimum_lifecycle_event_counts=(("message_update", 2), ("message_end", 2), ("turn_end", 1),
                                        ("agent_end", 1), ("agent_settled", 1)),
    ),
)
R5_SCENARIOS["R5.session-and-model-event-order"] = (
    InputRun(
        chunks=(
            (_line("rename-session", "set_session_name", name="R5 event ordering"), 0.0),
            (_line("set-thinking", "set_thinking_level", level="low"), 0.0),
        ),
        expected_response_ids=("rename-session", "set-thinking"),
        expected_response_commands=("set_session_name", "set_thinking_level"),
        expected_successes=(True, True), expected_data_presence=(False, False),
        expected_error_presence=(False, False),
        stdout_waits=((0, '"id":"rename-session"', 1, 10.0),
                      (1, '"id":"set-thinking"', 1, 10.0)),
        reasoning=True,
        lifecycle_event_types=("session_info_changed", "thinking_level_changed"),
        preserve_event_payloads=True,
        minimum_lifecycle_event_counts=(("session_info_changed", 1), ("thinking_level_changed", 1)),
        expected_marker_order=("event:session_info_changed", "response:rename-session",
                               "event:thinking_level_changed", "response:set-thinking"),
    ),
)
R5_SCENARIOS["R5.session-entry-event-payload"] = (
    InputRun(
        chunks=(
            (_line("append-entry-command", "prompt", message="/native-fixture-state-set stored"), 0.0),
            (_line("read-appended-entry", "get_entries"), 0.0),
        ),
        expected_response_ids=("append-entry-command", "read-appended-entry"),
        expected_response_commands=("prompt", "get_entries"), expected_successes=(True, True),
        expected_data_presence=(True, True), expected_error_presence=(False, False),
        expected_response_dispositions=("handled", None),
        stdout_waits=((0, '"id":"append-entry-command"', 1, 10.0),
                      (1, '"id":"read-appended-entry"', 1, 10.0)),
        lifecycle_event_types=("entry_appended",), preserve_event_payloads=True,
        preserve_response_data_commands=("get_entries",), normalize_session_entry_ids=True,
        use_entry_appending_extension=True,
        minimum_lifecycle_event_counts=(("entry_appended", 1),),
        expected_marker_order=("event:entry_appended", "response:append-entry-command",
                               "response:read-appended-entry"),
    ),
)

for _case_id, _runs in tuple(R5_SCENARIOS.items()):
    R5_SCENARIOS[_case_id] = tuple(replace(run, model_context_window=65_536) for run in _runs)


R6_SCENARIOS: dict[str, tuple[InputRun, ...]] = {}

_R6_EVENTS = (
    "agent_start", "message_start", "message_update", "message_end", "turn_start", "turn_end",
    "tool_execution_start", "tool_execution_update", "tool_execution_end", "agent_end", "agent_settled",
)
_R6_PAYLOAD_TYPES = (
    "message_update", "message_end", "turn_end", "tool_execution_start", "tool_execution_update",
    "tool_execution_end",
)

def _r6_case(case_id: str, message: str, provider_steps: tuple[FixtureProviderStep, ...], *,
             tool_names: tuple[str, ...] = ("rpc_parity_tool",),
             minimum_tool_starts: int = 1, minimum_tool_updates: int = 0,
             minimum_tool_ends: int | None = None, preserve_assistant_terminal_shape: bool = False,
             retry_settings: tuple[tuple[str, Any], ...] = ()) -> tuple[InputRun, ...]:
    if minimum_tool_ends is None:
        minimum_tool_ends = minimum_tool_starts
    return (InputRun(
        chunks=((_line(case_id, "prompt", message=message), 0.0),),
        expected_response_ids=(case_id,), expected_response_commands=("prompt",),
        expected_successes=(True,), expected_data_presence=(True,), expected_error_presence=(False,),
        expected_response_dispositions=("started",), active_tool_names=tool_names,
        use_rpc_tool_extension=True, provider_steps=provider_steps,
        retry_settings=retry_settings, expected_min_provider_requests=len(provider_steps),
        compare_provider_requests=True, lifecycle_event_types=_R6_EVENTS,
        preserve_assistant_terminal_shape=preserve_assistant_terminal_shape,
        preserve_event_payload_types=_R6_PAYLOAD_TYPES,
        minimum_lifecycle_event_counts=tuple(
            item for item in (("tool_execution_start", minimum_tool_starts),
                              ("tool_execution_update", minimum_tool_updates),
                              ("tool_execution_end", minimum_tool_ends)) if item[1] > 0),
        stdout_waits=((0, '"type":"agent_settled"', 1, 25.0),),
    ),)


R6_SCENARIOS["R6.tool-call-start-arguments"] = _r6_case(
    "R6.tool-call-start-arguments", "call the fixture tool",
    (FixtureProviderStep(tool_name="rpc_parity_tool", tool_arguments=_rpc_tool_arguments("start")),
     FixtureProviderStep(text="tool call complete")))
R6_SCENARIOS["R6.tool-argument-fragments"] = _r6_case(
    "R6.tool-argument-fragments", "stream the tool arguments",
    (FixtureProviderStep(
        tool_name="rpc_parity_tool", tool_arguments=_rpc_tool_arguments("fragmented-arguments"),
        tool_argument_fragments=('{"value":', '"fragmented-arguments","delayMs":0,"emitPartial":false,"fail":false}'),
        flush_stream_chunks=True, stream_chunk_delay_seconds=0.02),
     FixtureProviderStep(text="tool argument fragments complete")))
R6_SCENARIOS["R6.tool-call-end-authoritative"] = _r6_case(
    "R6.tool-call-end-authoritative", "complete the streamed tool call",
    (FixtureProviderStep(
        tool_name="rpc_parity_tool", tool_arguments=_rpc_tool_arguments("authoritative-final-arguments"),
        tool_argument_fragments=(' { "value" : ',
                                 '"authoritative-final-arguments", "delayMs":0, "emitPartial":false, "fail":false } '),
        flush_stream_chunks=True, stream_chunk_delay_seconds=0.02),
     FixtureProviderStep(text="authoritative tool call complete")))
R6_SCENARIOS["R6.tool-execution-start-update-end"] = _r6_case(
    "R6.tool-execution-start-update-end", "report partial tool output",
    (FixtureProviderStep(tool_name="rpc_parity_tool",
                         tool_arguments=_rpc_tool_arguments("progress", delay_ms=200, emit_partial=True)),
     FixtureProviderStep(text="tool progress completed")), minimum_tool_updates=1)
R6_SCENARIOS["R6.sequential-tool-continuation"] = _r6_case(
    "R6.sequential-tool-continuation", "call two tools in sequence",
    (FixtureProviderStep(tool_name="rpc_parity_tool", tool_arguments=_rpc_tool_arguments("first")),
     FixtureProviderStep(tool_name="rpc_parity_tool", tool_arguments=_rpc_tool_arguments("second")),
     FixtureProviderStep(text="both sequential tools completed")), minimum_tool_starts=2)
R6_SCENARIOS["R6.parallel-tool-continuation"] = _r6_case(
    "R6.parallel-tool-continuation", "call two tools in parallel",
    (FixtureProviderStep(tool_calls=(
        ("rpc_parity_tool", _rpc_tool_arguments("parallel-fast")),
        ("rpc_parity_tool", _rpc_tool_arguments("parallel-slow", delay_ms=200)),
    )), FixtureProviderStep(text="both parallel tools completed")), minimum_tool_starts=2)
R6_SCENARIOS["R6.tool-error-continuation"] = _r6_case(
    "R6.tool-error-continuation", "continue after a tool error",
    (FixtureProviderStep(tool_name="rpc_parity_tool", tool_arguments=_rpc_tool_arguments("failure", fail=True)),
     FixtureProviderStep(text="tool error was reported and continued")))
R6_SCENARIOS["R6.provider-failure-after-tool"] = _r6_case(
    "R6.provider-failure-after-tool", "fail provider after successful tool",
    (FixtureProviderStep(tool_name="rpc_parity_tool", tool_arguments=_rpc_tool_arguments("once")),
     FixtureProviderStep(status_code=503, error_message="fixture provider failed after tool")),
    preserve_assistant_terminal_shape=True, retry_settings=(("enabled", False), ("maxRetries", 0)))
R6_SCENARIOS["R6.partial-assistant-with-tool"] = _r6_case(
    "R6.partial-assistant-with-tool", "mix assistant text and a tool call",
    (FixtureProviderStep(text_fragments=("assistant text before tool",),
                         tool_name="rpc_parity_tool", tool_arguments=_rpc_tool_arguments("mixed-content"),
                         flush_stream_chunks=True, stream_chunk_delay_seconds=0.02),
     FixtureProviderStep(text="mixed assistant and tool call complete")))
R6_SCENARIOS["R6.tool-call-id-and-parent-fields"] = _r6_case(
    "R6.tool-call-id-and-parent-fields", "execute one nested tool call",
    (FixtureProviderStep(tool_name="rpc_nested_orchestrator", tool_arguments="{}"),
     FixtureProviderStep(text="nested tool call complete")),
    tool_names=("rpc_parity_tool", "rpc_nested_orchestrator"), minimum_tool_starts=2)

for _case_id, _runs in tuple(R6_SCENARIOS.items()):
    R6_SCENARIOS[_case_id] = tuple(
        replace(run, model_context_window=65_536,
                provider_steps=tuple(replace(step, include_usage=False) for step in run.provider_steps))
        for run in _runs
    )


def _r7_fixture(entries: tuple[dict[str, Any], ...], *, session_id: str = "rpc-r7-session") -> str:
    records = [
        {
            "type": "session", "version": 3, "id": session_id,
            "timestamp": "2025-01-01T00:00:00.000Z", "cwd": "/tmp",
            "provider": "fixture", "modelId": "fixture-model", "thinkingLevel": "off",
        },
        *entries,
    ]
    if not any(entry.get("type") == "thinking_level_change" for entry in entries):
        parent_id = entries[-1].get("id") if entries else None
        records.append({
            "type": "thinking_level_change", "id": "r7-thinking", "parentId": parent_id,
            "timestamp": "2025-01-01T00:10:00.000Z", "thinkingLevel": "off",
        })
    return "".join(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n" for record in records)


def _r7_message(entry_id: str, parent_id: str | None, role: str, text: str, *, minute: int,
                assistant_usage: bool = True) -> dict[str, Any]:
    message: dict[str, Any] = {
        "role": role,
        "content": [{"type": "text", "text": text}],
        "timestamp": 1735689600000 + minute * 60_000,
    }
    if role == "assistant":
        message.update({
            "provider": "fixture", "model": "fixture-model", "api": "openai-completions",
            "stopReason": "stop",
        })
        if assistant_usage:
            message["usage"] = {
                "input": 12, "output": 2, "cacheRead": 0, "cacheWrite": 0,
                "totalTokens": 14,
                "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0, "total": 0},
            }
    return {
        "type": "message", "id": entry_id, "parentId": parent_id,
        "timestamp": f"2025-01-01T00:{minute:02d}:00.000Z", "message": message,
    }


_R7_LINEAR = _r7_fixture((
    _r7_message("r7-u1", None, "user", "first question", minute=1),
    _r7_message("r7-a1", "r7-u1", "assistant", "first answer", minute=2),
))
_R7_BRANCHES = _r7_fixture((
    _r7_message("r7-u1", None, "user", "root question", minute=1),
    _r7_message("r7-a1", "r7-u1", "assistant", "first answer", minute=2),
    _r7_message("r7-u2", "r7-a1", "user", "first branch question", minute=3),
    _r7_message("r7-a2", "r7-u2", "assistant", "first branch answer", minute=4),
    _r7_message("r7-u3", "r7-u1", "user", "selected branch question", minute=5),
    _r7_message("r7-a3", "r7-u3", "assistant", "selected branch answer", minute=6),
    {
        "type": "label", "id": "r7-label", "parentId": "r7-a3",
        "timestamp": "2025-01-01T00:07:00.000Z", "targetId": "r7-a3", "label": "selected",
    },
))
_R7_COMPACTED = _r7_fixture((
    _r7_message("r7-old-user", None, "user", "old question", minute=1),
    _r7_message("r7-old-assistant", "r7-old-user", "assistant", "old answer", minute=2),
    _r7_message("r7-kept-user", "r7-old-assistant", "user", "retained question", minute=3),
    _r7_message("r7-edited-assistant", "r7-kept-user", "assistant", "original answer", minute=4),
    {
        "type": "compaction", "id": "r7-compaction", "parentId": "r7-edited-assistant",
        "timestamp": "2025-01-01T00:05:00.000Z", "summary": "summary of old turn",
        "firstKeptEntryId": "r7-kept-user", "tokensBefore": 1234,
    },
    {
        "type": "context_edit", "id": "r7-context-edit", "parentId": "r7-compaction",
        "timestamp": "2025-01-01T00:06:00.000Z", "targetId": "r7-edited-assistant",
        "replacement": {"content": "edited answer"},
    },
))

R7_SCENARIOS: dict[str, tuple[InputRun, ...]] = {}
R7_SCENARIOS["R7.get-state-idle-schema"] = _one(
    _line("idle-state", "get_state"), expected_ids=("idle-state",), response_only=True,
    preserve_response_data_commands=("get_state",), preserve_session_identity=True,
)
R7_SCENARIOS["R7.get-state-active-stream"] = (
    InputRun(
        chunks=(
            (_line("active-prompt", "prompt", message="hold the first turn"), 0.0),
            (_line("active-queue", "prompt", message="queue while active", streamingBehavior="followUp"), 0.0),
            (_line("active-state", "get_state"), 0.0),
        ),
        expected_response_ids=("active-prompt", "active-queue", "active-state"),
        expected_response_commands=("prompt", "prompt", "get_state"),
        expected_successes=(True, True, True), expected_data_presence=(True, True, True),
        expected_response_dispositions=("started", "queued", None),
        provider_steps=(FixtureProviderStep(hold_before_response=True, text="first turn"),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"active-prompt"', 1, 10.0),
                      (1, '"id":"active-queue"', 1, 10.0),
                      (2, '"id":"active-state"', 1, 10.0)),
        release_provider_after_input_index=2, expected_min_provider_requests=1,
        response_only=True, preserve_response_data_commands=("get_state",), preserve_session_identity=True,
        state_expectations=(("active-state", "isStreaming", True),
                            ("active-state", "pendingMessageCount", 1)),
        model_context_window=65_536,
    ),
)
R7_SCENARIOS["R7.get-messages-system-projection"] = (
    InputRun(
        chunks=(
            (_line("system-prompt", "prompt", message="start generation of system sections"), 0.0),
            (_line("system-messages", "get_messages"), 0.0),
            (_line("system-abort", "abort"), 0.0),
        ),
        expected_response_ids=("system-prompt", "system-messages", "system-abort"),
        expected_response_commands=("prompt", "get_messages", "abort"),
        expected_successes=(True, True, True), expected_data_presence=(True, True, False),
        provider_steps=(FixtureProviderStep(hold_before_response=True, text="unused response"),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"system-prompt"', 1, 10.0),
                      (1, '"id":"system-messages"', 1, 10.0),
                      (2, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, response_only=True,
        preserve_response_data_commands=("get_messages",), model_context_window=65_536,
    ),
)
R7_SCENARIOS["R7.get-messages-compacted-context-edits"] = (
    InputRun(
        chunks=((_line("compacted-messages", "get_messages"), 0.0),),
        expected_response_ids=("compacted-messages",), expected_successes=(True,), response_only=True,
        preserve_response_data_commands=("get_messages",), session_fixture_jsonl=_R7_COMPACTED,
    ),
)
R7_SCENARIOS["R7.get-messages-active-partial"] = (
    InputRun(
        chunks=(
            (_line("partial-prompt", "prompt", message="hold a partial assistant stream"), 0.0),
            (_line("partial-messages", "get_messages"), 0.0),
            (_line("partial-abort", "abort"), 0.0),
        ),
        expected_response_ids=("partial-prompt", "partial-messages", "partial-abort"),
        expected_response_commands=("prompt", "get_messages", "abort"),
        expected_successes=(True, True, True), expected_data_presence=(True, True, False),
        provider_steps=(FixtureProviderStep(text="partial-only text", hold_after_partial=True),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"partial-prompt"', 1, 10.0),
                      (0, "partial-only text", 1, 10.0),
                      (1, '"id":"partial-messages"', 1, 10.0),
                      (2, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, lifecycle_event_types=("message_update", "agent_settled"),
        preserve_message_update_deltas_only=True, preserve_response_data_commands=("get_messages",),
        model_context_window=65_536,
    ),
)
R7_SCENARIOS["R7.get-entries-full-and-leaf"] = (
    InputRun(
        chunks=((_line("linear-entries", "get_entries"), 0.0),),
        expected_response_ids=("linear-entries",), expected_successes=(True,), response_only=True,
        preserve_response_data_commands=("get_entries",), session_fixture_jsonl=_R7_LINEAR,
    ),
    InputRun(
        chunks=((_line("branch-entries", "get_entries"), 0.0),),
        expected_response_ids=("branch-entries",), expected_successes=(True,), response_only=True,
        preserve_response_data_commands=("get_entries",), session_fixture_jsonl=_R7_BRANCHES,
    ),
)
R7_SCENARIOS["R7.get-entries-cursor-boundaries"] = (
    InputRun(
        chunks=((
            _line("cursor-full", "get_entries") +
            _line("cursor-first", "get_entries", since="r7-u1") +
            _line("cursor-last", "get_entries", since="r7-thinking") +
            _line("cursor-missing", "get_entries", since="missing-entry") +
            _line("cursor-null", "get_entries", since=None) +
            b'{"id":"cursor-number","type":"get_entries","since":7}\n' +
            _line("cursor-recovery", "get_entries", since="r7-a1"),
            0.0,
        ),),
        expected_response_ids=("cursor-full", "cursor-first", "cursor-last", "cursor-missing",
                               "cursor-null", "cursor-number", "cursor-recovery"),
        expected_response_commands=("get_entries",) * 7,
        expected_successes=(True, True, True, False, False, False, True),
        response_only=True, preserve_response_data_commands=("get_entries",),
        preserve_response_error_text=True, session_fixture_jsonl=_R7_LINEAR,
    ),
)
R7_SCENARIOS["R7.get-tree-branches-and-labels"] = (
    InputRun(
        chunks=((_line("branch-tree", "get_tree"), 0.0),),
        expected_response_ids=("branch-tree",), expected_successes=(True,), response_only=True,
        preserve_response_data_commands=("get_tree",), session_fixture_jsonl=_R7_BRANCHES,
    ),
)
R7_SCENARIOS["R7.get-fork-messages-content"] = (
    InputRun(
        chunks=((_line("fork-messages", "get_fork_messages"), 0.0),),
        expected_response_ids=("fork-messages",), expected_successes=(True,), response_only=True,
        preserve_response_data_commands=("get_fork_messages",), session_fixture_jsonl=_R7_BRANCHES,
    ),
)
R7_SCENARIOS["R7.get-last-assistant-text-values"] = tuple(
    InputRun(
        chunks=((_line(f"last-text-{name}", "get_last_assistant_text"), 0.0),),
        expected_response_ids=(f"last-text-{name}",), expected_successes=(True,), response_only=True,
        preserve_response_data_commands=("get_last_assistant_text",), session_fixture_jsonl=fixture,
    )
    for name, fixture in (
        ("empty", _r7_fixture(())),
        ("text", _R7_LINEAR),
        ("tool-only", _r7_fixture((
            _r7_message("r7-tool-user", None, "user", "call a tool", minute=1),
            {
                "type": "message", "id": "r7-tool-assistant", "parentId": "r7-tool-user",
                "timestamp": "2025-01-01T00:02:00.000Z",
                "message": {"role": "assistant", "content": [{"type": "toolCall", "id": "tool-1",
                    "name": "fixture_tool", "arguments": {}}], "provider": "fixture",
                    "model": "fixture-model", "api": "openai-completions", "stopReason": "toolUse"},
            },
        ))),
        ("branch", _R7_BRANCHES),
    )
)
R7_SCENARIOS["R7.session-read-during-provider-work"] = (
    InputRun(
        chunks=(
            (_line("read-active-prompt", "prompt", message="blocked read snapshot"), 0.0),
            (_line("read-active-queue", "prompt", message="queued read snapshot", streamingBehavior="followUp"), 0.0),
            (_line("read-active-state", "get_state"), 0.0),
            (_line("read-active-messages", "get_messages"), 0.0),
            (_line("read-active-entries", "get_entries"), 0.0),
            (_line("read-active-tree", "get_tree"), 0.0),
            (_line("read-active-fork", "get_fork_messages"), 0.0),
            (_line("read-active-last", "get_last_assistant_text"), 0.0),
        ),
        expected_response_ids=("read-active-prompt", "read-active-queue", "read-active-state",
                               "read-active-messages", "read-active-entries", "read-active-tree",
                               "read-active-fork", "read-active-last"),
        expected_response_commands=("prompt", "prompt", "get_state", "get_messages", "get_entries",
                                    "get_tree", "get_fork_messages", "get_last_assistant_text"),
        expected_successes=(True,) * 8,
        provider_steps=(FixtureProviderStep(hold_before_response=True, text="finish first"),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=tuple((index, f'"id":"{request_id}"', 1, 10.0)
                           for index, request_id in enumerate((
                               "read-active-prompt", "read-active-queue", "read-active-state",
                               "read-active-messages", "read-active-entries", "read-active-tree",
                               "read-active-fork", "read-active-last"))),
        release_provider_after_input_index=7, expected_min_provider_requests=1,
        response_only=True,
        preserve_response_data_commands=("get_state", "get_messages", "get_entries", "get_tree",
                                         "get_fork_messages", "get_last_assistant_text"),
        normalize_session_entry_ids=True, preserve_session_identity=True,
        state_expectations=(("read-active-state", "isStreaming", True),
                            ("read-active-state", "pendingMessageCount", 1)),
        model_context_window=65_536,
    ),
)
R7_SCENARIOS["R7.session-read-after-reload"] = (
    InputRun(
        chunks=((_line("native-seed", "prompt", message="persist native session"), 0.0),),
        expected_response_ids=("native-seed",), expected_successes=(True,),
        provider_steps=(FixtureProviderStep(text="native persisted answer"),),
        provider_waits=((0, 1, 10.0),),
        stdout_waits=((0, '"id":"native-seed"', 1, 10.0),
                      (0, '"type":"agent_settled"', 1, 15.0)),
        expected_min_provider_requests=1, response_only=True,
        model_context_window=65_536,
    ),
    InputRun(
        chunks=((_line("native-reloaded", "get_state") + _line("native-messages", "get_messages") +
                 _line("native-entries", "get_entries"), 0.0),),
        expected_response_ids=("native-reloaded", "native-messages", "native-entries"),
        expected_successes=(True, True, True), response_only=True,
        preserve_response_data_commands=("get_state", "get_messages", "get_entries"),
        normalize_session_entry_ids=True, preserve_session_identity=True,
        resume_session=True, reuse_workspace_previous=True, model_context_window=65_536,
    ),
    InputRun(
        chunks=((_line("jsonl-seed-state", "get_state"), 0.0),),
        expected_response_ids=("jsonl-seed-state",), expected_successes=(True,), response_only=True,
        preserve_response_data_commands=("get_state",), preserve_session_identity=True,
        session_fixture_jsonl=_R7_BRANCHES, model_context_window=65_536,
    ),
    InputRun(
        chunks=((_line("jsonl-reloaded", "get_state") + _line("jsonl-messages", "get_messages") +
                 _line("jsonl-entries", "get_entries"), 0.0),),
        expected_response_ids=("jsonl-reloaded", "jsonl-messages", "jsonl-entries"),
        expected_successes=(True, True, True), response_only=True,
        preserve_response_data_commands=("get_state", "get_messages", "get_entries"),
        normalize_session_entry_ids=True, preserve_session_identity=True,
        resume_session=True, reuse_workspace_previous=True, model_context_window=65_536,
    ),
)
R7_SCENARIOS["R7.read-command-error-cursors"] = (
    InputRun(
        chunks=((_line("invalid-cursor", "get_entries", since="missing") +
                 _line("valid-after-error", "get_entries", since="r7-u1") +
                 _line("messages-after-error", "get_messages"), 0.0),),
        expected_response_ids=("invalid-cursor", "valid-after-error", "messages-after-error"),
        expected_response_commands=("get_entries", "get_entries", "get_messages"),
        expected_successes=(False, True, True), response_only=True,
        preserve_response_error_text=True,
        preserve_response_data_commands=("get_entries", "get_messages"),
        session_fixture_jsonl=_R7_LINEAR,
    ),
)


def _r8_usage(input_tokens: int, output_tokens: int, cache_read: int, cache_write: int,
              input_cost: float, output_cost: float, cache_read_cost: float,
              cache_write_cost: float) -> dict[str, Any]:
    return {
        "input": input_tokens,
        "output": output_tokens,
        "cacheRead": cache_read,
        "cacheWrite": cache_write,
        "totalTokens": input_tokens + output_tokens + cache_read + cache_write,
        "cost": {
            "input": input_cost,
            "output": output_cost,
            "cacheRead": cache_read_cost,
            "cacheWrite": cache_write_cost,
            "total": input_cost + output_cost + cache_read_cost + cache_write_cost,
        },
    }


def _r8_native_usage(usage: dict[str, Any]) -> dict[str, Any]:
    return {
        "Model": "fixture-model",
        "Source": "branch_summary",
        "InputTokens": usage["input"],
        "OutputTokens": usage["output"],
        "CachedInputTokens": usage["cacheRead"],
        "ReasoningTokens": 0,
        "TotalTokens": usage["totalTokens"],
        "Cost": usage["cost"]["total"],
        "CachedWriteTokens": usage["cacheWrite"],
        "InputIncludingCachedTokens": usage["input"] + usage["cacheRead"],
    }


def _r8_pi_summary(entry_id: str, parent_id: str | None, usage: dict[str, Any] | None) -> dict[str, Any]:
    entry: dict[str, Any] = {
        "type": "branch_summary", "id": entry_id, "parentId": parent_id,
        "timestamp": "2025-01-01T00:01:00.000Z", "fromId": "root", "summary": "fixture summary",
    }
    if usage is not None:
        entry["usage"] = usage
    return entry


def _r8_native_summary(entry_id: str, parent_id: str | None, usage: dict[str, Any] | None) -> dict[str, Any]:
    payload: dict[str, Any] = {"fromId": "root", "summary": "fixture summary"}
    if usage is not None:
        payload["usage"] = usage
        payload["nativeUsage"] = _r8_native_usage(usage)
    return {
        "Id": entry_id, "ParentId": parent_id, "Type": "branch_summary", "Payload": payload,
        "Timestamp": "2025-01-01T00:01:00.000Z",
    }


def _r8_pi_fixture(entries: tuple[dict[str, Any], ...]) -> str:
    records = [{
        "type": "session", "version": 3, "id": "rpc-r8-session",
        "timestamp": "2025-01-01T00:00:00.000Z", "cwd": "/tmp",
        "provider": "fixture", "modelId": "fixture-model", "thinkingLevel": "off",
    }, *entries]
    return "".join(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n" for record in records)


def _r8_native_fixture(entries: tuple[dict[str, Any], ...]) -> str:
    document = {
        "Version": 2, "Id": "rpc-r8-session", "WorkingDirectory": "/tmp", "Model": "fixture-model",
        "Endpoint": None, "Name": None, "HeadId": entries[-1]["Id"] if entries else None,
        "Entries": entries, "Provider": "fixture", "PiHeader": None, "ParentSessionPath": None,
    }
    return json.dumps(document, ensure_ascii=False, separators=(",", ":")) + "\n"


def _r8_stats_run(request_id: str, pi_entries: tuple[dict[str, Any], ...],
                  native_entries: tuple[dict[str, Any], ...] | None, *,
                  preserve_session_identity: bool = True) -> InputRun:
    return InputRun(
        chunks=((_line(request_id, "get_session_stats"), 0.0),),
        expected_response_ids=(request_id,), expected_response_commands=("get_session_stats",),
        expected_successes=(True,), expected_data_presence=(True,), response_only=True,
        preserve_response_data_commands=("get_session_stats",),
        preserve_session_identity=preserve_session_identity,
        session_fixture_jsonl=_r8_pi_fixture(pi_entries),
        session_fixture_native_json=None if native_entries is None else _r8_native_fixture(native_entries),
        model_context_window=4096, bind_fixture_working_directory=True,
    )


R8_SCENARIOS: dict[str, tuple[InputRun, ...]] = {}
_R8_GENERATED_USAGE = _r8_usage(17, 3, 5, 2, 0.000034, 0.000009, 0.000005, 0.000008)
R8_SCENARIOS["R8.native-summary-generation"] = (
    _r8_stats_run("native-summary-stats",
                  (_r8_pi_summary("native-summary", None, _R8_GENERATED_USAGE),),
                  (_r8_native_summary("native-summary", None, _R8_GENERATED_USAGE),)),
)

R8_SCENARIOS["R8.native-summary-persisted-reload"] = (
    InputRun(
        chunks=((_line("summary-seed", "set_session_name", name="persisted summary"), 0.0),),
        expected_response_ids=("summary-seed",), expected_response_commands=("set_session_name",),
        expected_successes=(True,), response_only=True,
        session_fixture_jsonl=_r8_pi_fixture((_r8_pi_summary("native-summary", None, _R8_GENERATED_USAGE),)),
        session_fixture_native_json=_r8_native_fixture(
            (_r8_native_summary("native-summary", None, _R8_GENERATED_USAGE),)),
        model_context_window=4096, bind_fixture_working_directory=True,
    ),
    InputRun(
        chunks=((_line("summary-after-reload", "get_session_stats"), 0.0),),
        expected_response_ids=("summary-after-reload",), expected_response_commands=("get_session_stats",),
        expected_successes=(True,), expected_data_presence=(True,), response_only=True,
        preserve_response_data_commands=("get_session_stats",), preserve_session_identity=True,
        reuse_session_fixture_path=True, reuse_workspace_previous=True, model_context_window=4096,
    ),
)

_R8_IMPORTED_USAGE = _r8_usage(7, 4, 2, 3, 0.000014, 0.000012, 0.000002, 0.000012)
R8_SCENARIOS["R8.imported-pi-summary"] = (
    _r8_stats_run("imported-summary-stats",
                  (_r8_pi_summary("imported-summary", None, _R8_IMPORTED_USAGE),), None),
)

_R8_MIXED_NATIVE_USAGE = _r8_usage(5, 2, 3, 1, 0.000010, 0.000006, 0.000003, 0.000003)
_R8_MIXED_IMPORTED_USAGE = _r8_usage(7, 4, 2, 3, 0.000014, 0.000012, 0.000002, 0.000012)
_R8_MIXED_IMPORTED_PI_ENTRY = _r8_pi_summary("mixed-imported", "mixed-native", _R8_MIXED_IMPORTED_USAGE)
R8_SCENARIOS["R8.mixed-native-imported-summaries"] = (
    _r8_stats_run("mixed-summary-stats",
                  (_r8_pi_summary("mixed-native", None, _R8_MIXED_NATIVE_USAGE), _R8_MIXED_IMPORTED_PI_ENTRY),
                  (
                      _r8_native_summary("mixed-native", None, _R8_MIXED_NATIVE_USAGE),
                      {
                          "Id": "mixed-imported", "ParentId": "mixed-native", "Type": "branch_summary",
                          "Payload": {"piOriginalEntry": _R8_MIXED_IMPORTED_PI_ENTRY},
                          "Timestamp": "2025-01-01T00:02:00.000Z",
                      },
                  )),
)

_R8_ALL_FIELDS_USAGE = _r8_usage(31, 7, 11, 13, 0.000062, 0.000021, 0.000011, 0.000065)
R8_SCENARIOS["R8.cost-and-token-fields"] = (
    _r8_stats_run("all-usage-fields-stats",
                  (_r8_pi_summary("all-usage-fields", None, _R8_ALL_FIELDS_USAGE),),
                  (_r8_native_summary("all-usage-fields", None, _R8_ALL_FIELDS_USAGE),)),
)

_R8_ZERO_USAGE = _r8_usage(0, 0, 0, 0, 0, 0, 0, 0)
R8_SCENARIOS["R8.empty-and-zero-usage"] = (
    _r8_stats_run("empty-session-stats", (), (), preserve_session_identity=False),
    _r8_stats_run("zero-summary-stats",
                  (_r8_pi_summary("zero-summary", None, _R8_ZERO_USAGE),),
                  (_r8_native_summary("zero-summary", None, _R8_ZERO_USAGE),),
                  preserve_session_identity=False),
    _r8_stats_run("absent-summary-stats",
                  (_r8_pi_summary("absent-summary", None, None),),
                  (_r8_native_summary("absent-summary", None, None),),
                  preserve_session_identity=False),
)

def _r9_model(model_id: str, *, name: str | None = None, reasoning: bool = False,
              thinking_level_map: dict[str, str | None] | None = None) -> dict[str, Any]:
    model: dict[str, Any] = {
        "id": model_id, "name": name or model_id, "api": "openai-completions",
        "reasoning": reasoning, "input": ["text"], "contextWindow": 8192, "maxTokens": 2048,
        "cost": {"input": 1.25, "output": 4, "cacheRead": 0.2, "cacheWrite": 0.5},
    }
    if thinking_level_map is not None:
        model["thinkingLevelMap"] = thinking_level_map
    return model


_R9_BASIC_MODELS = (
    _r9_model("model-one", name="Model One", reasoning=True),
    _r9_model("model-two", name="Model Two", reasoning=True),
    _r9_model("model-three", name="Model Three", reasoning=True),
)
_R9_THINKING_EVENTS = ("thinking_level_changed",)
R9_SCENARIOS: dict[str, tuple[InputRun, ...]] = {}

R9_SCENARIOS["R9.available-model-schema-order"] = (
    InputRun(
        chunks=((_line("models", "get_available_models"), 0.0),),
        expected_response_ids=("models",), expected_response_commands=("get_available_models",),
        expected_successes=(True,), expected_data_presence=(True,), response_only=True,
        preserve_response_data_commands=("get_available_models",), model_catalog=_R9_BASIC_MODELS,
        initial_model_id="model-one", model_scope=("fixture/model-three", "fixture/model-one"),
    ),
)

R9_SCENARIOS["R9.set-model-exact-and-invalid"] = (
    InputRun(
        chunks=(
            (_line("select-exact", "set_model", provider="fixture", modelId="model-two"), 0.0),
            (_line("after-exact", "get_state"), 0.0),
            (_line("select-missing", "set_model", provider="fixture", modelId="missing"), 0.0),
            (_line("after-invalid", "get_state"), 0.0),
        ),
        expected_response_ids=("select-exact", "after-exact", "select-missing", "after-invalid"),
        expected_response_commands=("set_model", "get_state", "set_model", "get_state"),
        expected_successes=(True, True, False, True), expected_data_presence=(True, True, False, True),
        expected_error_presence=(False, False, True, False), preserve_response_error_text=True,
        preserve_response_data_commands=("set_model", "get_state"),
        stdout_waits=((0, '"id":"select-exact"', 1, 10.0),
                      (1, '"id":"after-exact"', 1, 10.0),
                      (2, '"id":"select-missing"', 1, 10.0),
                      (3, '"id":"after-invalid"', 1, 10.0)),
        state_expectations=(("after-exact", "thinkingLevel", "low"),
                            ("after-invalid", "thinkingLevel", "low")),
        model_catalog=_R9_BASIC_MODELS[:2], initial_model_id="model-one",
        settings_fixture={"defaultThinkingLevel": "low"},
    ),
)

R9_SCENARIOS["R9.cycle-model-order-and-null"] = (
    InputRun(
        chunks=((_line("cycle-scoped", "cycle_model"), 0.0),
                (_line("cycle-state", "get_state"), 0.0)),
        expected_response_ids=("cycle-scoped", "cycle-state"),
        expected_response_commands=("cycle_model", "get_state"), expected_successes=(True, True),
        expected_data_presence=(True, True), preserve_response_data_commands=("cycle_model", "get_state"),
        lifecycle_event_types=_R9_THINKING_EVENTS, preserve_event_payloads=True,
        stdout_waits=((0, '"id":"cycle-scoped"', 1, 10.0), (1, '"id":"cycle-state"', 1, 10.0)),
        state_expectations=(("cycle-state", "thinkingLevel", "high"),),
        model_catalog=_R9_BASIC_MODELS[:2], initial_model_id="model-one",
        model_scope=("fixture/model-two:high", "fixture/model-one:low"),
        settings_fixture={"defaultThinkingLevel": "low"},
    ),
    InputRun(
        chunks=((_line("cycle-single", "cycle_model"), 0.0),),
        expected_response_ids=("cycle-single",), expected_response_commands=("cycle_model",),
        expected_successes=(True,), expected_data_presence=(True,), response_only=True,
        preserve_response_data_commands=("cycle_model",),
        model_catalog=_R9_BASIC_MODELS[:1], initial_model_id="model-one",
    ),
    InputRun(
        chunks=((_line("cycle-empty", "cycle_model"), 0.0),),
        expected_response_ids=("cycle-empty",), expected_response_commands=("cycle_model",),
        expected_successes=(True,), expected_data_presence=(True,), response_only=True,
        preserve_response_data_commands=("cycle_model",), model_catalog=(), omit_cli_model_selection=True,
    ),
)

R9_SCENARIOS["R9.scoped-thinking-suffix"] = (
    InputRun(
        chunks=((_line("scope-before", "get_state"), 0.0),
                (_line("scope-cycle", "cycle_model"), 0.0),
                (_line("scope-after", "get_state"), 0.0)),
        expected_response_ids=("scope-before", "scope-cycle", "scope-after"),
        expected_response_commands=("get_state", "cycle_model", "get_state"),
        expected_successes=(True, True, True), expected_data_presence=(True, True, True),
        preserve_response_data_commands=("get_state", "cycle_model"),
        stdout_waits=((0, '"id":"scope-before"', 1, 10.0),
                      (1, '"id":"scope-cycle"', 1, 10.0),
                      (2, '"id":"scope-after"', 1, 10.0)),
        state_expectations=(("scope-before", "thinkingLevel", "low"),
                            ("scope-after", "thinkingLevel", "high")),
        model_catalog=_R9_BASIC_MODELS[:2], initial_model_id="model-one",
        model_scope=("fixture/model-one:low", "fixture/model-two:high"), omit_cli_model_selection=True,
        settings_fixture={"defaultThinkingLevel": "medium"},
    ),
)

R9_SCENARIOS["R9.per-model-thinking-defaults"] = (
    InputRun(
        chunks=((_line("defaults-before", "get_state"), 0.0),
                (_line("defaults-select", "set_model", provider="fixture", modelId="model-two"), 0.0),
                (_line("defaults-after", "get_state"), 0.0)),
        expected_response_ids=("defaults-before", "defaults-select", "defaults-after"),
        expected_response_commands=("get_state", "set_model", "get_state"),
        expected_successes=(True, True, True), expected_data_presence=(True, True, True),
        preserve_response_data_commands=("get_state", "set_model"),
        stdout_waits=((0, '"id":"defaults-before"', 1, 10.0),
                      (1, '"id":"defaults-select"', 1, 10.0),
                      (2, '"id":"defaults-after"', 1, 10.0)),
        lifecycle_event_types=_R9_THINKING_EVENTS, preserve_event_payloads=True,
        state_expectations=(("defaults-before", "thinkingLevel", "low"),
                            ("defaults-after", "thinkingLevel", "high")),
        model_catalog=_R9_BASIC_MODELS[:2], initial_model_id="model-one",
        settings_fixture={"defaultThinkingLevel": "minimal",
                          "modelThinkingLevels": {"fixture/model-one": "low", "fixture/model-two": "high"}},
    ),
)

_R9_SPARSE_MAP = {"off": None, "high": None, "xhigh": None, "max": "max"}
_R9_SPARSE_MODEL = _r9_model("sparse-model", name="Sparse Model", reasoning=True,
                             thinking_level_map=_R9_SPARSE_MAP)

R9_SCENARIOS["R9.supported-and-unsupported-levels"] = (
    InputRun(
        chunks=((_line("levels", "get_available_thinking_levels"), 0.0),
                (_line("set-high", "set_thinking_level", level="high"), 0.0),
                (_line("high-state", "get_state"), 0.0),
                (_line("set-off", "set_thinking_level", level="off"), 0.0),
                (_line("off-state", "get_state"), 0.0)),
        expected_response_ids=("levels", "set-high", "high-state", "set-off", "off-state"),
        expected_response_commands=("get_available_thinking_levels", "set_thinking_level", "get_state",
                                    "set_thinking_level", "get_state"),
        expected_successes=(True,) * 5, expected_data_presence=(True, False, True, False, True),
        preserve_response_data_commands=("get_available_thinking_levels", "get_state"),
        lifecycle_event_types=_R9_THINKING_EVENTS, preserve_event_payloads=True,
        stdout_waits=((0, '"id":"levels"', 1, 10.0), (1, '"id":"set-high"', 1, 10.0),
                      (2, '"id":"high-state"', 1, 10.0), (3, '"id":"set-off"', 1, 10.0),
                      (4, '"id":"off-state"', 1, 10.0)),
        state_expectations=(("high-state", "thinkingLevel", "max"),
                            ("off-state", "thinkingLevel", "minimal")),
        model_catalog=(_R9_SPARSE_MODEL,), initial_model_id="sparse-model",
        settings_fixture={"defaultThinkingLevel": "low"},
    ),
)

R9_SCENARIOS["R9.sparse-thinking-level-map"] = (
    InputRun(
        chunks=((_line("sparse-levels", "get_available_thinking_levels"), 0.0),
                (_line("sparse-cycle", "cycle_thinking_level"), 0.0),
                (_line("sparse-state", "get_state"), 0.0),
                (_line("sparse-persist-prompt", "prompt", message="persist sparse thinking level"), 0.0)),
        expected_response_ids=("sparse-levels", "sparse-cycle", "sparse-state", "sparse-persist-prompt"),
        expected_response_commands=("get_available_thinking_levels", "cycle_thinking_level", "get_state", "prompt"),
        expected_successes=(True, True, True, True), expected_data_presence=(True, True, True, True),
        preserve_response_data_commands=("get_available_thinking_levels", "cycle_thinking_level", "get_state"),
        lifecycle_event_types=(*_R9_THINKING_EVENTS, "agent_settled"), preserve_event_payloads=True,
        stdout_waits=((0, '"id":"sparse-levels"', 1, 10.0),
                      (1, '"id":"sparse-cycle"', 1, 10.0),
                      (2, '"id":"sparse-state"', 1, 10.0),
                      (3, '"id":"sparse-persist-prompt"', 1, 10.0),
                      (3, '"type":"agent_settled"', 1, 25.0)),
        provider_steps=(FixtureProviderStep(text="persisted sparse response"),),
        expected_min_provider_requests=1, compare_provider_requests=True,
        expected_provider_model_ids=("sparse-model",), provider_waits=((3, 1, 15.0),),
        state_expectations=(("sparse-state", "thinkingLevel", "medium"),),
        model_catalog=(_R9_SPARSE_MODEL,), initial_model_id="sparse-model",
        settings_fixture={"defaultThinkingLevel": "low", "compaction": {"enabled": False}},
    ),
    InputRun(
        chunks=((_line("sparse-restarted-state", "get_state"), 0.0),),
        expected_response_ids=("sparse-restarted-state",), expected_response_commands=("get_state",),
        expected_successes=(True,), expected_data_presence=(True,), response_only=True,
        preserve_response_data_commands=("get_state",),
        state_expectations=(("sparse-restarted-state", "thinkingLevel", "medium"),),
        resume_session=True, reuse_workspace_previous=True, omit_cli_model_selection=True,
        model_catalog=(_R9_SPARSE_MODEL,),
        settings_fixture={"defaultThinkingLevel": "low", "compaction": {"enabled": False}},
    ),
)

_R9_BUSY_TOOL = FixtureProviderStep(
    tool_name="bash",
    tool_arguments=json.dumps({
        "command": 'gate=__RPC_TOOL_GATE__; echo started > "$gate.started"; '
                   'while [ ! -f "$gate.release" ]; do sleep 0.01; done; echo barrier-released',
    }),
)

R9_SCENARIOS["R9.busy-model-change"] = (
    InputRun(
        chunks=(
            (_line("model-run", "prompt", message="call the fixture bash tool"), 0.0),
            (_line("model-set-busy", "set_model", provider="fixture", modelId="model-two"), 0.0),
            (_line("model-cycle-busy", "cycle_model"), 0.0),
            (_line("model-after-busy", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("model-run", "model-set-busy", "model-cycle-busy", "model-after-busy"),
        expected_response_commands=("prompt", "set_model", "cycle_model", "get_state"),
        expected_successes=(True, True, True, True), expected_data_presence=(True, True, True, True),
        expected_error_presence=(False, False, False, False),
        preserve_response_data_commands=("set_model", "cycle_model", "get_state"),
        lifecycle_event_types=("tool_execution_start", "tool_execution_end", "agent_settled",
                               "thinking_level_changed"),
        enable_bash_tool=True,
        provider_steps=(_R9_BUSY_TOOL, FixtureProviderStep(text="tool and model transition completed")),
        provider_waits=((0, 1, 10.0), (3, 2, 25.0)),
        stdout_waits=((0, '"id":"model-run"', 1, 10.0), (0, '"type":"tool_execution_start"', 1, 15.0),
                      (1, '"id":"model-set-busy"', 1, 10.0), (2, '"id":"model-cycle-busy"', 1, 10.0),
                      (3, '"id":"model-after-busy"', 1, 10.0), (4, '"type":"agent_settled"', 1, 25.0)),
        release_tool_gate_after_input_index=2, expected_min_provider_requests=2, compare_provider_requests=True,
        expected_provider_model_ids=("model-one", "model-three"),
        state_expectations=(("model-after-busy", "thinkingLevel", "medium"),),
        model_catalog=_R9_BASIC_MODELS, initial_model_id="model-one",
        model_scope=("fixture/model-one", "fixture/model-two", "fixture/model-three"),
        settings_fixture={"compaction": {"enabled": False}},
    ),
)

R9_SCENARIOS["R9.thinking-event-response-order"] = (
    InputRun(
        chunks=(
            (_line("event-model", "set_model", provider="fixture", modelId="model-two"), 0.0),
            (_line("event-level", "set_thinking_level", level="medium"), 0.0),
        ),
        expected_response_ids=("event-model", "event-level"),
        expected_response_commands=("set_model", "set_thinking_level"),
        expected_successes=(True, True), expected_data_presence=(True, False),
        preserve_response_data_commands=("set_model",),
        lifecycle_event_types=_R9_THINKING_EVENTS, preserve_event_payloads=True,
        stdout_waits=((0, '"id":"event-model"', 1, 10.0), (1, '"id":"event-level"', 1, 10.0)),
        expected_marker_order=("event:thinking_level_changed", "response:event-model",
                               "event:thinking_level_changed", "response:event-level"),
        model_catalog=_R9_BASIC_MODELS[:2], initial_model_id="model-one",
        settings_fixture={"defaultThinkingLevel": "medium",
                          "modelThinkingLevels": {"fixture/model-one": "low", "fixture/model-two": "high"}},
    ),
)

R9_SCENARIOS["R9.model-persistence-restart"] = (
    InputRun(
        chunks=(
            (_line("persist-model", "set_model", provider="fixture", modelId="model-two"), 0.0),
            (_line("persist-level", "set_thinking_level", level="high"), 0.0),
            (_line("persist-first-prompt", "prompt", message="first persisted turn"), 0.0),
        ),
        expected_response_ids=("persist-model", "persist-level", "persist-first-prompt"),
        expected_response_commands=("set_model", "set_thinking_level", "prompt"),
        expected_successes=(True, True, True), expected_data_presence=(True, False, True),
        preserve_response_data_commands=("set_model",),
        preserve_event_payload_types=("thinking_level_changed",),
        provider_steps=(FixtureProviderStep(text="first persisted response"),),
        expected_min_provider_requests=1, compare_provider_requests=True,
        expected_provider_model_ids=("model-two",),
        provider_waits=((2, 1, 15.0),),
        stdout_waits=((0, '"id":"persist-model"', 1, 10.0),
                      (1, '"id":"persist-level"', 1, 10.0),
                      (2, '"id":"persist-first-prompt"', 1, 10.0),
                      (2, '"type":"agent_settled"', 1, 25.0)),
        lifecycle_event_types=("thinking_level_changed", "agent_settled"),
        model_catalog=_R9_BASIC_MODELS[:2], initial_model_id="model-one",
        model_scope=("fixture/model-one:low", "fixture/model-two:medium"),
        settings_fixture={"defaultThinkingLevel": "low", "compaction": {"enabled": False}},
    ),
    InputRun(
        chunks=((_line("restarted-state", "get_state"), 0.0),
                (_line("restarted-prompt", "prompt", message="second persisted turn"), 0.0)),
        expected_response_ids=("restarted-state", "restarted-prompt"),
        expected_response_commands=("get_state", "prompt"), expected_successes=(True, True),
        expected_data_presence=(True, True), preserve_response_data_commands=("get_state",),
        state_expectations=(("restarted-state", "thinkingLevel", "high"),),
        provider_steps=(FixtureProviderStep(text="second persisted response"),),
        expected_min_provider_requests=1, compare_provider_requests=True,
        expected_provider_model_ids=("model-two",),
        provider_waits=((1, 1, 15.0),),
        stdout_waits=((0, '"id":"restarted-state"', 1, 10.0),
                      (1, '"id":"restarted-prompt"', 1, 10.0),
                      (1, '"type":"agent_settled"', 1, 25.0)),
        lifecycle_event_types=("agent_settled",),
        resume_session=True, reuse_workspace_previous=True, omit_cli_model_selection=True,
        model_catalog=_R9_BASIC_MODELS[:2],
        model_scope=("fixture/model-one:low", "fixture/model-two:medium"),
        settings_fixture={"defaultThinkingLevel": "low", "compaction": {"enabled": False}},
    ),
)

R9_SCENARIOS["R9.invalid-model-no-mutation"] = (
    InputRun(
        chunks=(
            (_line("invalid-run", "prompt", message="call the fixture bash tool"), 0.0),
            (_line("invalid-busy-model", "set_model", provider="fixture", modelId="missing"), 0.0),
            (_line("invalid-busy-state", "get_state"), 0.0),
            (_line("invalid-after-release", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("invalid-run", "invalid-busy-model", "invalid-busy-state", "invalid-after-release"),
        expected_response_commands=("prompt", "set_model", "get_state", "get_state"),
        expected_successes=(True, False, True, True), expected_data_presence=(True, False, True, True),
        expected_error_presence=(False, True, False, False), preserve_response_error_text=True,
        preserve_response_data_commands=("get_state",),
        lifecycle_event_types=("tool_execution_start", "tool_execution_end", "agent_settled"),
        enable_bash_tool=True,
        provider_steps=(_R9_BUSY_TOOL, FixtureProviderStep(text="invalid selection left model unchanged")),
        provider_waits=((0, 1, 10.0), (3, 2, 25.0)),
        stdout_waits=((0, '"id":"invalid-run"', 1, 10.0), (0, '"type":"tool_execution_start"', 1, 15.0),
                      (1, '"id":"invalid-busy-model"', 1, 10.0),
                      (2, '"id":"invalid-busy-state"', 1, 10.0),
                      (3, '"id":"invalid-after-release"', 1, 10.0),
                      (4, '"type":"agent_settled"', 1, 25.0)),
        release_tool_gate_after_input_index=2, expected_min_provider_requests=2, compare_provider_requests=True,
        expected_provider_model_ids=("model-one", "model-one"),
        model_catalog=_R9_BASIC_MODELS[:2], initial_model_id="model-one",
        settings_fixture={"compaction": {"enabled": False}},
    ),
)

R9_SCENARIOS["R9.catalog-boundary"] = (
    InputRun(
        chunks=(
            (_line("catalog-local", "get_available_models"), 0.0),
            (_line("catalog-select", "set_model", provider="fixture", modelId="model-two"), 0.0),
            (_line("catalog-thinking", "get_available_thinking_levels"), 0.0),
            (_line("catalog-cycle-thinking", "cycle_thinking_level"), 0.0),
            (_line("catalog-cycle-model", "cycle_model"), 0.0),
        ),
        expected_response_ids=("catalog-local", "catalog-select", "catalog-thinking",
                               "catalog-cycle-thinking", "catalog-cycle-model"),
        expected_response_commands=("get_available_models", "set_model", "get_available_thinking_levels",
                                    "cycle_thinking_level", "cycle_model"),
        expected_successes=(True,) * 5, expected_data_presence=(True, True, True, True, True),
        preserve_response_data_commands=("get_available_models", "set_model", "get_available_thinking_levels",
                                         "cycle_thinking_level", "cycle_model"),
        stdout_waits=((0, '"id":"catalog-local"', 1, 10.0),
                      (1, '"id":"catalog-select"', 1, 10.0),
                      (2, '"id":"catalog-thinking"', 1, 10.0),
                      (3, '"id":"catalog-cycle-thinking"', 1, 10.0),
                      (4, '"id":"catalog-cycle-model"', 1, 10.0)),
        response_only=True, compare_provider_requests=True,
        model_catalog=_R9_BASIC_MODELS[:2], initial_model_id="model-one",
    ),
)


def _r10_compaction_fixture() -> str:
    entries = (
        _r7_message("r10-u1", None, "user", "earlier user question with context", minute=1),
        _r7_message("r10-a1", "r10-u1", "assistant", "earlier assistant answer", minute=2),
        _r7_message("r10-u2", "r10-a1", "user", "another earlier user question", minute=3),
        _r7_message("r10-a2", "r10-u2", "assistant", "another earlier assistant answer", minute=4),
        _r7_message("r10-u3", "r10-a2", "user", "retained user question", minute=5),
        _r7_message("r10-a3", "r10-u3", "assistant", "retained assistant answer", minute=6),
    )
    return _r7_fixture(entries, session_id="r10-compact-session")


R10_SCENARIOS: dict[str, tuple[InputRun, ...]] = {}
_R10_RETRY_MODEL = {
    "id": "fixture-model", "name": "fixture-model", "api": "openai-completions",
    "reasoning": False, "input": ["text"], "contextWindow": 65_536, "maxTokens": 1,
    "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0},
}
_R10_THRESHOLD_MODEL = {**_R10_RETRY_MODEL, "contextWindow": 4096}
_R10_OVERFLOW_MODEL = {**_R10_THRESHOLD_MODEL, "contextWindow": 65_536, "maxTokens": 128}
R10_SCENARIOS["R10.manual-compact-success"] = (
    InputRun(
        chunks=(
            (_line("compact", "compact", customInstructions="Summarize the earlier context."), 0.0),
            (_line("messages", "get_messages"), 0.0),
            (_line("entries", "get_entries"), 0.0),
            (_line("stats", "get_session_stats"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("compact", "messages", "entries", "stats"),
        expected_response_commands=("compact", "get_messages", "get_entries", "get_session_stats"),
        expected_successes=(True, True, True, True),
        expected_data_presence=(True, True, True, True),
        stdout_waits=((0, '"id":"compact"', 1, 20.0),
                      (1, '"id":"messages"', 1, 10.0),
                      (2, '"id":"entries"', 1, 10.0),
                      (3, '"id":"stats"', 1, 10.0)),
        provider_steps=(FixtureProviderStep(text="stable summary"),),
        expected_min_provider_requests=1, expected_provider_model_ids=("fixture-model",),
        compare_provider_requests=True,
        lifecycle_event_types=("compaction_start", "compaction_end"), preserve_event_payloads=True,
        preserve_response_data_commands=("compact", "get_messages", "get_entries", "get_session_stats"),
        normalize_session_entry_ids=True,
        session_fixture_jsonl=_r10_compaction_fixture(), model_context_window=65_536,
        settings_fixture={"compaction": {"enabled": True, "keepRecentTokens": 10}},
    ),
)

R10_SCENARIOS["R10.manual-compact-failure-rollback"] = (
    InputRun(
        chunks=(
            (_line("compact-failure", "compact", customInstructions="Summarize the earlier context."), 0.0),
            (_line("failure-entries", "get_entries"), 0.0),
            (_line("failure-messages", "get_messages"), 0.0),
            (_line("failure-stats", "get_session_stats"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("compact-failure", "failure-entries", "failure-messages", "failure-stats"),
        expected_response_commands=("compact", "get_entries", "get_messages", "get_session_stats"),
        expected_successes=(False, True, True, True), expected_data_presence=(False, True, True, True),
        preserve_response_error_text=True,
        stdout_waits=((0, '"id":"compact-failure"', 1, 20.0),
                      (1, '"id":"failure-entries"', 1, 10.0),
                      (2, '"id":"failure-messages"', 1, 10.0),
                      (3, '"id":"failure-stats"', 1, 10.0)),
        provider_steps=(FixtureProviderStep(status_code=500, error_message="fixture summary failure"),),
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("compaction_start", "compaction_end"), preserve_event_payloads=True,
        preserve_response_data_commands=("get_entries", "get_messages", "get_session_stats"),
        normalize_session_entry_ids=True,
        session_fixture_jsonl=_r10_compaction_fixture(), model_context_window=65_536,
        settings_fixture={"compaction": {"enabled": True, "keepRecentTokens": 10}},
        retry_settings=(("enabled", False), ("maxRetries", 0), ("baseDelayMs", 1), ("maxAgentDelayMs", 1)),
    ),
)

R10_SCENARIOS["R10.manual-compact-abort"] = (
    InputRun(
        chunks=(
            (_line("compact-abort", "compact", customInstructions="Summarize the earlier context."), 0.0),
            (_line("abort-compact", "abort"), 0.0),
            (_line("abort-entries", "get_entries"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("compact-abort", "abort-compact", "abort-entries"),
        expected_response_commands=("compact", "abort", "get_entries"),
        expected_successes=(False, True, True), expected_data_presence=(False, False, True),
        stdout_waits=((0, '"type":"compaction_start"', 1, 10.0),
                      (1, '"id":"abort-compact"', 1, 10.0),
                      (2, '"id":"compact-abort"', 1, 20.0),
                      (2, '"id":"abort-entries"', 1, 10.0)),
        provider_steps=(FixtureProviderStep(hold_before_response=True, text="aborted summary"),),
        provider_waits=((0, 1, 10.0),), release_provider_after_input_index=1,
        expected_min_provider_requests=1, compare_provider_requests=True,
        lifecycle_event_types=("compaction_start", "compaction_end"), preserve_event_payloads=True,
        preserve_response_data_commands=("get_entries",), normalize_session_entry_ids=True,
        session_fixture_jsonl=_r10_compaction_fixture(), model_context_window=65_536,
        settings_fixture={"compaction": {"enabled": True, "keepRecentTokens": 10}},
        retry_settings=(("enabled", False), ("maxRetries", 0), ("baseDelayMs", 1), ("maxAgentDelayMs", 1)),
    ),
)

R10_SCENARIOS["R10.auto-compaction-setting"] = (
    InputRun(
        chunks=(
            (_line("auto-enabled", "set_auto_compaction", enabled=True), 0.0),
            (_line("auto-enabled-state", "get_state"), 0.0),
            (_line("auto-disabled", "set_auto_compaction", enabled=False), 0.0),
            (_line("auto-disabled-state", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("auto-enabled", "auto-enabled-state", "auto-disabled", "auto-disabled-state"),
        expected_response_commands=("set_auto_compaction", "get_state", "set_auto_compaction", "get_state"),
        expected_successes=(True, True, True, True),
        expected_data_presence=(False, True, False, True),
        state_expectations=(("auto-enabled-state", "autoCompactionEnabled", True),
                            ("auto-disabled-state", "autoCompactionEnabled", False)),
        response_only=True,
        settings_fixture={"compaction": {"enabled": False}},
    ),
)

R10_SCENARIOS["R10.threshold-and-overflow-compaction"] = (
    InputRun(
        chunks=(
            (_line("threshold-prompt", "prompt", message="finish the current turn"), 0.0),
            (_line("threshold-entries", "get_entries"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("threshold-prompt", "threshold-entries"),
        expected_response_commands=("prompt", "get_entries"),
        expected_successes=(True, True), expected_data_presence=(True, True),
        expected_response_dispositions=("started", None),
        stdout_waits=((0, '"id":"threshold-prompt"', 1, 10.0),
                      (0, '"type":"compaction_start"', 1, 15.0),
                      (0, '"type":"compaction_end"', 1, 15.0),
                      (1, '"id":"threshold-entries"', 1, 10.0)),
        provider_steps=(FixtureProviderStep(text="threshold answer", usage_updates=((1200, 2, 1202),)),
                        FixtureProviderStep(text="threshold summary")),
        expected_min_provider_requests=2, expected_provider_model_ids=("fixture-model", "fixture-model"),
        compare_provider_requests=True,
        lifecycle_event_types=("compaction_start", "compaction_end"), preserve_event_payloads=True,
        preserve_response_data_commands=("get_entries",), normalize_session_entry_ids=True,
        session_fixture_jsonl=_r10_compaction_fixture(), model_context_window=4096,
        model_catalog=(_R10_THRESHOLD_MODEL,), initial_model_id="fixture-model",
        settings_fixture={"compaction": {"enabled": True, "reserveTokens": 3000, "keepRecentTokens": 10}},
    ),
    InputRun(
        chunks=(
            (_line("overflow-prompt", "prompt", message="retry after context overflow"), 0.0),
            (_line("overflow-entries", "get_entries"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("overflow-prompt", "overflow-entries"),
        expected_response_commands=("prompt", "get_entries"),
        expected_successes=(True, True), expected_data_presence=(True, True),
        expected_response_dispositions=("started", None),
        stdout_waits=((0, '"id":"overflow-prompt"', 1, 10.0),
                      (0, '"type":"compaction_start"', 1, 15.0),
                      (0, '"type":"compaction_end"', 1, 15.0),
                      (0, '"type":"agent_settled"', 1, 20.0),
                      (1, '"id":"overflow-entries"', 1, 10.0)),
        provider_steps=(FixtureProviderStep(text="truncated answer", finish_reason="length",
                                             usage_updates=((1200, 2, 1202),)),
                        FixtureProviderStep(text="overflow summary"),
                        FixtureProviderStep(text="answer after overflow"),
                        FixtureProviderStep(text="recovered answer")),
        expected_min_provider_requests=4,
        provider_waits=((0, 4, 20.0),),
        expected_provider_model_ids=("fixture-model", "fixture-model", "fixture-model", "fixture-model"),
        compare_provider_requests=True,
        lifecycle_event_types=("compaction_start", "compaction_end"),
        preserve_event_payloads=True, preserve_response_data_commands=("get_entries",),
        normalize_session_entry_ids=True,
        session_fixture_jsonl=_r10_compaction_fixture(), model_context_window=65_536,
        model_catalog=(_R10_OVERFLOW_MODEL,), initial_model_id="fixture-model",
        settings_fixture={"compaction": {"enabled": True, "reserveTokens": 3000, "keepRecentTokens": 10}},
        use_rpc_navigation_extension=True,
    ),
)

R10_SCENARIOS["R10.auto-retry-events"] = (
    InputRun(
        chunks=(
            (_line("retry-enabled", "set_auto_retry", enabled=True), 0.0),
            (_line("retry-prompt", "prompt", message="retry once then complete"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("retry-enabled", "retry-prompt"),
        expected_response_commands=("set_auto_retry", "prompt"),
        expected_successes=(True, True), expected_data_presence=(False, True),
        expected_response_dispositions=(None, "started"),
        stdout_waits=((0, '"id":"retry-enabled"', 1, 10.0),
                      (1, '"id":"retry-prompt"', 1, 10.0),
                      (1, '"type":"agent_settled"', 1, 20.0)),
        provider_steps=(FixtureProviderStep(status_code=503, error_message="503 transient fixture"),
                        FixtureProviderStep(text="retried successfully")),
        retry_settings=(("enabled", True), ("maxRetries", 1), ("baseDelayMs", 1), ("maxAgentDelayMs", 1)),
        provider_waits=((1, 2, 10.0),), expected_min_provider_requests=2,
        expected_provider_model_ids=("fixture-model", "fixture-model"), compare_provider_requests=True,
        lifecycle_event_types=("auto_retry_start", "auto_retry_end", "agent_end", "agent_settled"),
        preserve_event_payload_types=("auto_retry_start", "auto_retry_end", "agent_settled"),
        model_catalog=(_R10_RETRY_MODEL,), initial_model_id="fixture-model",
        settings_fixture={"compaction": {"enabled": False}},
    ),
)

R10_SCENARIOS["R10.abort-retry"] = (
    InputRun(
        chunks=(
            (_line("abort-retry-enabled", "set_auto_retry", enabled=True), 0.0),
            (_line("abort-retry-prompt", "prompt", message="fail once then abort retry"), 0.0),
            (_line("abort-retry", "abort_retry"), 0.0),
            (_line("abort-retry-state", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("abort-retry-enabled", "abort-retry-prompt", "abort-retry", "abort-retry-state"),
        expected_response_commands=("set_auto_retry", "prompt", "abort_retry", "get_state"),
        expected_successes=(True, True, True, True), expected_data_presence=(False, True, False, True),
        expected_response_dispositions=(None, "started", None, None),
        stdout_waits=((0, '"id":"abort-retry-enabled"', 1, 10.0),
                      (1, '"id":"abort-retry-prompt"', 1, 10.0),
                      (1, '"type":"auto_retry_start"', 1, 10.0),
                      (2, '"id":"abort-retry"', 1, 10.0),
                      (2, '"type":"auto_retry_end"', 1, 10.0),
                      (3, '"id":"abort-retry-state"', 1, 10.0),
                      (3, '"type":"agent_settled"', 1, 10.0)),
        provider_steps=(FixtureProviderStep(status_code=503, error_message="503 transient fixture"),),
        retry_settings=(("enabled", True), ("maxRetries", 3), ("baseDelayMs", 3_000),
                        ("maxAgentDelayMs", 3_000)),
        provider_waits=((1, 1, 10.0),), expected_min_provider_requests=1,
        expected_provider_model_ids=("fixture-model",), compare_provider_requests=True,
        lifecycle_event_types=("auto_retry_start", "auto_retry_end", "agent_end", "agent_settled"),
        preserve_event_payload_types=("auto_retry_start", "auto_retry_end", "agent_settled"),
        model_catalog=(_R10_RETRY_MODEL,), initial_model_id="fixture-model",
        settings_fixture={"compaction": {"enabled": False}},
    ),
)

R10_SCENARIOS["R10.compaction-and-retry-restart"] = (
    InputRun(
        chunks=(
            (_line("restart-compact", "compact", customInstructions="Persist a compacted session."), 0.0),
            (_line("restart-auto-compaction", "set_auto_compaction", enabled=False), 0.0),
            (_line("restart-auto-retry", "set_auto_retry", enabled=True), 0.0),
            (_line("restart-before-exit-entries", "get_entries"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("restart-compact", "restart-auto-compaction", "restart-auto-retry",
                               "restart-before-exit-entries"),
        expected_response_commands=("compact", "set_auto_compaction", "set_auto_retry", "get_entries"),
        expected_successes=(True, True, True, True), expected_data_presence=(True, False, False, True),
        stdout_waits=((0, '"id":"restart-compact"', 1, 20.0),
                      (0, '"type":"compaction_start"', 1, 10.0),
                      (0, '"type":"compaction_end"', 1, 10.0),
                      (1, '"id":"restart-auto-compaction"', 1, 10.0),
                      (2, '"id":"restart-auto-retry"', 1, 10.0),
                      (3, '"id":"restart-before-exit-entries"', 1, 10.0)),
        provider_steps=(FixtureProviderStep(text="persisted restart summary"),
                        FixtureProviderStep(status_code=503, error_message="503 persisted retry fixture"),
                        FixtureProviderStep(text="retry after restart")),
        provider_waits=((0, 1, 15.0),), expected_min_provider_requests=1,
        expected_provider_model_ids=("fixture-model",), compare_provider_requests=True,
        lifecycle_event_types=("compaction_start", "compaction_end"), preserve_event_payloads=True,
        preserve_response_data_commands=("get_entries",), normalize_session_entry_ids=True,
        session_fixture_jsonl=_r10_compaction_fixture(), model_context_window=65_536,
        model_catalog=(_R10_OVERFLOW_MODEL,), initial_model_id="fixture-model",
        settings_fixture={"compaction": {"enabled": True, "reserveTokens": 3000, "keepRecentTokens": 10}},
        retry_settings=(("enabled", False), ("maxRetries", 1), ("baseDelayMs", 1), ("maxAgentDelayMs", 1)),
    ),
    InputRun(
        chunks=(
            (_line("restart-state", "get_state"), 0.0),
            (_line("restart-messages", "get_messages"), 0.0),
            (_line("restart-entries", "get_entries"), 0.0),
            (_line("restart-prompt", "prompt", message="retry after the persisted restart"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("restart-state", "restart-messages", "restart-entries", "restart-prompt"),
        expected_response_commands=("get_state", "get_messages", "get_entries", "prompt"),
        expected_successes=(True, True, True, True), expected_data_presence=(True, True, True, True),
        expected_response_dispositions=(None, None, None, "started"),
        stdout_waits=((0, '"id":"restart-state"', 1, 10.0),
                      (1, '"id":"restart-messages"', 1, 10.0),
                      (2, '"id":"restart-entries"', 1, 10.0),
                      (3, '"id":"restart-prompt"', 1, 10.0),
                      (3, '"type":"auto_retry_start"', 1, 15.0),
                      (3, '"type":"agent_settled"', 1, 25.0)),
        provider_waits=((3, 2, 15.0),), expected_min_provider_requests=2,
        expected_provider_model_ids=("fixture-model", "fixture-model"), compare_provider_requests=True,
        lifecycle_event_types=("auto_retry_start", "auto_retry_end", "agent_end", "agent_settled"),
        preserve_event_payload_types=("auto_retry_start", "auto_retry_end", "agent_settled"),
        preserve_response_data_commands=("get_state", "get_messages", "get_entries"),
        normalize_session_entry_ids=True, preserve_session_identity=True,
        state_expectations=(("restart-state", "autoCompactionEnabled", False),),
        reuse_session_fixture_path=True, reuse_workspace_previous=True, omit_cli_model_selection=True,
        model_context_window=65_536, model_catalog=(_R10_OVERFLOW_MODEL,),
    ),
)

R10_SCENARIOS["R10.summarization-retry-events"] = (
    InputRun(
        chunks=(
            (_line("summary-retry-compact", "compact", customInstructions="Retry the fixture summary."), 0.0),
            (_line("summary-retry-entries", "get_entries"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("summary-retry-compact", "summary-retry-entries"),
        expected_response_commands=("compact", "get_entries"),
        expected_successes=(True, True), expected_data_presence=(True, True),
        stdout_waits=((0, '"id":"summary-retry-compact"', 1, 20.0),
                      (0, '"type":"summarization_retry_scheduled"', 1, 10.0),
                      (0, '"type":"summarization_retry_attempt_start"', 1, 10.0),
                      (0, '"type":"summarization_retry_finished"', 1, 10.0),
                      (0, '"type":"compaction_end"', 1, 10.0),
                      (1, '"id":"summary-retry-entries"', 1, 10.0)),
        provider_steps=(FixtureProviderStep(status_code=503, error_message="transient summary failure"),
                        FixtureProviderStep(text="summary after retry")),
        expected_min_provider_requests=2, expected_provider_model_ids=("fixture-model", "fixture-model"),
        compare_provider_requests=True,
        lifecycle_event_types=("compaction_start", "summarization_retry_scheduled",
                               "summarization_retry_attempt_start", "summarization_retry_finished",
                               "compaction_end"),
        preserve_event_payloads=True, preserve_response_data_commands=("compact", "get_entries"),
        normalize_session_entry_ids=True,
        session_fixture_jsonl=_r10_compaction_fixture(), model_context_window=65_536,
        settings_fixture={"compaction": {"enabled": True, "reserveTokens": 10, "keepRecentTokens": 10}},
        retry_settings=(("enabled", True), ("maxRetries", 1), ("baseDelayMs", 1), ("maxAgentDelayMs", 1)),
    ),
    InputRun(
        chunks=(
            (_line("branch-summary-retry", "prompt",
                   message="/rpc-fixture-navigate-tree r10-u2"), 0.0),
            (_line("branch-summary-entries", "get_entries"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("branch-summary-retry", "branch-summary-entries"),
        expected_response_commands=("prompt", "get_entries"),
        expected_successes=(True, True), expected_data_presence=(True, True),
        expected_response_dispositions=("handled", None),
        stdout_waits=((0, '"id":"branch-summary-retry"', 1, 20.0),
                      (0, '"type":"summarization_retry_scheduled"', 1, 10.0),
                      (0, '"type":"summarization_retry_attempt_start"', 1, 10.0),
                      (0, '"type":"summarization_retry_finished"', 1, 10.0),
                      (1, '"id":"branch-summary-entries"', 1, 10.0)),
        provider_steps=(FixtureProviderStep(status_code=503, error_message="transient branch summary failure"),
                        FixtureProviderStep(text="branch summary after retry")),
        expected_min_provider_requests=2, expected_provider_model_ids=("fixture-model", "fixture-model"),
        compare_provider_requests=True,
        lifecycle_event_types=("summarization_retry_scheduled", "summarization_retry_attempt_start",
                               "summarization_retry_finished"),
        preserve_event_payloads=True, preserve_response_data_commands=("get_entries",),
        normalize_session_entry_ids=True,
        session_fixture_jsonl=_r10_compaction_fixture(), model_context_window=65_536,
        settings_fixture={"compaction": {"enabled": False}},
        retry_settings=(("enabled", True), ("maxRetries", 1), ("baseDelayMs", 1), ("maxAgentDelayMs", 1)),
        use_rpc_navigation_extension=True,
    ),
)

R11_SCENARIOS: dict[str, tuple[InputRun, ...]] = {
    "R11.bash-success-result": (
        InputRun(
            chunks=((_line("bash-result", "bash", command=(
                "printf 'stdout-marker\\n'; printf 'stderr-marker\\n' >&2; exit 7")), 0.0), (b"", 0.0)),
            expected_response_ids=("bash-result",), expected_response_commands=("bash",),
            expected_successes=(True,), expected_data_presence=(True,),
            response_data_expectations=(("bash-result", "exitCode", 7),
                                        ("bash-result", "cancelled", False),
                                        ("bash-result", "truncated", False)),
            preserve_response_data_commands=("bash",),
            lifecycle_event_types=("bash_execution_update",), preserve_event_payloads=True,
            stdout_waits=((0, '"command":"bash","success":true', 1, 10.0),),
        ),
    ),
    "R11.bash-stream-id-and-chunks": (
        InputRun(
            chunks=((_line("bash-chunks", "bash", command=(
                "printf 'chunk-one\\n'; sleep 0.25; printf 'chunk-two\\n'; "
                "sleep 0.25; printf 'chunk-three\\n'")), 0.0), (b"", 0.0)),
            expected_response_ids=("bash-chunks",), expected_response_commands=("bash",),
            expected_successes=(True,), expected_data_presence=(True,),
            response_data_expectations=(("bash-chunks", "exitCode", 0),
                                        ("bash-chunks", "cancelled", False)),
            preserve_response_data_commands=("bash",),
            lifecycle_event_types=("bash_execution_update",), preserve_event_payloads=True,
            minimum_lifecycle_event_counts=(("bash_execution_update", 2),),
            stdout_waits=((0, '"command":"bash","success":true', 1, 10.0),),
        ),
    ),
    "R11.bash-without-command-id": (
        InputRun(
            chunks=((_line(None, "bash", command="printf 'un correlated output\\n'"), 0.0), (b"", 0.0)),
            expected_response_commands=("bash",), expected_successes=(True,), expected_data_presence=(True,),
            response_data_expectations=((None, "exitCode", 0),),
            preserve_response_data_commands=("bash",),
            lifecycle_event_types=("bash_execution_update",), preserve_event_payloads=True,
            stdout_waits=((0, '"command":"bash","success":true', 1, 10.0),),
        ),
    ),
    "R11.bash-exclude-from-context": (
        InputRun(
            chunks=(
                (_line("bash-excluded", "bash", command="printf 'rpc-context-excluded-marker'",
                       excludeFromContext=True), 0.0),
                (_line("bash-excluded-prompt", "prompt", message="Answer with ready."), 0.0),
                (b"", 0.0),
            ),
            expected_response_ids=("bash-excluded", "bash-excluded-prompt"),
            expected_response_commands=("bash", "prompt"), expected_successes=(True, True),
            expected_data_presence=(True, True),
            response_data_expectations=(("bash-excluded", "output", "rpc-context-excluded-marker"),
                                        ("bash-excluded", "truncated", False)),
            preserve_response_data_commands=("bash",),
            provider_steps=(FixtureProviderStep(text="ready"),), provider_waits=((1, 1, 10.0),),
            expected_min_provider_requests=1, expected_provider_model_ids=("fixture-model",),
            compare_provider_requests=True, provider_request_text_excludes=("rpc-context-excluded-marker",),
            lifecycle_event_types=("bash_execution_update", "agent_settled"), preserve_event_payloads=True,
            stdout_waits=((0, '"command":"bash","success":true', 1, 10.0),
                          (1, '"id":"bash-excluded-prompt"', 1, 10.0),
                          (1, '"type":"agent_settled"', 1, 15.0)),
            model_catalog=({
                "id": "fixture-model", "name": "fixture-model", "api": "openai-completions",
                "reasoning": False, "input": ["text"], "contextWindow": 65_536, "maxTokens": 128,
                "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0},
            },),
            initial_model_id="fixture-model",
            model_context_window=65_536,
            settings_fixture={"compaction": {"enabled": False}},
        ),
    ),
    "R11.bash-failure-semantics": (
        InputRun(
            chunks=(
                (_line("bash-nonzero", "bash", command="printf 'nonzero-marker'; exit 5"), 0.0),
                (_line("bash-launch-failure", "bash", command="__pisharp_rpc_missing_executable__"), 0.0),
                (b"", 0.0),
            ),
            expected_response_ids=("bash-nonzero", "bash-launch-failure"),
            expected_response_commands=("bash", "bash"), expected_successes=(True, True),
            expected_data_presence=(True, True),
            response_data_expectations=(("bash-nonzero", "exitCode", 5),
                                        ("bash-launch-failure", "exitCode", 127)),
            preserve_response_data_commands=("bash",),
            lifecycle_event_types=("bash_execution_update",), preserve_event_payloads=True,
            stdout_waits=((0, '"command":"bash","success":true', 1, 10.0),
                          (1, '"command":"bash","success":true', 2, 10.0)),
            model_context_window=65_536,
            settings_fixture={"compaction": {"enabled": False}},
        ),
    ),
    "R11.abort-active-bash": (
        InputRun(
            chunks=(
                (_line("bash-abort-target", "bash", command=(
                    "printf 'abort-ready\\n'; while :; do sleep 1; done")), 0.0),
                (_line("bash-abort", "abort_bash"), 0.0),
                (b"", 0.0),
            ),
            expected_response_ids=("bash-abort", "bash-abort-target"),
            expected_response_commands=("abort_bash", "bash"), expected_successes=(True, True),
            expected_data_presence=(False, True),
            lifecycle_event_types=("bash_execution_update",), preserve_event_payloads=True,
            preserve_response_data_commands=("bash",),
            stdout_waits=((0, '"type":"bash_execution_update"', 1, 10.0),
                          (1, '"command":"abort_bash","success":true', 1, 10.0),
                          (1, '"command":"bash","success":true', 1, 10.0)),
        ),
    ),
    "R11.bash-during-agent-run": (
        InputRun(
            chunks=(
                (_line("bash-busy-prompt", "prompt", message="wait for the fixture response"), 0.0),
                (_line("bash-during-run", "bash", command="printf 'during-agent-run'"), 0.0),
                (_line("bash-busy-state", "get_state"), 0.0),
                (b"", 0.0),
            ),
            expected_response_ids=("bash-busy-prompt", "bash-during-run", "bash-busy-state"),
            expected_response_commands=("prompt", "bash", "get_state"),
            expected_successes=(True, True, True), expected_data_presence=(True, True, True),
            expected_response_dispositions=("started", None, None),
            response_data_expectations=(("bash-during-run", "output", "during-agent-run"),),
            preserve_response_data_commands=("bash", "get_state"), preserve_state_flags=True,
            state_flag_expectations=(("bash-busy-state", "isStreaming", True),),
            provider_steps=(FixtureProviderStep(hold_before_response=True, text="active answer"),),
            provider_waits=((0, 1, 10.0),), expected_min_provider_requests=1,
            expected_provider_model_ids=("fixture-model",), compare_provider_requests=True,
            release_provider_after_input_index=2,
            stdout_waits=((0, '"id":"bash-busy-prompt"', 1, 10.0),
                          (1, '"command":"bash","success":true', 1, 10.0),
                          (2, '"id":"bash-busy-state"', 1, 10.0),
                          (3, '"type":"agent_settled"', 1, 20.0)),
            lifecycle_event_types=("agent_start", "turn_start", "message_start", "message_update", "message_end",
                                   "turn_end", "agent_end", "agent_settled", "bash_execution_update"),
            preserve_event_payload_types=("bash_execution_update",),
            model_catalog=({
                "id": "fixture-model", "name": "fixture-model", "api": "openai-completions",
                "reasoning": False, "input": ["text"], "contextWindow": 65_536, "maxTokens": 128,
                "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0},
            },),
            initial_model_id="fixture-model",
            model_context_window=65_536,
            settings_fixture={"compaction": {"enabled": False}},
        ),
    ),
    "R11.shutdown-active-bash": (
        InputRun(
            chunks=((_line("bash-shutdown-target", "bash", command=(
                "printf 'shutdown-ready\\n'; sleep 30; printf 'late-output\\n'")), 0.0), (b"", 0.0)),
            expected_response_ids=(), expected_response_commands=(), expected_successes=(), expected_data_presence=(),
            lifecycle_event_types=("bash_execution_update",), preserve_event_payloads=True,
            stdout_waits=((0, '"type":"bash_execution_update"', 1, 10.0),),
        ),
    ),
}


_R8_GENERATED_FIXTURE_CACHE: dict[str, tuple[str, str, list[dict[str, str]]]] = {}


class _FixtureProvider:
    def __init__(self, response_delays: tuple[float, ...], provider_steps: tuple[FixtureProviderStep, ...],
                 tool_gate_path: Path):
        self._response_delays = response_delays
        self._provider_steps = provider_steps
        self._tool_gate_path = tool_gate_path
        self._request_count = 0
        self._request_bodies: list[bytes] = []
        self._condition = threading.Condition()
        self._release_stream = threading.Event()
        self._client_disconnect_count = 0
        self._tool_gate_path.with_suffix(".started").unlink(missing_ok=True)
        self._tool_gate_path.with_suffix(".release").unlink(missing_ok=True)
        self._tool_gate_path.with_suffix(".effect").unlink(missing_ok=True)
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, format: str, *args: Any) -> None:
                return

            def do_POST(self) -> None:
                try:
                    length = int(self.headers.get("Content-Length", "0"))
                    request_body = self.rfile.read(length)
                    request = json.loads(request_body.decode("utf-8"))
                    request_index = fixture.record_request(request_body)
                    step = fixture.provider_step(request_index)
                    if step.delay_seconds:
                        time.sleep(step.delay_seconds)
                    if step.hold_before_response and fixture.wait_for_client_disconnect(self.connection):
                        return
                    streaming = request.get("stream") is True
                    if streaming and step.hold_after_partial:
                        self.send_response(step.status_code)
                        self.send_header("Content-Type", "text/event-stream")
                        self.send_header("Cache-Control", "no-cache")
                        self.send_header("Connection", "close")
                        self.end_headers()
                        partial = fixture.stream_chunk(request_index, {"role": "assistant", "content": step.text})
                        self.wfile.write(f"data: {json.dumps(partial, separators=(',', ':'))}\n\n".encode("utf-8"))
                        self.wfile.flush()
                        if fixture.wait_for_client_disconnect(self.connection):
                            return
                        return
                    if streaming and step.flush_stream_chunks and step.status_code < 400:
                        self.send_response(step.status_code)
                        self.send_header("Content-Type", "text/event-stream")
                        self.send_header("Cache-Control", "no-cache")
                        self.send_header("Connection", "close")
                        self.end_headers()
                        for chunk in fixture.response_chunks(request_index, step):
                            self.wfile.write(chunk)
                            self.wfile.flush()
                            if step.stream_chunk_delay_seconds:
                                time.sleep(step.stream_chunk_delay_seconds)
                        return
                    response = fixture.response_body(streaming, request_index, step)
                    self.send_response(step.status_code)
                    self.send_header("Content-Type", "text/event-stream" if streaming and step.status_code < 400
                                     else "application/json")
                    self.send_header("Cache-Control", "no-cache")
                    self.send_header("Connection", "close")
                    self.send_header("Content-Length", str(len(response)))
                    self.end_headers()
                    self.wfile.write(response)
                    self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError):
                    fixture.record_client_disconnect()
                except Exception as error:
                    fixture.record_error(f"{type(error).__name__}: {error}")
                    try:
                        self.send_error(500)
                    except OSError:
                        pass

        self._server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self._server.daemon_threads = True
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)
        self._thread.start()
        self.base_url = f"http://127.0.0.1:{self._server.server_port}/v1"
        self._errors: list[str] = []

    @property
    def request_count(self) -> int:
        with self._condition:
            return self._request_count

    @property
    def errors(self) -> list[str]:
        with self._condition:
            return list(self._errors)

    @property
    def request_bodies(self) -> list[bytes]:
        with self._condition:
            return list(self._request_bodies)

    def record_request(self, body: bytes) -> int:
        with self._condition:
            index = self._request_count
            self._request_count += 1
            self._request_bodies.append(body)
            self._condition.notify_all()
            return index

    def record_error(self, error: str) -> None:
        with self._condition:
            self._errors.append(error)
            self._condition.notify_all()

    @property
    def client_disconnect_count(self) -> int:
        with self._condition:
            return self._client_disconnect_count

    def record_client_disconnect(self) -> None:
        with self._condition:
            self._client_disconnect_count += 1
            self._condition.notify_all()

    def wait_for_client_disconnect(self, connection: socket.socket) -> bool:
        while not self._release_stream.wait(0.05):
            try:
                readable, _, _ = select.select([connection], [], [], 0)
                if readable and connection.recv(1, socket.MSG_PEEK) == b"":
                    self.record_client_disconnect()
                    return True
            except (ConnectionResetError, OSError, ValueError):
                self.record_client_disconnect()
                return True
        return False

    def wait_for_requests(self, count: int, timeout_seconds: float) -> bool:
        deadline = time.monotonic() + timeout_seconds
        with self._condition:
            while self._request_count < count:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return False
                self._condition.wait(remaining)
            return True

    def release_held_response(self) -> None:
        self._release_stream.set()

    def provider_step(self, request_index: int) -> FixtureProviderStep:
        if request_index < len(self._provider_steps):
            return self._provider_steps[request_index]
        delay = (self._response_delays[request_index]
                 if request_index < len(self._response_delays) else 0.0)
        return FixtureProviderStep(delay_seconds=delay)

    def stream_chunk(self, request_index: int, delta: dict[str, Any]) -> dict[str, Any]:
        response_id = f"rpc-fixture-{request_index}"
        return {
            "id": response_id, "object": "chat.completion.chunk", "created": 1,
            "model": "fixture-model", "choices": [{"index": 0, "delta": delta, "finish_reason": None}],
        }

    def response_body(self, stream: bool, request_index: int, step: FixtureProviderStep) -> bytes:
        response_id = f"rpc-fixture-{request_index}"
        if step.status_code >= 400:
            response = {"error": {"message": step.error_message or "fixture provider error"}}
            return json.dumps(response, separators=(",", ":")).encode("utf-8")
        if stream:
            if (step.text_fragments or step.reasoning_fragments or step.usage_updates or step.fail_after_partial or
                    not step.include_usage or step.finish_reason != "stop" or
                    step.tool_name is not None or step.tool_calls):
                return b"".join(self.response_chunks(request_index, step))
            chunks = [
                self.stream_chunk(request_index, {"role": "assistant", "content": step.text}),
                {
                    "id": f"rpc-fixture-{request_index}", "object": "chat.completion.chunk", "created": 1,
                    "model": "fixture-model", "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
                    "usage": {"prompt_tokens": 12, "completion_tokens": 2, "total_tokens": 14},
                },
            ]
            body = "".join(f"data: {json.dumps(chunk, separators=(',', ':'))}\n\n" for chunk in chunks)
            return (body + "data: [DONE]\n\n").encode("utf-8")
        response = {
            "id": response_id, "object": "chat.completion", "created": 1, "model": "fixture-model",
            "choices": [{"index": 0, "message": {"role": "assistant", "content": step.text},
                         "finish_reason": step.finish_reason}],
            "usage": {"prompt_tokens": 12, "completion_tokens": 2, "total_tokens": 14},
        }
        return json.dumps(response, separators=(",", ":")).encode("utf-8")

    def response_chunks(self, request_index: int, step: FixtureProviderStep) -> list[bytes]:
        def sse(value: dict[str, Any]) -> bytes:
            return f"data: {json.dumps(value, separators=(',', ':'))}\n\n".encode("utf-8")

        def usage(value: tuple[int, int, int]) -> dict[str, Any]:
            input_tokens, output_tokens, total_tokens = value
            return {"prompt_tokens": input_tokens, "completion_tokens": output_tokens,
                    "total_tokens": total_tokens}

        tool_calls = list(step.tool_calls)
        if not tool_calls and step.tool_name is not None:
            tool_calls = [(step.tool_name, step.tool_arguments or "{}")]

        deltas: list[dict[str, Any]] = []
        deltas.extend({"reasoning_content": value} for value in step.reasoning_fragments)
        deltas.extend({"content": value} for value in step.text_fragments)
        emitted_role = False
        chunks: list[dict[str, Any]] = []
        for delta in deltas:
            if not emitted_role:
                delta = {"role": "assistant", **delta}
                emitted_role = True
            chunks.append(self.stream_chunk(request_index, delta))
        for call_index, (tool_name, tool_arguments) in enumerate(tool_calls):
            arguments = tool_arguments.replace("__RPC_TOOL_GATE__", str(self._tool_gate_path))
            fragments = (step.tool_argument_fragments if call_index == 0 and step.tool_argument_fragments
                         else (arguments,))
            for fragment_index, fragment in enumerate(fragments):
                tool_call_delta: dict[str, Any] = {"index": call_index}
                function_delta: dict[str, str] = {"arguments": fragment}
                if fragment_index == 0:
                    function_delta["name"] = tool_name
                    tool_call_delta.update({
                        "id": f"rpc-tool-{request_index}" if len(tool_calls) == 1
                        else f"rpc-tool-{request_index}-{call_index}",
                        "type": "function",
                    })
                tool_call_delta["function"] = function_delta
                delta: dict[str, Any] = {"tool_calls": [tool_call_delta]}
                if not emitted_role:
                    delta = {"role": "assistant", **delta}
                    emitted_role = True
                chunks.append(self.stream_chunk(request_index, delta))
        if not chunks:
            chunks.append(self.stream_chunk(request_index, {"role": "assistant", "content": step.text}))
        for index, chunk in enumerate(chunks):
            if index < len(step.usage_updates):
                chunk["usage"] = usage(step.usage_updates[index])
        if step.fail_after_partial:
            return [*(sse(chunk) for chunk in chunks), b"data: {malformed fixture stream\n\n"]
        final_usage = usage(step.usage_updates[-1]) if step.usage_updates else (
            {"prompt_tokens": 12, "completion_tokens": 2, "total_tokens": 14} if step.include_usage else None
        )
        finish = {"id": f"rpc-fixture-{request_index}", "object": "chat.completion.chunk", "created": 1,
                  "model": "fixture-model", "choices": [{"index": 0, "delta": {},
                                                             "finish_reason": "tool_calls" if tool_calls else step.finish_reason}]}
        if final_usage is not None:
            finish["usage"] = final_usage
        chunks.append(finish)
        return [*(sse(chunk) for chunk in chunks), b"data: [DONE]\n\n"]

    def close(self) -> None:
        self._release_stream.set()
        self._server.shutdown()
        self._server.server_close()
        self._thread.join(timeout=5)


def _base_environment() -> dict[str, str]:
    inherited = {
        key: value for key, value in os.environ.items()
        if key in {"PATH", "DOTNET_ROOT", "LD_LIBRARY_PATH", "SYSTEMROOT", "WINDIR"}
    }
    for name in (
        "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GOOGLE_API_KEY", "GEMINI_API_KEY",
        "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_PROVIDER",
        "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH",
    ):
        inherited.pop(name, None)
    inherited.update({"TERM": "xterm-256color", "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"})
    return inherited


def _prepare_product(product: str, pi: Path, workspace: Path,
                     input_run: InputRun, provider_base_url: str | None = None) -> tuple[list[str], Path, dict[str, str]]:
    product_root = workspace / product
    agent = product_root / "agent"
    sessions = product_root / "sessions"
    agent.mkdir(parents=True, exist_ok=True)
    sessions.mkdir(parents=True, exist_ok=True)
    if input_run.isolate_export_output_directory:
        (product_root / "rpc-exports").mkdir(exist_ok=True)
    fixture_root = workspace.parents[1].resolve()
    fixture_replacements = {
        "__RPC_WORKING_DIRECTORY__": str(fixture_root),
        "__RPC_SOURCE_PROJECT__": str(fixture_root / "source-project"),
        "__RPC_TARGET_PROJECT__": str(fixture_root / "target-project"),
    }
    fixture_files = input_run.fixture_files + (
        input_run.pi_fixture_files if product == "pi" else input_run.pisharp_fixture_files
    )
    for relative_path, content in fixture_files:
        destination = (fixture_root / relative_path).resolve()
        if not destination.is_relative_to(fixture_root):
            raise VerificationError(f"fixture path escapes the process workspace: {relative_path}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        for marker, replacement in fixture_replacements.items():
            content = content.replace(marker, replacement)
        destination.write_text(content, encoding="utf-8")
    for relative_path, content in input_run.session_fixture_files:
        destination = (sessions / relative_path).resolve()
        if not destination.is_relative_to(sessions.resolve()):
            raise VerificationError(f"session fixture path escapes the session directory: {relative_path}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        for marker, replacement in fixture_replacements.items():
            content = content.replace(marker, replacement)
        destination.write_text(content, encoding="utf-8")
    if input_run.project_rebind_fixture:
        for project, prompt_name in ((fixture_root / "source-project", "source-resource"),
                                     (fixture_root / "target-project", "target-resource")):
            prompt_directory = project / ".pi" / "prompts"
            prompt_directory.mkdir(parents=True, exist_ok=True)
            (prompt_directory / f"{prompt_name}.md").write_text(
                f"---\ndescription: {prompt_name}\n---\n\n{prompt_name} body\n", encoding="utf-8")
        (agent / "trust.json").write_text(json.dumps({
            str(fixture_root / "source-project"): False,
            str(fixture_root / "target-project"): True,
        }) + "\n", encoding="utf-8")
    model = {
        "id": "fixture-model", "name": "fixture-model", "api": input_run.provider_api,
        "reasoning": input_run.reasoning, "input": ["text"],
        "maxTokens": 128,
        "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0},
    }
    if input_run.model_context_window is not None:
        model["contextWindow"] = input_run.model_context_window
    provider: dict[str, Any] = {
        "baseUrl": provider_base_url or "http://127.0.0.1:9/v1",
        "models": [dict(configured) for configured in input_run.model_catalog] if input_run.model_catalog is not None
        else [model],
    }
    if product == "pi":
        provider["apiKey"] = "fixture-only-key"
    else:
        provider["apiKeyEnv"] = "PISHARP_RPC_FIXTURE_KEY"
    (agent / "models.json").write_text(json.dumps({"providers": {"fixture": provider}}) + "\n", encoding="utf-8")
    settings_path = agent / "settings.json"
    settings = json.loads(settings_path.read_text(encoding="utf-8")) if settings_path.exists() else {}
    if input_run.settings_fixture is not None:
        settings.update(input_run.settings_fixture)
    if input_run.project_rebind_fixture:
        settings["extensions"] = ["-builtin:mcp", "-builtin:llama.cpp"]
    if input_run.use_rpc_navigation_extension or input_run.use_rpc_ui_extension:
        settings["extensions"] = ["-builtin:mcp", "-builtin:llama.cpp"]
    if provider_base_url is not None and not settings_path.exists() and "compaction" not in settings:
        settings["compaction"] = {"keepRecentTokens": 1}
    if input_run.retry_settings:
        settings["retry"] = dict(input_run.retry_settings)
    if settings:
        settings_path.write_text(json.dumps(settings) + "\n", encoding="utf-8")
    environment = _base_environment()
    environment.update({
        "HOME": str(agent), "PI_CODING_AGENT_DIR": str(agent), "PI_OFFLINE": "1",
        "PISHARP_AGENT_DIR": str(agent), "PISHARP_SESSION_DIR": str(sessions),
        "PISHARP_RPC_FIXTURE_KEY": "fixture-only-key",
    })
    if input_run.use_rpc_ui_extension and product == "pisharp":
        environment["PISHARP_RPC_UI_FIXTURE"] = "true"
    if input_run.rpc_ui_extension_error:
        environment["PISHARP_RPC_UI_ERROR"] = "true"
    if input_run.rpc_ui_stream_editor:
        environment["PISHARP_RPC_UI_STREAM_EDITOR"] = "true"
    if input_run.use_rpc_tool_extension and product == "pisharp":
        environment["PISHARP_RPC_TOOL_FIXTURE"] = "true"
    has_session_fixture = input_run.session_fixture_jsonl is not None or \
        input_run.session_fixture_native_json is not None or input_run.reuse_session_fixture_path
    if has_session_fixture or input_run.omit_cli_model_selection:
        model_arguments = []
    else:
        model_id = input_run.initial_model_id
        if model_id is None:
            model_id = (input_run.model_catalog[0]["id"] if input_run.model_catalog else
                        "fixture-model" if input_run.model_catalog is None else None)
        model_arguments = [] if model_id is None else [
            "--provider", input_run.initial_model_provider, "--model", model_id,
        ]
    common = ["--mode", "rpc", *model_arguments,
              "--session-dir", str(sessions), "--no-context-files", "--no-extensions", "--no-skills",
              "--no-prompt-templates", "--system-prompt", "You are a deterministic RPC fixture assistant."]
    if input_run.no_session:
        common.append("--no-session")
    if input_run.rpc_command_resources:
        resource_root = fixture_root / "rpc-command-resources"
        common.extend(("--prompt-template", str(resource_root / "review.md"),
                       "--skill", str(resource_root / "quality" / "SKILL.md")))
    if input_run.project_rebind_fixture:
        common.remove("--no-extensions")
        common.remove("--no-prompt-templates")
    elif product == "pisharp" and (input_run.use_entry_appending_extension or
                                    input_run.use_rpc_navigation_extension or input_run.use_rpc_ui_extension or
                                    input_run.use_rpc_tool_extension):
        common.remove("--no-extensions")
    if input_run.resume_session:
        common.append("--continue")
    if input_run.model_scope:
        common.extend(("--models", ",".join(input_run.model_scope)))
    fixture_content = (input_run.session_fixture_native_json
                       if product == "pisharp" and input_run.session_fixture_native_json is not None
                       else input_run.session_fixture_jsonl)
    if fixture_content is not None:
        for marker, replacement in fixture_replacements.items():
            fixture_content = fixture_content.replace(marker, replacement)
        if input_run.bind_fixture_working_directory:
            working_directory = str(workspace.parents[1])
            if product == "pisharp" and input_run.session_fixture_native_json is not None:
                document = json.loads(fixture_content)
                document["WorkingDirectory"] = working_directory
                fixture_content = json.dumps(document, ensure_ascii=False, separators=(",", ":")) + "\n"
            else:
                records = [json.loads(line) for line in fixture_content.splitlines() if line]
                records[0]["cwd"] = working_directory
                fixture_content = "".join(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n"
                                           for record in records)
        fixture_name = input_run.session_fixture_name or (
            "rpc-session-fixture.session.json" if product == "pisharp" and
            input_run.session_fixture_native_json is not None else "rpc-session-fixture.jsonl")
        session_path = sessions / fixture_name
        session_path.write_text(fixture_content, encoding="utf-8")
        common.extend(("--session", str(session_path)))
    elif input_run.reuse_session_fixture_path:
        persisted_native_sessions = sorted(sessions.glob("*.session.json"),
                                          key=lambda path: path.stat().st_mtime_ns)
        session_path = persisted_native_sessions[-1] if product == "pisharp" and persisted_native_sessions \
            else sessions / "rpc-session-fixture.jsonl"
        if not session_path.is_file():
            raise VerificationError(f"persisted RPC fixture is missing: {session_path}")
        common.extend(("--session", str(session_path)))
    if input_run.active_tool_names:
        common.extend(("--tools", ",".join(input_run.active_tool_names)))
    elif input_run.enable_bash_tool:
        common.extend(("--tools", "bash"))
    elif not input_run.omit_no_tools:
        common.append("--no-tools")
    extension_path: Path | None = None
    if (input_run.use_handled_extension_command or input_run.use_entry_appending_extension or
            input_run.use_rpc_navigation_extension or input_run.use_rpc_ui_extension or
            input_run.use_rpc_tool_extension or input_run.project_rebind_fixture):
        if product == "pi" and input_run.project_rebind_fixture:
            extension_path = agent / "rpc-project-rebind.js"
            extension_path.write_text(
                'export default function (pi) { pi.on("session_start", (_event, context) => { '
                'process.stderr.write(`r12-project-context:${context.cwd}:${context.isProjectTrusted()}\\n`); '
                '}); }\n', encoding="utf-8")
        elif product == "pi":
            extension_path = agent / ("rpc-tool-extension.ts" if input_run.use_rpc_tool_extension
                                      else "rpc-handled-extension.ts")
            if input_run.use_rpc_tool_extension:
                extension_path.write_text(
                    'import { Type } from "@earendil-works/pi-ai";\n'
                    'const toolArgs = Type.Object({ value: Type.String(), delayMs: Type.Integer(), '
                    'emitPartial: Type.Boolean(), fail: Type.Boolean() }, { additionalProperties: false });\n'
                    'export default function (pi) { pi.registerTool({ name: "rpc_parity_tool", '
                    'label: "rpc_parity_tool", description: "Return a deterministic result for RPC parity tests.", '
                    'parameters: toolArgs, async execute(_id, p, _signal, onUpdate) { '
                    'if (p.emitPartial) onUpdate?.({ content: [{ type: "text", text: "partial:" + p.value }] }); '
                    'if (p.delayMs > 0) await new Promise(resolve => setTimeout(resolve, p.delayMs)); '
                    'if (p.fail) return { content: [{ type: "text", text: "fixture tool failed" }], '
                    'details: {}, isError: true }; '
                    'return { content: [{ type: "text", text: "result:" + p.value }] }; } }); '
                    'pi.registerTool({ name: "rpc_nested_orchestrator", label: "rpc_nested_orchestrator", '
                    'description: "Execute one nested RPC parity tool.", '
                    'parameters: Type.Object({}, { additionalProperties: false }), '
                    'async execute(_id, _p, signal, _onUpdate, context) { '
                    'const outcome = await context.executeTool("rpc_parity_tool", '
                    '{ value: "nested-child-result", delayMs: 0, emitPartial: false, fail: false }, { signal }); '
                    'const text = (outcome.result.content || []).filter(item => item.type === "text") '
                    '.map(item => item.text).join(""); return { content: [{ type: "text", '
                    'text: "nested child: " + text }] }; } }); }\n',
                    encoding="utf-8",
                )
            elif input_run.use_entry_appending_extension:
                extension_path.write_text(
                    'export default function (pi) { pi.registerCommand("native-fixture-state-set", '
                    '{ description: "Append a deterministic session entry", handler: async (value) => '
                    '{ pi.appendEntry("native-fixture-state", { revision: "v1", value }); '
                    'return "v1 state saved"; } }); }\n',
                    encoding="utf-8",
                )
            elif input_run.use_rpc_navigation_extension:
                extension_path.write_text(
                    'export default function (pi) { '
                    'pi.on("session_before_compact", async () => undefined); '
                    'pi.registerCommand("rpc-fixture-navigate-tree", { '
                    'description: "Navigate to a branch and request a deterministic summary.", '
                    'handler: async (targetId, context) => { '
                    'const result = await context.navigateTree(targetId, '
                    '{ summarize: true, customInstructions: "Summarize for RPC parity." }); '
                    'return result.cancelled ? "navigation cancelled" : "tree navigation completed"; '
                    '} }); }\n',
                    encoding="utf-8",
                )
            elif input_run.use_rpc_ui_extension:
                extension_path.write_text(
                    'export default function (pi) { '
                    'pi.on("before_agent_start", () => { '
                    'if (process.env.PISHARP_RPC_UI_ERROR === "true") throw new Error("R14 extension handler failed"); '
                    '}); '
                    'let editorSetDuringStream = false; pi.on("message_update", (_event, context) => { '
                    'if (process.env.PISHARP_RPC_UI_STREAM_EDITOR === "true" && !editorSetDuringStream) { '
                    'editorSetDuringStream = true; context.ui.setEditorText("R14 editor text"); } }); '
                    'pi.registerCommand("rpc-fixture-ui", { description: "Exercise RPC extension UI parity.", '
                    'handler: async (args, context) => { const emit = (name, value) => '
                    'context.ui.notify(`R14:${name}:${JSON.stringify(value === undefined ? null : value)}`, "info"); '
                    'switch (args.trim()) { '
                    'case "select": emit("select", await context.ui.select("R14 select", ["red", "blue"])); break; '
                    'case "confirm": emit("confirm", await context.ui.confirm("R14 confirm", "Proceed?")); break; '
                    'case "input": { const withPlaceholder = await context.ui.input("R14 input", "type here"); '
                    'const withoutPlaceholder = await context.ui.input("R14 input no placeholder"); '
                    'emit("input", [withPlaceholder, withoutPlaceholder]); break; } '
                    'case "editor": emit("editor", await context.ui.editor("R14 editor", "prefill text")); break; '
                    'case "notify": context.ui.notify("R14 notify info", "info"); '
                    'context.ui.notify("R14 notify warning", "warning"); '
                    'context.ui.notify("R14 notify error", "error"); break; '
                    'case "status": context.ui.setStatus("r14", "working"); context.ui.setStatus("r14", undefined); break; '
                    'case "widget": context.ui.setWidget("r14", ["first", "second"], { placement: "aboveEditor" }); '
                    'context.ui.setWidget("r14", ["below"], { placement: "belowEditor" }); '
                    'context.ui.setWidget("r14", undefined); context.ui.setWidget("r14-component", () => null); break; '
                    'case "title": context.ui.setTitle("R14 title"); break; '
                    'case "editor-text": context.ui.setEditorText("R14 editor text"); break; '
                    'case "concurrent": { const values = await Promise.all(['
                    'context.ui.select("R14 concurrent select", ["one", "two"]), '
                    'context.ui.input("R14 concurrent input", "concurrent placeholder")]); '
                    'emit("concurrent", values); break; } '
                    'case "timeout": emit("timeout", await context.ui.input("R14 timeout", "wait", { timeout: 40 })); break; '
                    'case "cancel": emit("cancel", await context.ui.input("R14 cancel", "cancel me")); break; '
                    'case "pending": await context.ui.input("R14 pending", "shutdown"); break; '
                    'case "unsupported": { const removeInput = context.ui.onTerminalInput(); removeInput(); '
                    'context.ui.setWorkingMessage("ignored"); context.ui.setWorkingVisible(false); '
                    'context.ui.setWorkingIndicator({ frames: ["ignored"] }); '
                    'context.ui.setHiddenThinkingLabel("ignored"); context.ui.setFooter(() => null); '
                    'context.ui.setHeader(() => null); context.ui.addAutocompleteProvider(() => []); '
                    'context.ui.setEditorComponent(() => null); '
                    'const custom = await context.ui.custom(); const editor = context.ui.getEditorText(); '
                    'context.ui.pasteToEditor("R14 pasted text"); context.ui.setToolsExpanded(true); '
                    'const themeSwitch = context.ui.setTheme("missing"); '
                    'emit("unsupported", { custom: custom === undefined, editor, '
                    'toolsExpanded: context.ui.getToolsExpanded(), theme: context.ui.getTheme("missing") === undefined, '
                    'themeSwitchFailed: !themeSwitch.success, editorComponent: context.ui.getEditorComponent() === undefined, '
                    'themes: context.ui.getAllThemes().length }); break; } '
                    'case "correlation": emit("correlation", await context.ui.select("R14 correlation", ["ok"])); break; '
                    'default: throw new Error(`Unknown R14 UI operation: ${args}`); } return "R14 UI operation complete"; } }); }\n',
                    encoding="utf-8",
                )
            else:
                extension_path.write_text(
                    'export default function (pi) { pi.registerCommand("fixture", '
                    '{ description: "Handle without a run", handler: async () => {} }); }\n',
                    encoding="utf-8",
                )
        elif input_run.use_entry_appending_extension or input_run.use_rpc_navigation_extension or \
                input_run.use_rpc_ui_extension or \
                input_run.project_rebind_fixture:
            extension_path = None
        else:
            extension_path = TEST_EXTENSION_DLL
        if extension_path is not None:
            common.extend(("--extension", str(extension_path)))
        if product == "pisharp" and input_run.use_entry_appending_extension:
            native_extension_root = agent / "native-extensions" / "pisharp.testextensions.nativefixture" / "1.0.0"
            library_root = native_extension_root / "lib" / "net10.0"
            library_root.mkdir(parents=True, exist_ok=True)
            for source in NATIVE_ENTRY_EXTENSION_DLL.parent.iterdir():
                if source.suffix in {".dll", ".json"} and source.is_file():
                    shutil.copy2(source, library_root / source.name)
            manifest = {
                "id": "PiSharp.TestExtensions.NativeFixture",
                "version": "1.0.0",
                "apiVersion": 1,
                "extensions": [{
                    "assembly": "lib/net10.0/NativeExtensionFixture.dll",
                    "type": "PiSharp.TestExtensions.NativeFixture.ExampleExtension",
                }],
            }
            (native_extension_root / "pisharp-extension.json").write_text(
                json.dumps(manifest) + "\n", encoding="utf-8")
            settings["nativeExtensions"] = [{
                "id": "PiSharp.TestExtensions.NativeFixture",
                "version": "1.0.0",
                "source": "rpc-fixture-native-extension.nupkg",
            }]
            settings_path.write_text(json.dumps(settings) + "\n", encoding="utf-8")
        if product == "pisharp" and (input_run.use_rpc_navigation_extension or input_run.use_rpc_ui_extension):
            native_extension_root = agent / "native-extensions" / "pisharp.testextensions.rpcparity" / "1.0.0"
            library_root = native_extension_root / "lib" / "net10.0"
            library_root.mkdir(parents=True, exist_ok=True)
            for source in RPC_PARITY_EXTENSION_DLL.parent.iterdir():
                if source.suffix in {".dll", ".json"} and source.is_file():
                    shutil.copy2(source, library_root / source.name)
            manifest = {
                "id": "PiSharp.TestExtensions.RpcParity",
                "version": "1.0.0",
                "apiVersion": 1,
                "extensions": [{
                    "assembly": "lib/net10.0/RpcParityExtensionFixture.dll",
                    "type": "PiSharp.TestExtensions.RpcParity.RpcParityExtension",
                }],
            }
            (native_extension_root / "pisharp-extension.json").write_text(
                json.dumps(manifest) + "\n", encoding="utf-8")
            settings["nativeExtensions"] = [{
                "id": "PiSharp.TestExtensions.RpcParity",
                "version": "1.0.0",
                "source": "rpc-parity-extension-fixture.nupkg",
            }]
            settings_path.write_text(json.dumps(settings) + "\n", encoding="utf-8")
        if product == "pisharp" and input_run.project_rebind_fixture:
            native_extension_root = agent / "native-extensions" / "pisharp.testextensions.rpcrebind" / "1.0.0"
            library_root = native_extension_root / "lib" / "net10.0"
            library_root.mkdir(parents=True, exist_ok=True)
            for source in RPC_PARITY_EXTENSION_DLL.parent.iterdir():
                if source.suffix in {".dll", ".json"} and source.is_file():
                    shutil.copy2(source, library_root / source.name)
            manifest = {
                "id": "PiSharp.TestExtensions.RpcRebind",
                "version": "1.0.0",
                "apiVersion": 1,
                "extensions": [{
                    "assembly": "lib/net10.0/RpcParityExtensionFixture.dll",
                    "type": "PiSharp.TestExtensions.RpcParity.RpcProjectRebindExtension",
                }],
            }
            (native_extension_root / "pisharp-extension.json").write_text(
                json.dumps(manifest) + "\n", encoding="utf-8")
            settings["nativeExtensions"] = [{
                "id": "PiSharp.TestExtensions.RpcRebind",
                "version": "1.0.0",
                "source": "rpc-project-rebind-fixture.nupkg",
            }]
            settings_path.write_text(json.dumps(settings) + "\n", encoding="utf-8")
    if product == "pi":
        command = ["node", "--import", (pi / PI_SOURCE_RESOLVER).as_uri(), str(pi / PI_CLI), *common]
        environment["PI_CODING_AGENT_DIR"] = str(agent)
        process_cwd = product_root if input_run.isolate_export_output_directory else workspace.parents[1]
        return command, process_cwd, environment
    command = ["dotnet", str(PISHARP_DLL), *common, "--offline"]
    process_cwd = product_root if input_run.isolate_export_output_directory else workspace.parents[1]
    return command, process_cwd, environment


def _read_all(stream, destination: list[bytes], condition: threading.Condition | None = None,
              read_size: int = 64 * 1024, start_gate: threading.Event | None = None,
              stop_event: threading.Event | None = None) -> None:
    while stop_event is None or not stop_event.is_set():
        if start_gate is not None:
            start_gate.wait()
        if stop_event is not None and stop_event.is_set():
            return
        if stop_event is None:
            chunk = stream.read(read_size)
        else:
            try:
                readable, _, _ = select.select([stream.fileno()], [], [], 0.05)
                if not readable:
                    continue
                if start_gate is not None and not start_gate.is_set():
                    continue
                chunk = os.read(stream.fileno(), read_size)
            except (OSError, ValueError):
                return
        if not chunk:
            return
        if condition is None:
            destination.append(chunk)
        else:
            with condition:
                destination.append(chunk)
                condition.notify_all()


def _execute(product: str, pi: Path, workspace: Path, input_run: InputRun,
             shared_provider: _FixtureProvider | None = None) -> dict[str, Any]:
    if shared_provider is not None:
        return _execute_with_provider(product, pi, workspace, input_run, shared_provider)
    uses_provider = bool(input_run.provider_response_delays or input_run.provider_steps)
    gate_path = workspace.parent.parent / "rpc-tool-gate"
    provider = (_FixtureProvider(input_run.provider_response_delays, input_run.provider_steps, gate_path)
                if uses_provider else None)
    try:
        return _execute_with_provider(product, pi, workspace, input_run, provider)
    finally:
        if provider is not None:
            provider.close()


def _execute_with_provider(product: str, pi: Path, workspace: Path, input_run: InputRun,
                           provider: _FixtureProvider | None) -> dict[str, Any]:
    replacements = input_run.pi_input_replacements if product == "pi" else input_run.pisharp_input_replacements
    if replacements:
        chunks = input_run.chunks
        for old, new in replacements:
            chunks = tuple((payload.replace(old.encode("utf-8"), new.encode("utf-8")), delay)
                           for payload, delay in chunks)
        input_run = replace(input_run, chunks=chunks)
    if input_run.project_rebind_fixture:
        target_project = str(workspace.parents[1].resolve() / "target-project")
        chunks = tuple((payload.replace(b"__RPC_TARGET_PROJECT__", target_project.encode("utf-8")), delay)
                       for payload, delay in input_run.chunks)
        input_run = replace(input_run, chunks=chunks)
    if input_run.bind_rpc_tool_gate_path:
        tool_gate_path = str((workspace.parent.parent / "rpc-tool-gate").resolve()).encode("utf-8")
        chunks = tuple((payload.replace(b"__RPC_TOOL_GATE__", tool_gate_path), delay)
                       for payload, delay in input_run.chunks)
        input_run = replace(input_run, chunks=chunks)
    if input_run.session_fixture_files:
        session_directory = str((workspace / product / "sessions").resolve())
        chunks = tuple((payload.replace(b"__RPC_SESSION_DIRECTORY__", session_directory.encode("utf-8")), delay)
                       for payload, delay in input_run.chunks)
        input_run = replace(input_run, chunks=chunks)
    provider_request_baseline = provider.request_count if provider is not None else 0
    command, cwd, environment = _prepare_product(
        product, pi, workspace, input_run, provider.base_url if provider is not None else None,
    )
    process = subprocess.Popen(
        command, cwd=cwd, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, start_new_session=True, bufsize=0,
    )
    stdout_chunks: list[bytes] = []
    stderr_chunks: list[bytes] = []
    stdout_condition = threading.Condition()
    if input_run.stdout_read_chunk_size < 1:
        raise VerificationError("stdout read chunk size must be positive")
    stdout_reader_release = threading.Event()
    if (input_run.stdout_pause_before_input_index is None and
            input_run.stdout_pause_after_input_index is None) or \
            input_run.stdout_pause_before_input_index is not None and \
            input_run.stdout_pause_before_input_index > 0:
        stdout_reader_release.set()
    stdout_reader_stop = threading.Event()
    stderr_reader_release = threading.Event()
    stderr_reader_release.set()
    stderr_reader_stop = threading.Event()
    stdout_reader = threading.Thread(
        target=_read_all,
        args=(process.stdout, stdout_chunks, stdout_condition, input_run.stdout_read_chunk_size,
              stdout_reader_release, stdout_reader_stop), daemon=True)
    stderr_reader = threading.Thread(
        target=_read_all, args=(process.stderr, stderr_chunks, None, 64 * 1024,
                                stderr_reader_release, stderr_reader_stop), daemon=True)
    stdout_reader.start()
    stderr_reader.start()
    input_chunks: list[bytes] = []
    input_error: str | None = None
    scenario_failures: list[str] = []
    provider_waits = {index: [] for index, _, _ in input_run.provider_waits}
    for index, count, timeout in input_run.provider_waits:
        provider_waits[index].append((count, timeout))
    tool_start_waits = {index: [] for index, _ in input_run.tool_start_waits}
    for index, timeout in input_run.tool_start_waits:
        tool_start_waits[index].append(timeout)
    stdout_waits = {index: [] for index, _, _, _ in input_run.stdout_waits}
    for index, marker, occurrence, timeout in input_run.stdout_waits:
        stdout_waits[index].append((marker, occurrence, timeout))

    def wait_for_stdout(marker: str, occurrence: int, timeout_seconds: float) -> bool:
        needle = marker.encode("utf-8")
        deadline = time.monotonic() + timeout_seconds
        with stdout_condition:
            while b"".join(stdout_chunks).count(needle) < occurrence:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return False
                stdout_condition.wait(remaining)
            return True

    def current_stdout_records() -> list[dict[str, Any]]:
        with stdout_condition:
            snapshot = b"".join(stdout_chunks)
        records: list[dict[str, Any]] = []
        for line in snapshot.splitlines():
            try:
                value = json.loads(line.decode("utf-8"))
            except (UnicodeError, json.JSONDecodeError):
                continue
            if isinstance(value, dict):
                records.append(value)
        return records

    def answer_extension_ui() -> None:
        if not input_run.ui_responses:
            return
        requests_by_key: dict[tuple[str, str], dict[str, Any]] = {}
        expected_keys = {(method, title) for method, title, _ in input_run.ui_responses}

        def wait_for_request(key: tuple[str, str]) -> dict[str, Any] | None:
            deadline = time.monotonic() + 15.0
            while key not in requests_by_key:
                for record in current_stdout_records():
                    if record.get("type") != "extension_ui_request":
                        continue
                    request_key = (record.get("method"), record.get("title"))
                    if request_key in expected_keys:
                        requests_by_key.setdefault(request_key, record)
                if key in requests_by_key:
                    break
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    scenario_failures.append(f"RPC extension UI request was not observed: {key!r}")
                    return None
                with stdout_condition:
                    stdout_condition.wait(min(remaining, 0.25))
            return requests_by_key[key]

        if input_run.ui_wait_all_requests:
            for key in sorted(expected_keys):
                if wait_for_request(key) is None:
                    return

        def send_ui_response(request_id: str, fields: dict[str, Any]) -> None:
            payload = json.dumps({"type": "extension_ui_response", "id": request_id, **fields},
                                 ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
            input_chunks.append(payload)
            try:
                process.stdin.write(payload)
                process.stdin.flush()
            except (BrokenPipeError, OSError) as error:
                scenario_failures.append(f"could not send RPC extension UI response: {type(error).__name__}: {error}")

        unknown_responses_sent = False
        for method, title, response in input_run.ui_responses:
            request = requests_by_key.get((method, title))
            if request is None:
                request = wait_for_request((method, title))
            if request is None:
                return
            request_id = request.get("id")
            if not isinstance(request_id, str) or not request_id:
                scenario_failures.append(f"RPC extension UI request {method!r}/{title!r} omitted its non-empty id")
                return
            if not unknown_responses_sent:
                for unknown_id in input_run.ui_unknown_response_ids:
                    send_ui_response(unknown_id, {"value": "ignored unknown response"})
                unknown_responses_sent = True
            send_ui_response(request_id, response)
            if input_run.ui_duplicate_late_response:
                send_ui_response(request_id, {"value": "late duplicate response"})
        if input_run.ui_duplicate_late_response:
            send_ui_response("r14-unknown-ui-request", {"cancelled": True})

    try:
        for index, (payload, delay) in enumerate(input_run.chunks):
            if input_run.stdout_pause_before_input_index == index:
                stdout_reader_release.clear()
            if delay:
                time.sleep(delay)
            input_chunks.append(payload)
            try:
                process.stdin.write(payload)
                process.stdin.flush()
            except (BrokenPipeError, OSError) as error:
                input_error = f"{type(error).__name__}: {error}"
                break
            if input_run.ui_responses and index == input_run.ui_response_after_input_index:
                answer_extension_ui()
            for count, timeout_seconds in provider_waits.get(index, []):
                if provider is None or not provider.wait_for_requests(provider_request_baseline + count,
                                                                      timeout_seconds):
                    scenario_failures.append(f"provider did not receive request {count} after input chunk {index}")
            for marker, occurrence, timeout_seconds in stdout_waits.get(index, []):
                if not wait_for_stdout(marker, occurrence, timeout_seconds):
                    scenario_failures.append(
                        f"stdout marker {marker!r} occurrence {occurrence} was not observed after input chunk {index}"
                    )
            for timeout_seconds in tool_start_waits.get(index, []):
                if provider is None:
                    scenario_failures.append("tool start wait requested without a local fixture provider")
                    continue
                started_path = provider._tool_gate_path.with_suffix(".started")
                deadline = time.monotonic() + timeout_seconds
                while not started_path.is_file() and time.monotonic() < deadline:
                    time.sleep(0.01)
                if not started_path.is_file():
                    scenario_failures.append(
                        f"fixture tool did not start after input chunk {index} within {timeout_seconds:g}s"
                    )
            if input_run.release_provider_after_input_index == index:
                if provider is None:
                    scenario_failures.append("provider release requested without a local fixture provider")
                else:
                    provider.release_held_response()
            if input_run.release_tool_gate_after_input_index == index:
                if provider is None:
                    scenario_failures.append("tool gate release requested without a local fixture provider")
                else:
                    provider._tool_gate_path.with_suffix(".release").write_text("release\n", encoding="utf-8")
            if input_run.stdout_pause_after_input_index == index:
                if input_run.stdout_pause_seconds < 0:
                    scenario_failures.append("stdout pause duration cannot be negative")
                else:
                    time.sleep(input_run.stdout_pause_seconds)
                    if input_run.close_stdout_after_input_index != index:
                        stdout_reader_release.set()
            if input_run.close_stdout_after_input_index == index:
                stdout_reader_stop.set()
                stdout_reader_release.set()
                stdout_reader.join(timeout=2)
                try:
                    process.stdout.close()
                except OSError:
                    pass
            if input_run.close_stderr_after_input_index == index:
                stderr_reader_stop.set()
                stderr_reader_release.set()
                stderr_reader.join(timeout=2)
                try:
                    process.stderr.close()
                except OSError:
                    pass
            if input_run.terminate_after_input_index == index:
                try:
                    termination_signal = getattr(signal, input_run.termination_signal)
                except AttributeError as error:
                    raise VerificationError(f"unknown termination signal: {input_run.termination_signal}") from error
                try:
                    os.killpg(process.pid, termination_signal)
                except ProcessLookupError:
                    pass
                break
        stdout_reader_release.set()
        stderr_reader_release.set()
        try:
            process.stdin.close()
        except OSError as error:
            input_error = input_error or f"{type(error).__name__}: {error}"
        timed_out = False
        try:
            exit_code = process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            timed_out = True
            os.killpg(process.pid, signal.SIGKILL)
            exit_code = process.wait(timeout=10)
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=10)
    stdout_reader.join(timeout=10)
    stderr_reader.join(timeout=10)
    process_group_alive = False
    try:
        os.killpg(process.pid, 0)
        process_group_alive = True
    except ProcessLookupError:
        pass
    except PermissionError:
        process_group_alive = True
    stdout = b"".join(stdout_chunks)
    stderr = b"".join(stderr_chunks)
    lines: list[dict[str, Any]] = []
    parse_failures: list[str] = []
    for raw_line in stdout.splitlines():
        if not raw_line:
            parse_failures.append("")
            lines.append({"rawLine": ""})
            continue
        try:
            lines.append(json.loads(raw_line.decode("utf-8")))
        except (UnicodeError, json.JSONDecodeError) as error:
            decoded = raw_line.decode("utf-8", errors="replace")
            parse_failures.append(decoded)
            lines.append({"rawLine": decoded, "parseError": type(error).__name__})
    session_files: list[dict[str, Any]] = []
    session_files_root: str | None = None
    if input_run.capture_session_files:
        fixture_root = workspace.parents[1].resolve()
        session_files_root = str(fixture_root)
        session_root = (workspace / product / "sessions").resolve()
        paths: dict[Path, str] = {}
        if session_root.is_dir():
            for path in session_root.rglob("*"):
                if path.is_file() and (path.name.endswith(".jsonl") or path.name.endswith(".session.json")):
                    paths[path.resolve()] = "session"
        fixture_files = input_run.fixture_files + (
            input_run.pi_fixture_files if product == "pi" else input_run.pisharp_fixture_files
        )
        for relative_path, _ in fixture_files:
            path = (fixture_root / relative_path).resolve()
            if path.is_file():
                paths[path] = "fixture"
        for path, owner in sorted(paths.items(), key=lambda item: (item[1], item[0].as_posix())):
            session_files.append({
                "path": str(path),
                "owner": owner,
                "relativePath": (path.relative_to(session_root).as_posix() if owner == "session"
                                 else path.relative_to(fixture_root).as_posix()),
                "content": path.read_bytes(),
            })
    export_files: list[dict[str, Any]] = []
    if input_run.capture_export_files:
        fixture_root = workspace.parents[1].resolve()
        for record in lines:
            if (not isinstance(record, dict) or record.get("type") != "response" or
                    record.get("command") != "export_html" or record.get("success") is not True):
                continue
            data = record.get("data")
            export_path = data.get("path") if isinstance(data, dict) else None
            if not isinstance(export_path, str) or not export_path:
                continue
            path = Path(export_path)
            path = (path if path.is_absolute() else cwd / path).resolve()
            if not path.is_relative_to(fixture_root):
                continue
            if path.is_file():
                export_files.append({
                    "path": str(path),
                    "relativePath": path.relative_to(fixture_root).as_posix(),
                    "content": path.read_bytes(),
                })
    return {
        "stdin": b"".join(input_chunks), "stdout": stdout, "stderr": stderr,
        "exitCode": exit_code, "timedOut": timed_out, "inputError": input_error,
        "records": lines, "parseFailures": parse_failures, "scenarioFailures": scenario_failures,
        "providerRequestCount": provider.request_count - provider_request_baseline if provider is not None else 0,
        "providerRequests": provider.request_bodies[provider_request_baseline:] if provider is not None else [],
        "providerErrors": provider.errors if provider is not None else [],
        "providerDisconnectCount": provider.client_disconnect_count if provider is not None else 0,
        "processGroupAlive": process_group_alive,
        "sessionFiles": session_files,
        "sessionFilesRoot": session_files_root,
        "exportFiles": export_files,
        "processCwd": str(cwd.resolve()),
        "processWorkspaceRoot": str(workspace.parents[1].resolve()),
    }


def _project(value: Any, *, preserve_response_error_text: bool = False,
             preserve_state_flags: bool = False, preserve_queue_state: bool = False,
             preserve_queue_response_data: bool = False,
             preserve_response_data_commands: tuple[str, ...] = (),
             preserve_session_identity: bool = False,
             session_identity_tokens: dict[str, dict[str, str]] | None = None) -> Any:
    if session_identity_tokens is None:
        session_identity_tokens = {"sessionId": {}, "sessionFile": {}}
    if isinstance(value, list):
        return [_project(item, preserve_response_error_text=preserve_response_error_text,
                         preserve_state_flags=preserve_state_flags,
                         preserve_queue_state=preserve_queue_state,
                         preserve_queue_response_data=preserve_queue_response_data,
                         preserve_response_data_commands=preserve_response_data_commands,
                         preserve_session_identity=preserve_session_identity,
                         session_identity_tokens=session_identity_tokens) for item in value]
    if isinstance(value, str):
        return re.sub(r"^(https?://127\.0\.0\.1:)\d+(/v1(?:/.*)?)$", r"\1<fixture-port>\2", value)
    if not isinstance(value, dict):
        return value
    result: dict[str, Any] = {}
    for key, item in value.items():
        if key in VOLATILE_FIELDS:
            if preserve_session_identity and key in session_identity_tokens:
                if isinstance(item, str):
                    tokens = session_identity_tokens[key]
                    result[key] = tokens.setdefault(item, f"{key}-{len(tokens)}")
                else:
                    result[key] = item
            continue
        if key == "error" and value.get("type") == "response":
            result["errorPresent"] = True
            result["errorIsNonEmptyString"] = isinstance(item, str) and bool(item)
            if preserve_response_error_text:
                result["errorText"] = item
            continue
        if key == "data" and value.get("type") == "response":
            result["dataPresent"] = True
            if value.get("command") == "get_state" and isinstance(item, dict):
                state: dict[str, Any] = {}
                if preserve_state_flags:
                    state.update({name: item[name] for name in ("isStreaming", "isCompacting") if name in item})
                if preserve_queue_state:
                    state.update({name: item[name] for name in
                                  ("steeringMode", "followUpMode", "pendingMessageCount") if name in item})
                if state:
                    result["state"] = state
            if preserve_queue_response_data and value.get("command") == "clear_queue" and isinstance(item, dict):
                result["queueData"] = item
            if value.get("command") in preserve_response_data_commands:
                result["data"] = _project(item, preserve_response_error_text=preserve_response_error_text,
                                           preserve_state_flags=preserve_state_flags,
                                           preserve_queue_state=preserve_queue_state,
                                           preserve_queue_response_data=preserve_queue_response_data,
                                           preserve_response_data_commands=preserve_response_data_commands,
                                           preserve_session_identity=preserve_session_identity,
                                           session_identity_tokens=session_identity_tokens)
            continue
        result[key] = _project(item, preserve_response_error_text=preserve_response_error_text,
                               preserve_state_flags=preserve_state_flags,
                               preserve_queue_state=preserve_queue_state,
                               preserve_queue_response_data=preserve_queue_response_data,
                               preserve_response_data_commands=preserve_response_data_commands,
                               preserve_session_identity=preserve_session_identity,
                               session_identity_tokens=session_identity_tokens)
    if value.get("type") == "response" and "data" not in value:
        result["dataPresent"] = False
    if value.get("type") == "response" and "error" not in value:
        result["errorPresent"] = False
    return result


def _normalize_workspace_paths(value: Any, workspace_root: str) -> Any:
    if isinstance(value, str):
        return value.replace(workspace_root, "<rpc-root>")
    if isinstance(value, list):
        return [_normalize_workspace_paths(item, workspace_root) for item in value]
    if isinstance(value, dict):
        return {key: _normalize_workspace_paths(item, workspace_root) for key, item in value.items()}
    return value


def _normalize_extension_paths(value: Any) -> Any:
    """Retain extension-path presence while removing product-specific fixture install paths."""
    if isinstance(value, list):
        for item in value:
            _normalize_extension_paths(item)
    elif isinstance(value, dict):
        if value.get("type") == "extension_error" and isinstance(value.get("extensionPath"), str):
            value["extensionPath"] = "<extension-path>"
        for item in value.values():
            _normalize_extension_paths(item)
    return value


def _normalize_session_entry_ids(value: Any) -> Any:
    """Normalize generated session entry identifiers while preserving RPC request IDs."""
    entries: list[dict[str, Any]] = []

    def collect(item: Any) -> None:
        if isinstance(item, list):
            for child in item:
                collect(child)
        elif isinstance(item, dict):
            if isinstance(item.get("id"), str) and "parentId" in item and isinstance(item.get("type"), str):
                entries.append(item)
            for child in item.values():
                collect(child)

    collect(value)
    identities: dict[str, str] = {}
    for entry in entries:
        identities.setdefault(entry["id"], f"entry-{len(identities)}")
    for entry in entries:
        entry["id"] = identities[entry["id"]]
        parent = entry.get("parentId")
        if isinstance(parent, str):
            entry["parentId"] = identities.get(parent, "external-parent")

    def normalize_leaf_ids(item: Any) -> None:
        if isinstance(item, list):
            for child in item:
                normalize_leaf_ids(child)
        elif isinstance(item, dict):
            for key, child in item.items():
                if key in {"leafId", "entryId", "targetId", "firstKeptEntryId", "fromId"} and isinstance(child, str):
                    item[key] = identities.get(child, f"external-{key}")
                else:
                    normalize_leaf_ids(child)

    normalize_leaf_ids(value)
    return value


def _canonicalize_ui_request_ids(value: Any) -> None:
    """Replace generated UUIDs with ordered labels while preserving request identity and order."""
    if not isinstance(value, list):
        return
    index = 0
    for record in value:
        if isinstance(record, dict) and record.get("type") == "extension_ui_request":
            record["id"] = f"rpc-ui-request-{index}"
            index += 1


def _event_type(record: Any) -> str | None:
    if not isinstance(record, dict):
        return None
    event_type = record.get("type")
    if event_type == "event" and isinstance(record.get("data"), dict):
        event_type = record["data"].get("Type", record["data"].get("type"))
    return event_type if isinstance(event_type, str) else None


def _canonical_provider_request(value: dict[str, Any]) -> dict[str, Any]:
    """Compare tool arguments by their JSON value while retaining every argument field."""
    request = json.loads(json.dumps(value))
    for message in request.get("messages", []):
        if not isinstance(message, dict):
            continue
        for tool_call in message.get("tool_calls", []):
            if not isinstance(tool_call, dict):
                continue
            function = tool_call.get("function")
            if not isinstance(function, dict):
                continue
            arguments = function.get("arguments")
            if isinstance(arguments, str):
                try:
                    function["arguments"] = json.loads(arguments)
                except json.JSONDecodeError:
                    pass
    return request


def _canonical_session_file(capture: dict[str, Any], identity_tokens: dict[str, dict[str, str]],
                            fixture_root: str) -> dict[str, Any]:
    raw = capture["content"]
    text = raw.decode("utf-8", errors="strict")
    path = capture["path"]
    if path.endswith(".jsonl"):
        try:
            records = [json.loads(line) for line in text.splitlines() if line]
        except json.JSONDecodeError:
            return {"owner": capture["owner"], "path": capture["relativePath"], "invalidJsonl": text}
        if not records or not isinstance(records[0], dict) or records[0].get("type") != "session":
            return {"owner": capture["owner"], "path": capture["relativePath"], "invalidJsonl": text}
        header = dict(records[0])
        entries = [dict(entry) for entry in records[1:]]
    else:
        try:
            document = json.loads(text)
        except json.JSONDecodeError:
            return {"owner": capture["owner"], "path": capture["relativePath"], "invalidDocument": text}
        if not isinstance(document, dict) or not isinstance(document.get("Entries"), list):
            return {"owner": capture["owner"], "path": capture["relativePath"], "invalidDocument": document}
        header = dict(document.get("PiHeader") or {}) if isinstance(document.get("PiHeader"), dict) else {}
        header.update({
            "type": "session",
            "id": document.get("Id"),
            "cwd": document.get("WorkingDirectory"),
        })
        header.setdefault("version", 3)
        parent_session = document.get("ParentSessionPath")
        if parent_session is not None:
            header["parentSession"] = parent_session
        elif header.get("parentSession") is None:
            header.pop("parentSession", None)
        entries = []
        for entry in document["Entries"]:
            if not isinstance(entry, dict):
                entries.append(entry)
                continue
            projected = {
                "type": entry.get("Type"), "id": entry.get("Id"),
                "parentId": entry.get("ParentId"), "timestamp": entry.get("Timestamp"),
            }
            payload = entry.get("Payload")
            if isinstance(payload, dict):
                original = payload.get("PiEntry", payload.get("piOriginalEntry"))
                if isinstance(original, dict):
                    projected_original = dict(original)
                    projected_original["id"] = entry.get("Id")
                    projected_original["parentId"] = entry.get("ParentId")
                    projected_original["timestamp"] = entry.get("Timestamp")
                    entries.append(projected_original)
                    continue
            if isinstance(payload, dict):
                projected.update(payload)
                if projected["type"] == "model_change" and isinstance(payload.get("model"), str):
                    projected["modelId"] = projected.pop("model")
                    projected.pop("endpoint", None)
            entries.append(projected)

    session_id = header.pop("id", None)
    header["sessionId"] = session_id
    parent = header.get("parentSession")
    if isinstance(parent, str) and not parent.startswith("<rpc-root>/"):
        parent_path = os.path.abspath(parent if os.path.isabs(parent) else os.path.join(fixture_root, parent))
        if parent_path in identity_tokens["sessionFile"]:
            header["parentSession"] = identity_tokens["sessionFile"][parent_path]
        elif parent_path.startswith(fixture_root + os.sep):
            header["parentSession"] = "<rpc-root>/" + os.path.relpath(parent_path, fixture_root).replace(os.sep, "/")
    elif parent is None:
        header.pop("parentSession", None)
    cwd = header.get("cwd")
    if isinstance(cwd, str):
        cwd_path = os.path.abspath(cwd)
        if cwd_path == fixture_root:
            header["cwd"] = "<rpc-root>"
        elif cwd_path.startswith(fixture_root + os.sep):
            header["cwd"] = "<rpc-root>/" + os.path.relpath(cwd_path, fixture_root).replace(os.sep, "/")
    return {"owner": capture["owner"], "header": header, "entries": entries}


def _project_session_files(result: dict[str, Any], input_run: InputRun,
                           identity_tokens: dict[str, dict[str, str]]) -> list[dict[str, Any]]:
    fixture_root = result.get("sessionFilesRoot")
    if not isinstance(fixture_root, str):
        return []
    captures = result.get("sessionFiles", [])
    parsed = [(capture, _canonical_session_file(capture, identity_tokens, fixture_root)) for capture in captures]
    groups: dict[tuple[str, str], list[tuple[dict[str, Any], dict[str, Any]]]] = {}
    for capture, document in parsed:
        if capture["owner"] == "session" and isinstance(document.get("header"), dict) and \
                isinstance(document["header"].get("sessionId"), str):
            group = ("session", document["header"]["sessionId"])
        else:
            group = (capture["owner"], capture["relativePath"])
        groups.setdefault(group, []).append((capture, document))

    projected = []
    for group, copies in groups.items():
        paths = [os.path.abspath(capture["path"]) for capture, _ in copies if capture["owner"] == "session"]
        known_tokens = [identity_tokens["sessionFile"][path] for path in paths
                        if path in identity_tokens["sessionFile"]]
        file_token = known_tokens[0] if known_tokens else None
        if group[0] == "session":
            if file_token is None:
                file_token = f"sessionFile-{len(identity_tokens['sessionFile'])}"
            for path in paths:
                identity_tokens["sessionFile"][path] = file_token
            identity_tokens["sessionId"].setdefault(group[1], f"sessionId-{len(identity_tokens['sessionId'])}")

        normalized_copies = []
        for capture, original in copies:
            document = json.loads(json.dumps(original))
            header = document.get("header")
            if isinstance(header, dict):
                parent = header.get("parentSession")
                if isinstance(parent, str) and not parent.startswith("<rpc-root>/"):
                    parent_path = os.path.abspath(parent if os.path.isabs(parent)
                                                  else os.path.join(fixture_root, parent))
                    if parent_path in identity_tokens["sessionFile"]:
                        header["parentSession"] = identity_tokens["sessionFile"][parent_path]
                    elif parent_path.startswith(fixture_root + os.sep):
                        header["parentSession"] = "<rpc-root>/" + os.path.relpath(
                            parent_path, fixture_root).replace(os.sep, "/")
            document["path"] = file_token if capture["owner"] == "session" else \
                "fixture/" + capture["relativePath"]
            normalized_copies.append(_project(document, preserve_session_identity=True,
                                               session_identity_tokens=identity_tokens))
        if input_run.normalize_session_entry_ids:
            for document in normalized_copies:
                _normalize_session_entry_ids(document)
        representative = normalized_copies[0]
        if len(normalized_copies) > 1:
            semantic_copies = [{key: value for key, value in document.items() if key != "path"}
                               for document in normalized_copies]
            copies_match = all(
                document == semantic_copies[0] for document in semantic_copies[1:])
            if not copies_match:
                representative["equivalentPhysicalCopies"] = False
        projected.append(representative)

    for document in projected:
        header = document.get("header")
        parent = header.get("parentSession") if isinstance(header, dict) else None
        if isinstance(parent, str) and parent.startswith("<rpc-root>/"):
            parent_path = os.path.abspath(os.path.join(fixture_root, parent[len("<rpc-root>/"):]))
            if parent_path in identity_tokens["sessionFile"]:
                header["parentSession"] = identity_tokens["sessionFile"][parent_path]
    return sorted(projected, key=lambda item: (item["owner"], item["path"]))


class _ExportMessageParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.messages: list[dict[str, str]] = []
        self._role: str | None = None
        self._in_article = 0
        self._in_heading = 0
        self._in_pre = 0
        self._heading_text: list[str] = []
        self._pre_text: list[str] = []
        self._message_text: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag == "article":
            self._in_article += 1
        elif self._in_article and tag == "h2":
            self._in_heading += 1
        elif self._in_article and tag == "pre":
            self._in_pre += 1
            self._pre_text = []

    def handle_endtag(self, tag: str) -> None:
        if tag == "h2" and self._in_heading:
            self._in_heading -= 1
            self._role = "".join(self._heading_text).strip()
            self._heading_text = []
        elif tag == "pre" and self._in_pre:
            self._in_pre -= 1
            self._message_text.extend(self._pre_text)
            self._pre_text = []
        elif tag == "article" and self._in_article:
            self._in_article -= 1
            if self._role is not None:
                self.messages.append({"role": self._role, "text": "".join(self._message_text)})
            self._role = None
            self._message_text = []
            self._pre_text = []
            self._heading_text = []
            self._in_heading = 0
            self._in_pre = 0

    def handle_data(self, data: str) -> None:
        if self._in_heading:
            self._heading_text.append(data)
        elif self._in_pre:
            self._pre_text.append(data)


def _export_semantic_data(content: bytes) -> dict[str, Any]:
    text = content.decode("utf-8", errors="strict")
    embedded = re.search(r'id="session-data"[^>]*>([A-Za-z0-9+/=]+)</script>', text)
    if embedded is not None:
        session_data = json.loads(base64.b64decode(embedded.group(1)))
        messages: list[dict[str, str]] = []
        for entry in session_data.get("entries", []):
            message = entry.get("message") if isinstance(entry, dict) else None
            if not isinstance(message, dict) or not isinstance(message.get("role"), str):
                continue
            parts = [part["text"] for part in message.get("content", [])
                     if isinstance(part, dict) and isinstance(part.get("text"), str)]
            messages.append({"role": message["role"], "text": "".join(parts)})
        return {
            "messages": messages,
            "systemPrompt": session_data.get("systemPrompt"),
            "tools": session_data.get("tools", []),
        }
    parser = _ExportMessageParser()
    parser.feed(text)
    return {"messages": parser.messages, "systemPrompt": None, "tools": []}


def _export_semantic_messages(content: bytes) -> list[dict[str, str]]:
    return _export_semantic_data(content)["messages"]


def _observation(run_results: list[dict[str, Any]], input_runs: tuple[InputRun, ...]) -> dict[str, Any]:
    def records_for(result: dict[str, Any], input_run: InputRun) -> list[Any]:
        records = result["records"]
        if input_run.response_only:
            return [record for record in records
                    if isinstance(record, dict) and record.get("type") == "response"]
        if not input_run.lifecycle_event_types:
            return records
        projected = []
        for record in records:
            if not isinstance(record, dict):
                continue
            if record.get("type") == "response":
                projected.append(record)
                continue
            if input_run.ui_expected_requests and record.get("type") == "extension_ui_request":
                projected.append(record)
                continue
            event_type = _event_type(record)
            if event_type in input_run.lifecycle_event_types:
                message = record.get("message") if isinstance(record, dict) else None
                if (event_type in ("message_start", "message_end") and isinstance(message, dict) and
                        message.get("role") == "system" and not input_run.preserve_system_message_payloads):
                    continue
                if (input_run.preserve_system_message_payloads and event_type in ("message_start", "message_end")
                        and isinstance(message, dict) and message.get("role") == "system"):
                    projected.append(record)
                elif input_run.preserve_system_message_payloads and event_type == "agent_end":
                    messages = record.get("messages")
                    system_message = next((item for item in messages if isinstance(item, dict)
                                           and item.get("role") == "system"), None) \
                        if isinstance(messages, list) else None
                    projected.append({"type": event_type, "systemMessage": system_message})
                elif (input_run.preserve_assistant_terminal_shape and event_type in ("message_end", "turn_end")
                        and isinstance(message, dict)):
                    projected.append({
                        "type": event_type,
                        "message": {
                            "role": message.get("role"),
                            **({
                                "stopReason": message.get("stopReason"),
                                "errorMessagePresent": isinstance(message.get("errorMessage"), str)
                                and bool(message.get("errorMessage")),
                            } if message.get("role") == "assistant" else {}),
                        },
                    })
                elif input_run.preserve_message_update_deltas_only and event_type == "message_update":
                    projected.append({"type": event_type,
                                      "assistantMessageEvent": record.get("assistantMessageEvent")})
                elif input_run.preserve_agent_end_messages and event_type == "agent_end":
                    messages = record.get("messages")
                    projected.append({
                        "type": event_type,
                        "messages": [message for message in messages
                                     if isinstance(message, dict) and message.get("role") != "system"]
                        if isinstance(messages, list) else [],
                        "willRetry": record.get("willRetry"),
                    })
                elif input_run.preserve_event_payloads or event_type in input_run.preserve_event_payload_types:
                    projected.append(record)
                else:
                    projected.append(record if input_run.preserve_event_payloads else {"type": event_type})
        return projected

    projected_runs = []
    for input_run, result in zip(input_runs, run_results, strict=True):
        identity_tokens = {"sessionId": {}, "sessionFile": {}}
        projected_records = _project(
            records_for(result, input_run),
            preserve_response_error_text=input_run.preserve_response_error_text,
            preserve_state_flags=input_run.preserve_state_flags,
            preserve_queue_state=input_run.preserve_queue_state,
            preserve_queue_response_data=input_run.preserve_queue_response_data,
            preserve_response_data_commands=input_run.preserve_response_data_commands,
            preserve_session_identity=input_run.preserve_session_identity,
            session_identity_tokens=identity_tokens,
        )
        if input_run.ui_expected_requests:
            _canonicalize_ui_request_ids(projected_records)
        projected_run = {
                "exitCode": result["exitCode"],
                "timedOut": result["timedOut"],
                "inputError": result["inputError"],
                "records": projected_records,
                "stderrCompared": input_run.compare_stderr,
                "parseFailures": result["parseFailures"],
        }
        if input_run.compare_stderr:
            stderr = result["stderr"].decode("utf-8", errors="replace")
            if input_run.normalize_workspace_paths:
                stderr = stderr.replace(result["processWorkspaceRoot"], "<rpc-root>")
            projected_run["stderr"] = stderr
        if input_run.normalize_workspace_paths:
            projected_run["records"] = _normalize_workspace_paths(
                projected_run["records"], result["processWorkspaceRoot"])
        if input_run.normalize_extension_paths:
            _normalize_extension_paths(projected_run["records"])
        if input_run.compare_provider_requests:
            projected_run["providerRequests"] = [
                _canonical_provider_request(json.loads(body.decode("utf-8")))
                for body in result["providerRequests"]
            ]
        if input_run.expected_provider_disconnects is not None:
            projected_run["providerDisconnectCount"] = result.get("providerDisconnectCount", 0)
        if input_run.assert_process_group_disposed:
            projected_run["processGroupAlive"] = result.get("processGroupAlive", False)
        if input_run.capture_session_files and input_run.compare_session_files:
            projected_run["sessionFiles"] = _project_session_files(result, input_run, identity_tokens)
        if input_run.compare_export_files:
            export_files = []
            for item in result.get("exportFiles", []):
                semantic_data = _export_semantic_data(item["content"])
                if input_run.normalize_workspace_paths:
                    semantic_data = _normalize_workspace_paths(semantic_data, result["processWorkspaceRoot"])
                export_files.append({"filename": Path(item["relativePath"]).name, **semantic_data})
            projected_run["exportFiles"] = export_files
        if input_run.normalize_session_entry_ids:
            projected_run["records"] = _normalize_session_entry_ids(projected_run["records"])
        projected_runs.append(projected_run)
    return {"runs": projected_runs}


def _check_run(input_run: InputRun, result: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    if result["timedOut"]:
        errors.append("process timed out")
    if input_run.expected_exit_code is not None and result["exitCode"] != input_run.expected_exit_code:
        errors.append(f"process exited {result['exitCode']}; expected {input_run.expected_exit_code}")
    errors.extend(result.get("scenarioFailures", []))
    errors.extend(f"local provider error: {error}" for error in result.get("providerErrors", []))
    if (input_run.expected_provider_disconnects is not None and
            result.get("providerDisconnectCount", 0) != input_run.expected_provider_disconnects):
        errors.append(f"provider client disconnected {result.get('providerDisconnectCount', 0)} times; expected "
                      f"{input_run.expected_provider_disconnects}")
    if input_run.assert_process_group_disposed and result.get("processGroupAlive", False):
        errors.append("process exited with child processes still in its process group")
    if input_run.ui_expected_requests:
        ui_requests = [record for record in result["records"]
                       if isinstance(record, dict) and record.get("type") == "extension_ui_request"]
        expected_requests = input_run.ui_expected_requests
        if len(ui_requests) != len(expected_requests):
            errors.append(f"RPC extension UI emitted {len(ui_requests)} requests; expected {len(expected_requests)}")
        request_ids: list[str] = []
        for index, record in enumerate(ui_requests):
            request_id = record.get("id")
            if not isinstance(request_id, str) or not request_id:
                errors.append(f"RPC extension UI request {index} has no non-empty id")
            else:
                request_ids.append(request_id)
                try:
                    uuid.UUID(request_id)
                except ValueError:
                    errors.append(f"RPC extension UI request {index} id is not a UUID: {request_id!r}")
        if len(request_ids) != len(set(request_ids)):
            errors.append("RPC extension UI request IDs are not unique")
        for index, (record, expected) in enumerate(zip(ui_requests, expected_requests)):
            observed = {key: value for key, value in record.items() if key != "id"}
            if observed != expected:
                errors.append(f"RPC extension UI request {index} was {observed!r}; expected exactly {expected!r}")
    if input_run.rpc_ui_extension_error:
        extension_errors = [record for record in result["records"] if _event_type(record) == "extension_error"]
        if len(extension_errors) != 1:
            errors.append(f"RPC extension error event occurred {len(extension_errors)} times; expected exactly once")
        else:
            event = extension_errors[0]
            data = event.get("data") if isinstance(event.get("data"), dict) else {}
            details = data.get("Details") if isinstance(data.get("Details"), dict) else {}
            extension_path = event.get("extensionPath", details.get("extensionPath"))
            event_name = event.get("event", details.get("eventType"))
            error_text = event.get("error", data.get("Error"))
            if not isinstance(extension_path, str) or not extension_path:
                errors.append("RPC extension error event omitted extensionPath")
            if event_name != "before_agent_start":
                errors.append(f"RPC extension error event named {event_name!r}; expected 'before_agent_start'")
            if error_text != "R14 extension handler failed":
                errors.append(f"RPC extension error text was {error_text!r}")
    if input_run.expected_persisted_session_text:
        persisted_sessions = [
            capture.get("content", b"").decode("utf-8", errors="replace")
            for capture in result.get("sessionFiles", [])
            if capture.get("owner") == "session"
        ]
        for marker in input_run.expected_persisted_session_text:
            if not any(marker in content for content in persisted_sessions):
                errors.append(f"no persisted session file contains expected text {marker!r}")
    if input_run.forbidden_persisted_session_text:
        persisted_sessions = [
            capture.get("content", b"").decode("utf-8", errors="replace")
            for capture in result.get("sessionFiles", [])
            if capture.get("owner") == "session"
        ]
        for marker in input_run.forbidden_persisted_session_text:
            if any(marker in content for content in persisted_sessions):
                errors.append(f"persisted session unexpectedly contains {marker!r}")
    if input_run.session_file_expectations:
        session_captures = [capture for capture in result.get("sessionFiles", [])
                            if capture.get("owner") == "session"]
        for session_id, required_texts, forbidden_texts in input_run.session_file_expectations:
            matching = [capture["content"].decode("utf-8", errors="replace") for capture in session_captures
                        if session_id in capture["content"].decode("utf-8", errors="replace")]
            if not matching:
                errors.append(f"no persisted session file was captured for session {session_id!r}")
                continue
            satisfies = [content for content in matching
                         if all(marker in content for marker in required_texts) and
                         all(marker not in content for marker in forbidden_texts)]
            if not satisfies:
                errors.append(f"session {session_id!r} has no captured file with required markers "
                              f"{required_texts!r} and without {forbidden_texts!r}")
    if input_run.session_file_state_expectations:
        responses_by_id = {
            record.get("id"): record for record in result["records"]
            if isinstance(record, dict) and record.get("type") == "response" and isinstance(record.get("id"), str)
        }
        session_captures = [capture for capture in result.get("sessionFiles", [])
                            if capture.get("owner") == "session"]
        for response_id, expected_path_prefix, required_texts, forbidden_texts in \
                input_run.session_file_state_expectations:
            response = responses_by_id.get(response_id)
            data = response.get("data") if isinstance(response, dict) else None
            session_path = data.get("sessionFile") if isinstance(data, dict) else None
            session_id = data.get("sessionId") if isinstance(data, dict) else None
            matching = [capture for capture in session_captures
                        if isinstance(session_path, str) and
                        Path(capture.get("path", "")).resolve() == Path(session_path).resolve()]
            if len(matching) != 1:
                errors.append(f"state response {response_id!r} sessionFile did not resolve to exactly one captured session file")
                continue
            capture = matching[0]
            relative_path = capture.get("relativePath", "")
            if expected_path_prefix is not None and not relative_path.startswith(expected_path_prefix):
                errors.append(f"state response {response_id!r} selected path {relative_path!r}; expected prefix "
                              f"{expected_path_prefix!r}")
            try:
                document = json.loads(capture["content"].decode("utf-8"))
            except json.JSONDecodeError:
                document = next((json.loads(line) for line in capture["content"].decode("utf-8").splitlines()
                                 if line.strip()), {})
            document_id = document.get("Id", document.get("id")) if isinstance(document, dict) else None
            if session_id is not None and document_id != session_id:
                errors.append(f"state response {response_id!r} session ID {session_id!r} does not own its selected file")
            content = capture["content"].decode("utf-8", errors="replace")
            for marker in required_texts:
                if marker not in content:
                    errors.append(f"state response {response_id!r} selected session file is missing {marker!r}")
            for marker in forbidden_texts:
                if marker in content:
                    errors.append(f"state response {response_id!r} selected session file unexpectedly contains {marker!r}")
    for suffix, expected_text in input_run.tool_side_effect_expectations:
        gate_path = Path(result["processWorkspaceRoot"]) / "rpc-tool-gate"
        effect_path = gate_path.with_suffix(suffix)
        try:
            observed_text = effect_path.read_text(encoding="utf-8")
        except FileNotFoundError:
            observed_text = None
        if observed_text != expected_text:
            errors.append(f"fixture tool side effect {effect_path.name!r} was {observed_text!r}; "
                          f"expected exactly {expected_text!r}")
    provider_count = result.get("providerRequestCount", 0)
    provider_requests = result.get("providerRequests", [])
    if provider_count != len(provider_requests):
        errors.append(f"provider received {provider_count} requests but {len(provider_requests)} request bodies were captured")
    if input_run.expected_provider_model_ids is not None:
        observed_models = tuple(
            json.loads(body.decode("utf-8")).get("model") for body in provider_requests
        )
        if observed_models != input_run.expected_provider_model_ids:
            errors.append(
                f"provider request models {observed_models!r} differ from "
                f"{input_run.expected_provider_model_ids!r}"
            )
    if provider_count < input_run.expected_min_provider_requests:
        errors.append(
            f"local provider received {provider_count} requests; expected at least "
            f"{input_run.expected_min_provider_requests}"
        )
    for marker in input_run.provider_request_text_excludes:
        for index, body in enumerate(provider_requests):
            request_text = json.dumps(json.loads(body.decode("utf-8")), ensure_ascii=False)
            if marker in request_text:
                errors.append(f"provider request {index} unexpectedly contains excluded text {marker!r}")
    for request_index, required_markers, forbidden_markers in input_run.provider_request_expectations:
        if request_index >= len(provider_requests):
            errors.append(f"provider request {request_index} is missing for its content assertions")
            continue
        request_text = json.dumps(json.loads(provider_requests[request_index].decode("utf-8")), ensure_ascii=False)
        for marker in required_markers:
            if marker not in request_text:
                errors.append(f"provider request {request_index} is missing expected history marker {marker!r}")
        for marker in forbidden_markers:
            if marker in request_text:
                errors.append(f"provider request {request_index} unexpectedly contains history marker {marker!r}")
    responses = [record for record in result["records"] if isinstance(record, dict) and record.get("type") == "response"]
    for response_id, expected_commands in input_run.expected_project_commands:
        response = next((record for record in responses if record.get("id") == response_id), None)
        data = response.get("data") if response is not None else None
        commands = data.get("commands") if isinstance(data, dict) else None
        actual_commands = []
        if isinstance(commands, list):
            for command in commands:
                source_info = command.get("sourceInfo") if isinstance(command, dict) else None
                if not isinstance(source_info, dict):
                    actual_commands.append((command.get("name"), command.get("source"), None, None, None, None))
                    continue
                actual_commands.append((command.get("name"), command.get("source"), source_info.get("source"),
                                        source_info.get("scope"), source_info.get("origin"), source_info.get("path")))
        expected_names = [item[0] for item in expected_commands]
        observed_names = [item[0] for item in actual_commands]
        if observed_names != expected_names:
            errors.append(f"response {response_id!r} project command ordering {observed_names!r} differs from "
                          f"{expected_names!r}")
        for actual, expected in zip(actual_commands, expected_commands):
            name, command_source, resource_source, scope, origin, path_fragment = expected
            if actual[:5] != (name, command_source, resource_source, scope, origin):
                errors.append(f"response {response_id!r} project command metadata {actual[:5]!r} differs from "
                              f"{expected[:5]!r}")
            if not isinstance(actual[5], str) or path_fragment not in actual[5]:
                errors.append(f"response {response_id!r} command {name!r} path {actual[5]!r} does not contain "
                              f"{path_fragment!r}")
    for response_id, expected_names in input_run.expected_command_names:
        response = next((record for record in responses if record.get("id") == response_id), None)
        data = response.get("data") if response is not None else None
        commands = data.get("commands") if isinstance(data, dict) else None
        actual_names = [item.get("name") for item in commands if isinstance(item, dict)] \
            if isinstance(commands, list) else []
        if actual_names != list(expected_names):
            errors.append(f"response {response_id!r} command names {actual_names!r} differ from "
                          f"{list(expected_names)!r}")
        for command in commands if isinstance(commands, list) else []:
            if (not isinstance(command, dict) or command.get("name") not in expected_names or
                    not isinstance(command.get("sourceInfo"), dict)):
                errors.append(f"response {response_id!r} has a command without sourceInfo metadata")
                continue
            source_info = command["sourceInfo"]
            if not all(isinstance(source_info.get(field), str) and source_info[field]
                       for field in ("path", "source", "scope", "origin")):
                errors.append(f"response {response_id!r} command {command.get('name')!r} has incomplete "
                              "sourceInfo metadata")
            elif not Path(source_info["path"]).exists():
                errors.append(f"response {response_id!r} command {command.get('name')!r} source path does not exist")
    for response_id, expected_basename, required_texts, forbidden_texts in input_run.export_file_expectations:
        response = next((record for record in responses if record.get("id") == response_id), None)
        data = response.get("data") if response is not None else None
        export_path = data.get("path") if isinstance(data, dict) else None
        if not isinstance(export_path, str):
            errors.append(f"export response {response_id!r} has no path")
            continue
        if Path(export_path).name != expected_basename:
            errors.append(f"export response {response_id!r} basename {Path(export_path).name!r} differs from "
                          f"{expected_basename!r}")
        path = Path(export_path)
        if not path.is_absolute():
            path = Path(result["processCwd"]) / path
        matching = [item for item in result.get("exportFiles", [])
                    if Path(item.get("path", "")).resolve() == path.resolve()]
        if len(matching) != 1:
            errors.append(f"export response {response_id!r} path did not resolve to exactly one captured HTML file")
            continue
        content = html.unescape(matching[0]["content"].decode("utf-8", errors="replace"))
        semantic_text = " ".join(item["text"] for item in _export_semantic_messages(matching[0]["content"]))
        searchable_content = content + " " + semantic_text
        for marker in required_texts:
            if marker not in searchable_content:
                errors.append(f"export response {response_id!r} HTML is missing required content {marker!r}")
        for marker in forbidden_texts:
            if marker in searchable_content:
                errors.append(f"export response {response_id!r} HTML unexpectedly contains {marker!r}")
    if input_run.expected_project_contexts:
        workspace_root = result["processWorkspaceRoot"]
        actual_contexts = set()
        for line in result["stderr"].decode("utf-8", errors="replace").splitlines():
            if line.startswith("r12-project-context:"):
                actual_contexts.add(line.replace(workspace_root, "<rpc-root>"))
        expected_contexts = {f"r12-project-context:<rpc-root>/{name}:{str(trusted).lower()}"
                             for name, trusted in input_run.expected_project_contexts}
        if actual_contexts != expected_contexts:
            errors.append(f"project context observations {sorted(actual_contexts)!r} differ from "
                          f"{sorted(expected_contexts)!r}")
    if input_run.expected_response_ids is not None:
        observed_ids = [record["id"] if "id" in record else _ABSENT for record in responses]
        if tuple(observed_ids) != input_run.expected_response_ids:
            errors.append(f"response IDs {observed_ids!r} differ from {input_run.expected_response_ids!r}")
    if input_run.expected_response_commands is not None:
        observed_commands = tuple(record.get("command") for record in responses)
        if observed_commands != input_run.expected_response_commands:
            errors.append(
                f"response commands {observed_commands!r} differ from {input_run.expected_response_commands!r}"
            )
    if input_run.all_responses_succeed and any(record.get("success") is not True for record in responses):
        errors.append("one or more expected successful responses failed")
    if input_run.expected_successes is not None:
        observed_successes = tuple(record.get("success") is True for record in responses)
        if observed_successes != input_run.expected_successes:
            errors.append(f"response success flags {observed_successes!r} differ from {input_run.expected_successes!r}")
    if input_run.expected_data_presence is not None:
        observed_data_presence = tuple("data" in record for record in responses)
        if observed_data_presence != input_run.expected_data_presence:
            errors.append(
                f"response data presence {observed_data_presence!r} differs from "
                f"{input_run.expected_data_presence!r}"
            )
    if input_run.expected_error_presence is not None:
        observed_error_presence = tuple("error" in record for record in responses)
        if observed_error_presence != input_run.expected_error_presence:
            errors.append(
                f"response error presence {observed_error_presence!r} differs from "
                f"{input_run.expected_error_presence!r}"
            )
    for response_id, field, expected in input_run.response_data_expectations:
        response = next((record for record in responses if record.get("id") == response_id), None)
        data = response.get("data") if response is not None else None
        if not isinstance(data, dict) or data.get(field) != expected:
            observed = data.get(field) if isinstance(data, dict) else None
            errors.append(f"response {response_id!r} data field {field!r} was {observed!r}, expected {expected!r}")
    if input_run.expected_response_dispositions is not None:
        observed_dispositions = tuple(
            record.get("data", {}).get("disposition") if isinstance(record.get("data"), dict) else None
            for record in responses
        )
        if observed_dispositions != input_run.expected_response_dispositions:
            errors.append(
                f"response dispositions {observed_dispositions!r} differ from "
                f"{input_run.expected_response_dispositions!r}"
            )
    event_types = [_event_type(record) for record in result["records"]]
    for forbidden in input_run.forbidden_lifecycle_event_types:
        if forbidden in event_types:
            errors.append(f"forbidden lifecycle event {forbidden!r} was emitted")
    for event_type, minimum in input_run.minimum_lifecycle_event_counts:
        actual = event_types.count(event_type)
        if actual < minimum:
            errors.append(f"lifecycle event {event_type!r} occurred {actual} times; expected at least {minimum}")
    if input_run.assistant_terminal_stop_reason is not None or input_run.require_assistant_error_message:
        assistant_message_ends = [record.get("message") for record in result["records"]
                                  if _event_type(record) == "message_end" and isinstance(record, dict)
                                  and isinstance(record.get("message"), dict)
                                  and record["message"].get("role") == "assistant"]
        if not assistant_message_ends:
            errors.append("assistant message_end was not observed")
        else:
            terminal_message = assistant_message_ends[-1]
            if (input_run.assistant_terminal_stop_reason is not None and
                    terminal_message.get("stopReason") != input_run.assistant_terminal_stop_reason):
                errors.append(
                    f"assistant stopReason was {terminal_message.get('stopReason')!r}; expected "
                    f"{input_run.assistant_terminal_stop_reason!r}"
                )
            if (input_run.require_assistant_error_message and
                    (not isinstance(terminal_message.get("errorMessage"), str) or
                     not terminal_message.get("errorMessage"))):
                errors.append("assistant terminal message has no nonempty errorMessage")
    for response_id, field, expected in input_run.state_flag_expectations:
        response = next((record for record in responses if record.get("id") == response_id), None)
        data = response.get("data") if response is not None else None
        if not isinstance(data, dict) or data.get(field) is not expected:
            observed = data.get(field) if isinstance(data, dict) else None
            errors.append(f"response {response_id!r} state flag {field!r} was {observed!r}, expected {expected!r}")
    for response_id, field, expected in input_run.state_expectations:
        response = next((record for record in responses if record.get("id") == response_id), None)
        data = response.get("data") if response is not None else None
        if not isinstance(data, dict) or data.get(field) != expected:
            observed = data.get(field) if isinstance(data, dict) else None
            errors.append(f"response {response_id!r} state field {field!r} was {observed!r}, expected {expected!r}")
    if input_run.expected_marker_order:
        markers = []
        for record in result["records"]:
            if not isinstance(record, dict):
                continue
            if record.get("type") == "response" and isinstance(record.get("id"), str):
                markers.append(f"response:{record['id']}")
                continue
            event_type = _event_type(record)
            if event_type is not None and (not input_run.lifecycle_event_types or
                                           event_type in input_run.lifecycle_event_types):
                markers.append(f"event:{event_type}")
        offset = 0
        for expected_marker in input_run.expected_marker_order:
            try:
                match_index = markers.index(expected_marker, offset)
            except ValueError:
                errors.append(
                    f"required output order marker {expected_marker!r} is missing after "
                    f"{markers[:offset]!r}"
                )
                break
            offset = match_index + 1
    if input_run.require_diagnostic and not (result["parseFailures"] or result["stderr"] or
                                                any(record.get("type") == "error" or record.get("success") is False
                                                    for record in result["records"] if isinstance(record, dict))):
        errors.append("malformed JSON input produced no observable parse error")
    if input_run.require_all_stdout_json and result["parseFailures"]:
        errors.append("stdout contains a non-JSON protocol record")
    if input_run.require_empty_stderr and result["stderr"]:
        errors.append("stderr is not empty")
    if input_run.require_no_output and (result["stdout"] or result["stderr"]):
        errors.append("empty stdin produced unexpected process output")
    return errors


def _write_capture(evidence_dir: Path, case_id: str, product_run: str, run_index: int,
                   capture: dict[str, Any]) -> list[dict[str, str]]:
    prefix = Path("captures") / case_id / f"{product_run}-{run_index}"
    artifacts: list[dict[str, str]] = []
    for stream in ("stdin", "stdout", "stderr"):
        relative = (prefix / f"{stream}.bin").as_posix()
        path = evidence_dir / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(capture[stream])
        role = f"{product_run}.{stream}"
        artifacts.append({"path": relative, "role": role, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    requests_relative = (prefix / "provider-requests.jsonl").as_posix()
    requests_path = evidence_dir / requests_relative
    requests_path.parent.mkdir(parents=True, exist_ok=True)
    requests_path.write_bytes(b"".join(body + b"\n" for body in capture.get("providerRequests", [])))
    artifacts.append({"path": requests_relative, "role": f"{product_run}.provider.requests",
                      "sha256": hashlib.sha256(requests_path.read_bytes()).hexdigest()})
    exit_relative = (prefix / "exit.json").as_posix()
    exit_path = evidence_dir / exit_relative
    exit_path.write_text(json.dumps({
        "exitCode": capture["exitCode"], "timedOut": capture["timedOut"], "inputError": capture["inputError"],
        "scenarioFailures": capture.get("scenarioFailures", []),
        "providerRequestCount": capture.get("providerRequestCount", 0),
        "providerDisconnectCount": capture.get("providerDisconnectCount", 0),
        "processGroupAlive": capture.get("processGroupAlive", False),
        "providerRequestCountMatchesCaptures": capture.get("providerRequestCount", 0) ==
        len(capture.get("providerRequests", [])),
        "providerErrors": capture.get("providerErrors", []),
    }, sort_keys=True) + "\n", encoding="utf-8")
    artifacts.append({"path": exit_relative, "role": f"{product_run}.exit",
                      "sha256": hashlib.sha256(exit_path.read_bytes()).hexdigest()})
    session_manifest = []
    for index, item in enumerate(capture.get("sessionFiles", [])):
        relative = (prefix / "session-files" / f"{index:03d}.bin").as_posix()
        path = evidence_dir / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(item["content"])
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        artifacts.append({"path": relative, "role": f"{product_run}.session-file.{index}", "sha256": digest})
        session_manifest.append({
            "owner": item["owner"], "relativePath": item["relativePath"],
            "rawArtifact": relative, "sha256": digest, "bytes": len(item["content"]),
        })
    export_manifest = []
    for index, item in enumerate(capture.get("exportFiles", [])):
        relative = (prefix / "export-files" / f"{index:03d}.html").as_posix()
        path = evidence_dir / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(item["content"])
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        artifacts.append({"path": relative, "role": f"{product_run}.export-file.{index}", "sha256": digest})
        export_manifest.append({
            "relativePath": item["relativePath"], "rawArtifact": relative,
            "sha256": digest, "bytes": len(item["content"]),
        })
    if export_manifest:
        manifest_relative = (prefix / "export-files.json").as_posix()
        manifest_path = evidence_dir / manifest_relative
        manifest_path.write_text(json.dumps(export_manifest, ensure_ascii=False, indent=2) + "\n",
                                 encoding="utf-8")
        artifacts.append({"path": manifest_relative, "role": f"{product_run}.export-files.manifest",
                          "sha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest()})
    if session_manifest:
        manifest_relative = (prefix / "session-files.json").as_posix()
        manifest_path = evidence_dir / manifest_relative
        manifest_path.write_text(json.dumps(session_manifest, ensure_ascii=False, indent=2) + "\n",
                                 encoding="utf-8")
        artifacts.append({"path": manifest_relative, "role": f"{product_run}.session-files.manifest",
                          "sha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest()})
    return artifacts


_TEST_EXTENSION_BUILD_CACHE: dict[Path, dict[str, str]] = {}
_NATIVE_EXTENSION_BUILD_CACHE: dict[Path, dict[str, str]] = {}
_RPC_PARITY_EXTENSION_BUILD_CACHE: dict[Path, dict[str, str]] = {}


def _ensure_test_extension_fixture(evidence_dir: Path) -> dict[str, str]:
    key = evidence_dir.resolve()
    cached = _TEST_EXTENSION_BUILD_CACHE.get(key)
    if cached is not None:
        return cached
    command = ["dotnet", "build", str(TEST_EXTENSION_PROJECT), "--no-restore", "--warnaserror"]
    completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
    log = completed.stdout + completed.stderr
    log_path = evidence_dir / "test-extension-fixture-build.log"
    log_path.parent.mkdir(parents=True, exist_ok=True)
    log_path.write_text(log, encoding="utf-8")
    if completed.returncode != 0:
        raise VerificationError(f"test extension fixture build failed: {log[-4000:]}")
    if not TEST_EXTENSION_DLL.is_file():
        raise VerificationError(f"test extension fixture DLL is missing: {TEST_EXTENSION_DLL}")
    artifact = {
        "path": log_path.relative_to(evidence_dir).as_posix(),
        "role": "fixture.build",
        "sha256": hashlib.sha256(log_path.read_bytes()).hexdigest(),
    }
    _TEST_EXTENSION_BUILD_CACHE[key] = artifact
    return artifact


def _ensure_native_extension_fixture(evidence_dir: Path) -> dict[str, str]:
    key = evidence_dir.resolve()
    cached = _NATIVE_EXTENSION_BUILD_CACHE.get(key)
    if cached is not None:
        return cached
    command = ["dotnet", "build", str(NATIVE_ENTRY_EXTENSION_PROJECT), "--no-restore", "--warnaserror"]
    completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
    log = completed.stdout + completed.stderr
    log_path = evidence_dir / "native-extension-fixture-build.log"
    log_path.parent.mkdir(parents=True, exist_ok=True)
    log_path.write_text(log, encoding="utf-8")
    if completed.returncode != 0:
        raise VerificationError(f"native extension fixture build failed: {log[-4000:]}")
    if not NATIVE_ENTRY_EXTENSION_DLL.is_file():
        raise VerificationError(f"native extension fixture DLL is missing: {NATIVE_ENTRY_EXTENSION_DLL}")
    artifact = {
        "path": log_path.relative_to(evidence_dir).as_posix(),
        "role": "fixture.native-extension.build",
        "sha256": hashlib.sha256(log_path.read_bytes()).hexdigest(),
    }
    _NATIVE_EXTENSION_BUILD_CACHE[key] = artifact
    return artifact


def _ensure_rpc_parity_extension_fixture(evidence_dir: Path) -> dict[str, str]:
    key = evidence_dir.resolve()
    cached = _RPC_PARITY_EXTENSION_BUILD_CACHE.get(key)
    if cached is not None:
        return cached
    command = ["dotnet", "build", str(RPC_PARITY_EXTENSION_PROJECT), "--no-restore", "--warnaserror"]
    completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
    log = completed.stdout + completed.stderr
    log_path = evidence_dir / "rpc-parity-extension-fixture-build.log"
    log_path.parent.mkdir(parents=True, exist_ok=True)
    log_path.write_text(log, encoding="utf-8")
    if completed.returncode != 0:
        raise VerificationError(f"RPC parity extension fixture build failed: {log[-4000:]}")
    if not RPC_PARITY_EXTENSION_DLL.is_file():
        raise VerificationError(f"RPC parity extension fixture DLL is missing: {RPC_PARITY_EXTENSION_DLL}")
    artifact = {
        "path": log_path.relative_to(evidence_dir).as_posix(),
        "role": "fixture.rpc-parity-extension.build",
        "sha256": hashlib.sha256(log_path.read_bytes()).hexdigest(),
    }
    _RPC_PARITY_EXTENSION_BUILD_CACHE[key] = artifact
    return artifact


def _store_observation(evidence_dir: Path, case_id: str, product_run: str,
                       observation: dict[str, Any]) -> tuple[str, str]:
    relative = (Path("observations") / case_id / f"{product_run}.json").as_posix()
    path = evidence_dir / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(observation, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return relative, hashlib.sha256(path.read_bytes()).hexdigest()


def _generate_r8_native_summary_fixture(evidence_dir: Path) -> tuple[str, str, list[dict[str, str]]]:
    evidence_dir = evidence_dir.resolve()
    cache_key = str(evidence_dir.resolve())
    if cache_key in _R8_GENERATED_FIXTURE_CACHE:
        return _R8_GENERATED_FIXTURE_CACHE[cache_key]

    output_dir = (evidence_dir / "generated" / "native-summary").resolve()
    if output_dir.exists():
        shutil.rmtree(output_dir)
    output_dir.mkdir(parents=True)
    environment = os.environ.copy()
    environment["PISHARP_RPC_R8_FIXTURE_OUTPUT_DIR"] = str(output_dir)
    command = [
        "dotnet", "test", str(TEST_EXTENSION_PROJECT), "--no-restore",
        "--filter", "FullyQualifiedName~NativeExtensionTreeEventTests.RequestedBranchSummaryUsesMafAndPersistsItsUsage",
    ]
    log_path = output_dir / "summary-generation-test.log"
    try:
        with log_path.open("w", encoding="utf-8") as log_stream:
            completed = subprocess.run(command, cwd=ROOT, env=environment, stdout=log_stream,
                                       stderr=subprocess.STDOUT, text=True, timeout=180)
    except subprocess.TimeoutExpired as error:
        log_text = log_path.read_text(encoding="utf-8", errors="replace")
        raise VerificationError(f"native branch-summary generator regression timed out: {log_text[-4000:]}") from error
    log_text = log_path.read_text(encoding="utf-8", errors="replace")
    log_text = f"$ {' '.join(command)}\nexit code: {completed.returncode}\n\n{log_text}"
    log_path.write_text(log_text, encoding="utf-8")
    if completed.returncode != 0:
        raise VerificationError(f"native branch-summary generator regression failed: {log_text[-4000:]}")

    native_path = output_dir / "generated.session.json"
    exported_pi_path = output_dir / "generated.jsonl"
    if not native_path.is_file() or not exported_pi_path.is_file():
        raise VerificationError("native branch-summary regression did not emit both persisted session formats")
    native_document = json.loads(native_path.read_text(encoding="utf-8"))
    native_entries = native_document.get("Entries", [])
    if not any(entry.get("Type") == "branch_summary" and
               isinstance(entry.get("Payload", {}).get("nativeUsage"), dict)
               for entry in native_entries if isinstance(entry, dict)):
        raise VerificationError("generated native session fixture has no billed branch-summary usage")
    native_document["Provider"] = "fixture"
    native_document["Model"] = "fixture-model"
    native_process_path = output_dir / "pisharp-process-session.session.json"
    native_process_path.write_text(json.dumps(native_document, ensure_ascii=False, separators=(",", ":")) + "\n",
                                  encoding="utf-8")
    pi_entries = [json.loads(line) for line in exported_pi_path.read_text(encoding="utf-8").splitlines() if line]
    if pi_entries and pi_entries[0].get("type") == "session":
        pi_entries[0]["provider"] = "fixture"
        pi_entries[0]["modelId"] = "fixture-model"
        pi_entries[0]["thinkingLevel"] = "off"
    # The generator regression's scaffold assistant messages have no provider usage. Pi's
    # get_session_stats requires a complete Usage record on each assistant message, so project
    # those unbilled messages as zero usage in the Pi input while preserving the billed summary.
    for entry in pi_entries:
        message = entry.get("message")
        if isinstance(message, dict) and message.get("role") == "assistant" and not message.get("usage"):
            message["usage"] = {
                "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0, "totalTokens": 0,
                "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0, "total": 0},
            }
    if not any(entry.get("type") == "branch_summary" and isinstance(entry.get("usage"), dict)
               for entry in pi_entries):
        raise VerificationError("generated Pi JSONL fixture has no branch-summary usage")
    pi_path = output_dir / "pinned-pi-session.jsonl"
    pi_path.write_text("".join(json.dumps(entry, ensure_ascii=False, separators=(",", ":")) + "\n"
                                for entry in pi_entries), encoding="utf-8")

    artifacts = []
    for path, role in ((log_path, "summary-generation-test"),
                       (native_path, "generated-native-session"),
                       (native_process_path, "pisharp-process-session"),
                       (exported_pi_path, "generated-pisharp-jsonl-session"),
                       (pi_path, "pinned-pi-compatible-jsonl-session")):
        artifacts.append({
            "path": path.relative_to(evidence_dir).as_posix(),
            "role": role,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        })
    result = (native_process_path.read_text(encoding="utf-8"), pi_path.read_text(encoding="utf-8"), artifacts)
    _R8_GENERATED_FIXTURE_CACHE[cache_key] = result
    return result


def run_process_case(case: dict[str, Any], *, pi: Path, evidence_dir: Path) -> dict[str, Any]:
    case_id = case["id"]
    from session_scenarios import R12_SCENARIOS, R13_SCENARIOS, R14_SCENARIOS, R15_SCENARIOS

    scenarios = R1_SCENARIOS.get(case_id) or R2_SCENARIOS.get(case_id) or R3_SCENARIOS.get(case_id) or \
        R4_SCENARIOS.get(case_id) or R5_SCENARIOS.get(case_id) or R6_SCENARIOS.get(case_id) or \
        R7_SCENARIOS.get(case_id) or R8_SCENARIOS.get(case_id) or R9_SCENARIOS.get(case_id) or \
        R10_SCENARIOS.get(case_id) or R11_SCENARIOS.get(case_id) or R12_SCENARIOS.get(case_id) or \
        R13_SCENARIOS.get(case_id) or R14_SCENARIOS.get(case_id) or R15_SCENARIOS.get(case_id)
    if case_id in R2_SCENARIOS and case_id not in {
        "R2.long-command-acceptance", "R2.active-compaction-command",
    }:
        scenarios = tuple(replace(run, response_only=True) for run in scenarios)
    if scenarios is None:
        raise VerificationError(f"no process scenario adapter is registered for {case_id}")
    artifacts: list[dict[str, str]] = []
    if case_id in {"R8.native-summary-generation", "R8.native-summary-persisted-reload"}:
        native_fixture, pi_fixture, generated_artifacts = _generate_r8_native_summary_fixture(evidence_dir)
        artifacts.extend(generated_artifacts)
        scenarios = tuple(
            replace(run, session_fixture_jsonl=pi_fixture, session_fixture_native_json=native_fixture,
                    bind_fixture_working_directory=True)
            if run.session_fixture_jsonl is not None or run.session_fixture_native_json is not None else run
            for run in scenarios
        )
    if any(input_run.use_handled_extension_command or input_run.use_rpc_tool_extension
           for input_run in scenarios):
        artifacts.append(_ensure_test_extension_fixture(evidence_dir))
    if any(input_run.use_entry_appending_extension for input_run in scenarios):
        artifacts.append(_ensure_native_extension_fixture(evidence_dir))
    if any(input_run.use_rpc_navigation_extension or input_run.use_rpc_ui_extension for input_run in scenarios):
        artifacts.append(_ensure_rpc_parity_extension_fixture(evidence_dir))
    if any(input_run.project_rebind_fixture for input_run in scenarios):
        artifacts.append(_ensure_rpc_parity_extension_fixture(evidence_dir))
    observations: dict[str, dict[str, Any]] = {}
    failures: dict[str, list[str]] = {}
    with tempfile.TemporaryDirectory(prefix=f"rpc-{case_id.replace('.', '-')}-") as temporary:
        workspace = Path(temporary)
        for product_run, product in (("pi1", "pi"), ("pi2", "pi"), ("pisharp", "pisharp")):
            run_results = []
            product_failures: list[str] = []
            shared_session = any(input_run.resume_session or input_run.reuse_workspace_previous
                                 for input_run in scenarios)
            shared_provider = None
            if shared_session:
                if any(input_run.provider_response_delays for input_run in scenarios):
                    raise VerificationError("shared-session process scenarios must use indexed fixture provider steps")
                shared_provider = _FixtureProvider((),
                    tuple(step for input_run in scenarios for step in input_run.provider_steps),
                    workspace / "rpc-tool-gate")
            previous_workspace: Path | None = None
            try:
                for index, input_run in enumerate(scenarios):
                    if input_run.reuse_workspace_previous:
                        if previous_workspace is None:
                            raise VerificationError("a shared-session scenario must have a preceding process workspace")
                        run_workspace = previous_workspace
                    else:
                        run_workspace = workspace / product_run / str(index)
                        previous_workspace = run_workspace
                    result = _execute(product, pi, run_workspace, input_run, shared_provider)
                    artifacts.extend(_write_capture(evidence_dir, case_id, product_run, index, result))
                    product_failures.extend(f"run {index}: {error}" for error in _check_run(input_run, result))
                    run_results.append(result)
            finally:
                if shared_provider is not None:
                    shared_provider.close()
            observations[product_run] = _observation(run_results, scenarios)
            failures[product_run] = product_failures
    observation_paths: dict[str, tuple[str, str]] = {
        name: _store_observation(evidence_dir, case_id, name, value) for name, value in observations.items()
    }
    for name, (path, digest) in observation_paths.items():
        artifacts.append({"path": path, "role": f"{name}.observation", "sha256": digest})
    same_pi = observations["pi1"] == observations["pi2"]
    matches = observations["pi1"] == observations["pisharp"]
    pi_path, _ = observation_paths["pi1"]
    sharp_path, _ = observation_paths["pisharp"]
    assertion = {
        "contractObservation": case["expectedObservation"],
        "comparison": case["comparisonRules"]["mode"],
        "piArtifact": pi_path,
        "pisharpArtifact": sharp_path,
        "piPointer": "",
        "pisharpPointer": "",
        "piValue": observations["pi1"],
        "pisharpValue": observations["pisharp"],
        "passed": matches,
    }
    if assertion["comparison"] == "equivalent":
        assertion.update({
            "normalizedPi": observations["pi1"],
            "normalizedPiSharp": observations["pisharp"],
            "normalizationEvidence": {
                "rules": case["comparisonRules"]["normalizations"],
                "piObservationArtifact": pi_path,
                "pisharpObservationArtifact": sharp_path,
                "normalizationAppliedBeforeComparison": True,
            },
        })
    assertions = [assertion]
    all_passed = same_pi and matches and not any(failures.values())
    return {
        "caseId": case_id,
        "checkpointId": case_id.split(".", 1)[0],
        "contractExpectedObservation": case["expectedObservation"],
        "executed": True,
        "passed": all_passed,
        "skipped": False,
        "rawArtifacts": artifacts,
        "assertions": assertions,
        "independentPiCalibrationMatched": same_pi,
        "independentPiCalibration": {
            "pi1Artifact": observation_paths["pi1"][0],
            "pi2Artifact": observation_paths["pi2"][0],
            "matched": same_pi,
        },
        "pinnedPiAndPiSharpMatched": matches,
        "executionFailures": failures,
    }


def scenario_case_ids() -> set[str]:
    from session_scenarios import R12_SCENARIOS, R13_SCENARIOS, R14_SCENARIOS, R15_SCENARIOS

    return (set(R1_SCENARIOS) | set(R2_SCENARIOS) | set(R3_SCENARIOS) |
            set(R4_SCENARIOS) | set(R5_SCENARIOS) | set(R6_SCENARIOS) | set(R7_SCENARIOS) |
            set(R8_SCENARIOS) | set(R9_SCENARIOS) | set(R10_SCENARIOS) | set(R11_SCENARIOS) |
            set(R12_SCENARIOS) | set(R13_SCENARIOS) | set(R14_SCENARIOS) | set(R15_SCENARIOS))
