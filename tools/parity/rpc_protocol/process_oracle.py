#!/usr/bin/env python3
"""Capture paired pinned-Pi/PiSharp JSONL process observations for RPC cases."""
from __future__ import annotations

from dataclasses import dataclass, replace
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import threading
import time
from typing import Any

from verify import VerificationError

ROOT = Path(__file__).resolve().parents[3]
PI_SOURCE_RESOLVER = "packages/coding-agent/src/experimental/source-resolver.ts"
PI_CLI = "packages/coding-agent/src/cli.ts"
PISHARP_DLL = ROOT / "src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll"
TEST_EXTENSION_PROJECT = ROOT / "tests/PiSharp.Tests/PiSharp.Tests.csproj"
TEST_EXTENSION_DLL = ROOT / "tests/PiSharp.Tests/bin/Debug/net10.0/PiSharp.Tests.dll"
VOLATILE_FIELDS = {"sessionFile", "sessionId", "timestamp"}


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
    hold_after_partial: bool = False


@dataclass(frozen=True)
class InputRun:
    chunks: tuple[tuple[bytes, float], ...]
    expected_response_ids: tuple[Any, ...] | None = None
    expected_response_commands: tuple[str | None, ...] | None = None
    expected_successes: tuple[bool, ...] | None = None
    expected_data_presence: tuple[bool, ...] | None = None
    expected_error_presence: tuple[bool, ...] | None = None
    expected_response_dispositions: tuple[str | None, ...] | None = None
    preserve_response_error_text: bool = False
    response_only: bool = False
    provider_response_delays: tuple[float, ...] = ()
    provider_steps: tuple[FixtureProviderStep, ...] = ()
    provider_waits: tuple[tuple[int, int, float], ...] = ()
    stdout_waits: tuple[tuple[int, str, int, float], ...] = ()
    release_tool_gate_after_input_index: int | None = None
    expected_min_provider_requests: int = 0
    lifecycle_event_types: tuple[str, ...] = ()
    preserve_event_payloads: bool = False
    preserve_system_message_payloads: bool = False
    forbidden_lifecycle_event_types: tuple[str, ...] = ()
    minimum_lifecycle_event_counts: tuple[tuple[str, int], ...] = ()
    assistant_terminal_stop_reason: str | None = None
    require_assistant_error_message: bool = False
    preserve_assistant_terminal_shape: bool = False
    use_handled_extension_command: bool = False
    enable_bash_tool: bool = False
    compare_provider_requests: bool = False
    model_context_window: int = 4096
    preserve_state_flags: bool = False
    state_flag_expectations: tuple[tuple[str, str, bool], ...] = ()
    expected_marker_order: tuple[str, ...] = ()
    all_responses_succeed: bool = False
    require_diagnostic: bool = False
    require_all_stdout_json: bool = False
    require_empty_stderr: bool = False
    require_no_output: bool = False


