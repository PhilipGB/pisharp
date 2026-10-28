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
VOLATILE_FIELDS = {"sessionFile", "sessionId", "timestamp"}


class _AbsentValue:
    def __repr__(self) -> str:
        return "<absent>"


_ABSENT = _AbsentValue()


@dataclass(frozen=True)
class InputRun:
    chunks: tuple[tuple[bytes, float], ...]
    expected_response_ids: tuple[Any, ...] | None = None
    expected_response_commands: tuple[str | None, ...] | None = None
    expected_successes: tuple[bool, ...] | None = None
    expected_data_presence: tuple[bool, ...] | None = None
    expected_error_presence: tuple[bool, ...] | None = None
    preserve_response_error_text: bool = False
    response_only: bool = False
    provider_response_delays: tuple[float, ...] = ()
    provider_waits: tuple[tuple[int, int, float], ...] = ()
    stdout_waits: tuple[tuple[int, str, int, float], ...] = ()
    expected_min_provider_requests: int = 0
    lifecycle_event_types: tuple[str, ...] = ()
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


class _FixtureProvider:
    def __init__(self, response_delays: tuple[float, ...]):
        self._response_delays = response_delays
        self._request_count = 0
        self._condition = threading.Condition()
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, format: str, *args: Any) -> None:
                return

            def do_POST(self) -> None:
                try:
                    length = int(self.headers.get("Content-Length", "0"))
                    request = json.loads(self.rfile.read(length).decode("utf-8"))
                    request_index = fixture.record_request()
                    delay = fixture._response_delays[request_index] if request_index < len(fixture._response_delays) else 0.0
                    if delay:
                        time.sleep(delay)
                    response = fixture.response_body(request.get("stream") is True, request_index)
                    self.send_response(200)
                    self.send_header("Content-Type", "text/event-stream" if request.get("stream") is True
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

    def record_request(self) -> int:
        with self._condition:
            index = self._request_count
            self._request_count += 1
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

    @staticmethod
    def response_body(stream: bool, request_index: int) -> bytes:
        response_id = f"rpc-fixture-{request_index}"
        if stream:
            chunks = [
                {
                    "id": response_id, "object": "chat.completion.chunk", "created": 1,
                    "model": "fixture-model",
                    "choices": [{"index": 0, "delta": {"role": "assistant", "content": "fixture answer"},
                                 "finish_reason": None}],
                },
                {
                    "id": response_id, "object": "chat.completion.chunk", "created": 1,
                    "model": "fixture-model",
                    "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
                    "usage": {"prompt_tokens": 12, "completion_tokens": 2, "total_tokens": 14},
                },
            ]
            body = "".join(f"data: {json.dumps(chunk, separators=(',', ':'))}\n\n" for chunk in chunks)
            return (body + "data: [DONE]\n\n").encode("utf-8")
        response = {
            "id": response_id, "object": "chat.completion", "created": 1, "model": "fixture-model",
            "choices": [{"index": 0, "message": {"role": "assistant", "content": "fixture summary"},
                         "finish_reason": "stop"}],
            "usage": {"prompt_tokens": 12, "completion_tokens": 2, "total_tokens": 14},
        }
        return json.dumps(response, separators=(",", ":")).encode("utf-8")

    def close(self) -> None:
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
                     provider_base_url: str | None = None) -> tuple[list[str], Path, dict[str, str]]:
    product_root = workspace / product
    agent = product_root / "agent"
    sessions = product_root / "sessions"
    agent.mkdir(parents=True)
    sessions.mkdir(parents=True)
    model = {
        "id": "fixture-model", "name": "fixture-model", "api": "openai-completions",
        "reasoning": False, "input": ["text"], "contextWindow": 4096, "maxTokens": 128,
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
              "--session-dir", str(sessions), "--no-extensions", "--no-skills",
              "--no-prompt-templates", "--no-tools"]
    if product == "pi":
        command = ["node", "--import", (pi / PI_SOURCE_RESOLVER).as_uri(), str(pi / PI_CLI), *common]
        environment["PI_CODING_AGENT_DIR"] = str(agent)
        return command, pi, environment
    command = ["dotnet", str(PISHARP_DLL), *common, "--offline"]
    return command, product_root, environment


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
    provider = _FixtureProvider(input_run.provider_response_delays) if input_run.provider_response_delays else None
    try:
        return _execute_with_provider(product, pi, workspace, input_run, provider)
    finally:
        if provider is not None:
            provider.close()


def _execute_with_provider(product: str, pi: Path, workspace: Path, input_run: InputRun,
                           provider: _FixtureProvider | None) -> dict[str, Any]:
    command, cwd, environment = _prepare_product(
        product, pi, workspace, provider.base_url if provider is not None else None,
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
            event_type = record.get("type")
            if event_type == "event" and isinstance(record.get("data"), dict):
                event_type = record["data"].get("Type", record["data"].get("type"))
            if event_type in input_run.lifecycle_event_types:
                projected.append({"type": event_type})
        return projected

    return {
        "runs": [
            {
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
            for input_run, result in zip(input_runs, run_results, strict=True)
        ]
    }


def _check_run(input_run: InputRun, result: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    if result["timedOut"]:
        errors.append("process timed out")
    if result["exitCode"] != 0:
        errors.append(f"process exited {result['exitCode']}")
    errors.extend(result.get("scenarioFailures", []))
    errors.extend(f"local provider error: {error}" for error in result.get("providerErrors", []))
    provider_count = result.get("providerRequestCount", 0)
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
            event_type = record.get("type")
            if event_type == "event" and isinstance(record.get("data"), dict):
                event_type = record["data"].get("Type", record["data"].get("type"))
            if event_type in input_run.lifecycle_event_types:
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
    exit_relative = (prefix / "exit.json").as_posix()
    exit_path = evidence_dir / exit_relative
    exit_path.write_text(json.dumps({
        "exitCode": capture["exitCode"], "timedOut": capture["timedOut"], "inputError": capture["inputError"],
        "scenarioFailures": capture.get("scenarioFailures", []),
        "providerRequestCount": capture.get("providerRequestCount", 0),
        "providerErrors": capture.get("providerErrors", []),
    }, sort_keys=True) + "\n", encoding="utf-8")
    artifacts.append({"path": exit_relative, "role": f"{product_run}.exit",
                      "sha256": hashlib.sha256(exit_path.read_bytes()).hexdigest()})
    return artifacts


def _store_observation(evidence_dir: Path, case_id: str, product_run: str,
                       observation: dict[str, Any]) -> tuple[str, str]:
    relative = (Path("observations") / case_id / f"{product_run}.json").as_posix()
    path = evidence_dir / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(observation, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return relative, hashlib.sha256(path.read_bytes()).hexdigest()


def run_process_case(case: dict[str, Any], *, pi: Path, evidence_dir: Path) -> dict[str, Any]:
    case_id = case["id"]
    scenarios = R1_SCENARIOS.get(case_id) or R2_SCENARIOS.get(case_id)
    if case_id in R2_SCENARIOS and case_id not in {
        "R2.long-command-acceptance", "R2.active-compaction-command",
    }:
        scenarios = tuple(replace(run, response_only=True) for run in scenarios)
    if scenarios is None:
        raise VerificationError(f"no process scenario adapter is registered for {case_id}")
    artifacts: list[dict[str, str]] = []
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
    return set(R1_SCENARIOS) | set(R2_SCENARIOS)