def _request(request_id: str | None, command: str = "get_state", **fields: Any) -> bytes:
    value: dict[str, Any] = {"type": command}
    if request_id is not None:
        value["id"] = request_id
    value.update(fields)
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def _line(request_id: str | None, command: str = "get_state", **fields: Any) -> bytes:
    return _request(request_id, command, **fields) + b"\n"


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
        self._tool_gate_path.with_suffix(".started").unlink(missing_ok=True)
        self._tool_gate_path.with_suffix(".release").unlink(missing_ok=True)
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
                        fixture._release_stream.wait(60)
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
                    pass
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

    def wait_for_requests(self, count: int, timeout_seconds: float) -> bool:
        deadline = time.monotonic() + timeout_seconds
        with self._condition:
            while self._request_count < count:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return False
                self._condition.wait(remaining)
            return True

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
            if step.tool_name is not None:
                arguments = step.tool_arguments or "{}"
                arguments = arguments.replace("__RPC_TOOL_GATE__", str(self._tool_gate_path))
                chunks = [
                    self.stream_chunk(request_index, {"role": "assistant", "tool_calls": [{
                        "index": 0, "id": f"rpc-tool-{request_index}", "type": "function",
                        "function": {"name": step.tool_name, "arguments": arguments},
                    }]}),
                    {
                        "id": f"rpc-fixture-{request_index}", "object": "chat.completion.chunk", "created": 1,
                        "model": "fixture-model", "choices": [{"index": 0, "delta": {}, "finish_reason": "tool_calls"}],
                        "usage": {"prompt_tokens": 12, "completion_tokens": 2, "total_tokens": 14},
                    },
                ]
            else:
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
                         "finish_reason": "stop"}],
            "usage": {"prompt_tokens": 12, "completion_tokens": 2, "total_tokens": 14},
        }
        return json.dumps(response, separators=(",", ":")).encode("utf-8")

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
    agent.mkdir(parents=True)
    sessions.mkdir(parents=True)
    model = {
        "id": "fixture-model", "name": "fixture-model", "api": "openai-completions",
        "reasoning": False, "input": ["text"], "contextWindow": input_run.model_context_window, "maxTokens": 128,
        "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0},
    }
    provider: dict[str, Any] = {
        "baseUrl": provider_base_url or "http://127.0.0.1:9/v1",
        "models": [model],
    }
    if product == "pi":
        provider["apiKey"] = "fixture-only-key"
    else:
        provider["apiKeyEnv"] = "PISHARP_RPC_FIXTURE_KEY"
    (agent / "models.json").write_text(json.dumps({"providers": {"fixture": provider}}) + "\n", encoding="utf-8")
    if provider_base_url is not None:
        (agent / "settings.json").write_text('{"compaction":{"keepRecentTokens":1}}\n', encoding="utf-8")
    environment = _base_environment()
    environment.update({
        "HOME": str(agent), "PI_CODING_AGENT_DIR": str(agent), "PI_OFFLINE": "1",
        "PISHARP_AGENT_DIR": str(agent), "PISHARP_SESSION_DIR": str(sessions),
        "PISHARP_RPC_FIXTURE_KEY": "fixture-only-key",
    })
    common = ["--mode", "rpc", "--provider", "fixture", "--model", "fixture-model",
              "--session-dir", str(sessions), "--no-context-files", "--no-extensions", "--no-skills",
              "--no-prompt-templates", "--system-prompt", "You are a deterministic RPC fixture assistant."]
    if input_run.enable_bash_tool:
        common.extend(("--tools", "bash"))
    else:
        common.append("--no-tools")
    extension_path: Path | None = None
    if input_run.use_handled_extension_command:
        if product == "pi":
            extension_path = agent / "rpc-handled-extension.ts"
            extension_path.write_text(
                'export default function (pi) { pi.registerCommand("fixture", '
                '{ description: "Handle without a run", handler: async () => {} }); }\n',
                encoding="utf-8",
            )
        else:
            extension_path = TEST_EXTENSION_DLL
        common.extend(("--extension", str(extension_path)))
    if product == "pi":
        command = ["node", "--import", (pi / PI_SOURCE_RESOLVER).as_uri(), str(pi / PI_CLI), *common]
        environment["PI_CODING_AGENT_DIR"] = str(agent)
        return command, workspace.parents[1], environment
    command = ["dotnet", str(PISHARP_DLL), *common, "--offline"]
    return command, workspace.parents[1], environment


def _read_all(stream, destination: list[bytes], condition: threading.Condition | None = None) -> None:
    while True:
        chunk = stream.read(64 * 1024)
        if not chunk:
            return
        if condition is None:
            destination.append(chunk)
        else:
            with condition:
                destination.append(chunk)
                condition.notify_all()


def _execute(product: str, pi: Path, workspace: Path, input_run: InputRun) -> dict[str, Any]:
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
    stdout_reader = threading.Thread(target=_read_all, args=(process.stdout, stdout_chunks, stdout_condition), daemon=True)
    stderr_reader = threading.Thread(target=_read_all, args=(process.stderr, stderr_chunks), daemon=True)
    stdout_reader.start()
    stderr_reader.start()
    input_chunks: list[bytes] = []
    input_error: str | None = None
    scenario_failures: list[str] = []
    provider_waits = {index: [] for index, _, _ in input_run.provider_waits}
    for index, count, timeout in input_run.provider_waits:
        provider_waits[index].append((count, timeout))
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

    try:
        for index, (payload, delay) in enumerate(input_run.chunks):
            if delay:
                time.sleep(delay)
            input_chunks.append(payload)
            try:
                process.stdin.write(payload)
                process.stdin.flush()
            except (BrokenPipeError, OSError) as error:
                input_error = f"{type(error).__name__}: {error}"
                break
            for count, timeout_seconds in provider_waits.get(index, []):
                if provider is None or not provider.wait_for_requests(count, timeout_seconds):
                    scenario_failures.append(f"provider did not receive request {count} after input chunk {index}")
            for marker, occurrence, timeout_seconds in stdout_waits.get(index, []):
                if not wait_for_stdout(marker, occurrence, timeout_seconds):
                    scenario_failures.append(
                        f"stdout marker {marker!r} occurrence {occurrence} was not observed after input chunk {index}"
                    )
            if input_run.release_tool_gate_after_input_index == index:
                if provider is None:
                    scenario_failures.append("tool gate release requested without a local fixture provider")
                else:
                    provider._tool_gate_path.with_suffix(".release").write_text("release\n", encoding="utf-8")
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
    return {
        "stdin": b"".join(input_chunks), "stdout": stdout, "stderr": stderr,
        "exitCode": exit_code, "timedOut": timed_out, "inputError": input_error,
        "records": lines, "parseFailures": parse_failures, "scenarioFailures": scenario_failures,
        "providerRequestCount": provider.request_count if provider is not None else 0,
        "providerRequests": provider.request_bodies if provider is not None else [],
        "providerErrors": provider.errors if provider is not None else [],
    }


def _project(value: Any, *, preserve_response_error_text: bool = False,
             preserve_state_flags: bool = False) -> Any:
    if isinstance(value, list):
        return [_project(item, preserve_response_error_text=preserve_response_error_text,
                         preserve_state_flags=preserve_state_flags) for item in value]
    if not isinstance(value, dict):
        return value
    result: dict[str, Any] = {}
    for key, item in value.items():
        if key in VOLATILE_FIELDS:
            continue
        if key == "error" and value.get("type") == "response":
            result["errorPresent"] = True
            result["errorIsNonEmptyString"] = isinstance(item, str) and bool(item)
            if preserve_response_error_text:
                result["errorText"] = item
            continue
        if key == "data" and value.get("type") == "response":
            result["dataPresent"] = True
            if (preserve_state_flags and value.get("command") == "get_state" and
                    isinstance(item, dict)):
                result["stateFlags"] = {
                    name: item[name] for name in ("isStreaming", "isCompacting") if name in item
                }
            continue
        result[key] = _project(item, preserve_response_error_text=preserve_response_error_text,
                               preserve_state_flags=preserve_state_flags)
    if value.get("type") == "response" and "data" not in value:
        result["dataPresent"] = False
    if value.get("type") == "response" and "error" not in value:
        result["errorPresent"] = False
    return result


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
            event_type = _event_type(record)
            if event_type in input_run.lifecycle_event_types:
                message = record.get("message") if isinstance(record, dict) else None
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
                else:
                    projected.append(record if input_run.preserve_event_payloads else {"type": event_type})
        return projected

    projected_runs = []
    for input_run, result in zip(input_runs, run_results, strict=True):
        projected_run = {
                "exitCode": result["exitCode"],
                "timedOut": result["timedOut"],
                "inputError": result["inputError"],
                "records": _project(
                    records_for(result, input_run),
                    preserve_response_error_text=input_run.preserve_response_error_text,
                    preserve_state_flags=input_run.preserve_state_flags,
                ),
                "stderr": result["stderr"].decode("utf-8", errors="replace"),
                "parseFailures": result["parseFailures"],
        }
        if input_run.compare_provider_requests:
            projected_run["providerRequests"] = [
                _canonical_provider_request(json.loads(body.decode("utf-8")))
                for body in result["providerRequests"]
            ]
        projected_runs.append(projected_run)
    return {"runs": projected_runs}


def _check_run(input_run: InputRun, result: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    if result["timedOut"]:
        errors.append("process timed out")
    if result["exitCode"] != 0:
        errors.append(f"process exited {result['exitCode']}")
    errors.extend(result.get("scenarioFailures", []))
    errors.extend(f"local provider error: {error}" for error in result.get("providerErrors", []))
    provider_count = result.get("providerRequestCount", 0)
    provider_requests = result.get("providerRequests", [])
    if provider_count != len(provider_requests):
        errors.append(f"provider received {provider_count} requests but {len(provider_requests)} request bodies were captured")
    if provider_count < input_run.expected_min_provider_requests:
        errors.append(
            f"local provider received {provider_count} requests; expected at least "
            f"{input_run.expected_min_provider_requests}"
        )
    responses = [record for record in result["records"] if isinstance(record, dict) and record.get("type") == "response"]
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
        "providerRequestCountMatchesCaptures": capture.get("providerRequestCount", 0) ==
        len(capture.get("providerRequests", [])),
        "providerErrors": capture.get("providerErrors", []),
    }, sort_keys=True) + "\n", encoding="utf-8")
    artifacts.append({"path": exit_relative, "role": f"{product_run}.exit",
                      "sha256": hashlib.sha256(exit_path.read_bytes()).hexdigest()})
    return artifacts


_TEST_EXTENSION_BUILD_CACHE: dict[Path, dict[str, str]] = {}


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


def _store_observation(evidence_dir: Path, case_id: str, product_run: str,
                       observation: dict[str, Any]) -> tuple[str, str]:
    relative = (Path("observations") / case_id / f"{product_run}.json").as_posix()
    path = evidence_dir / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(observation, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return relative, hashlib.sha256(path.read_bytes()).hexdigest()


def run_process_case(case: dict[str, Any], *, pi: Path, evidence_dir: Path) -> dict[str, Any]:
    case_id = case["id"]
    scenarios = R1_SCENARIOS.get(case_id) or R2_SCENARIOS.get(case_id) or R3_SCENARIOS.get(case_id)
    if case_id in R2_SCENARIOS and case_id not in {
        "R2.long-command-acceptance", "R2.active-compaction-command",
    }:
        scenarios = tuple(replace(run, response_only=True) for run in scenarios)
    if scenarios is None:
        raise VerificationError(f"no process scenario adapter is registered for {case_id}")
    artifacts: list[dict[str, str]] = []
    if any(input_run.use_handled_extension_command for input_run in scenarios):
        artifacts.append(_ensure_test_extension_fixture(evidence_dir))
    observations: dict[str, dict[str, Any]] = {}
    failures: dict[str, list[str]] = {}
    with tempfile.TemporaryDirectory(prefix=f"rpc-{case_id.replace('.', '-')}-") as temporary:
        workspace = Path(temporary)
        for product_run, product in (("pi1", "pi"), ("pi2", "pi"), ("pisharp", "pisharp")):
            run_results = []
            product_failures: list[str] = []
            for index, input_run in enumerate(scenarios):
                result = _execute(product, pi, workspace / product_run / str(index), input_run)
                artifacts.extend(_write_capture(evidence_dir, case_id, product_run, index, result))
                product_failures.extend(f"run {index}: {error}" for error in _check_run(input_run, result))
                run_results.append(result)
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
    assertions = [{
        "contractObservation": case["expectedObservation"],
        "comparison": case["comparisonRules"]["mode"],
        "piArtifact": pi_path,
        "pisharpArtifact": sharp_path,
        "piPointer": "",
        "pisharpPointer": "",
        "piValue": observations["pi1"],
        "pisharpValue": observations["pisharp"],
        "passed": matches,
    }]
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
    return set(R1_SCENARIOS) | set(R2_SCENARIOS) | set(R3_SCENARIOS)
