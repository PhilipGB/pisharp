#!/usr/bin/env python3
"""Capture paired pinned-Pi/PiSharp JSONL process observations for RPC cases."""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
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


@dataclass(frozen=True)
class InputRun:
    chunks: tuple[tuple[bytes, float], ...]
    expected_response_ids: tuple[str | None, ...] | None = None
    expected_successes: tuple[bool, ...] | None = None
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
             expected_response_ids=(None, "recovered"), expected_successes=(False, True),
             require_diagnostic=True),
)
R1_SCENARIOS["R1.empty-and-blank-input"] = (
    InputRun((), expected_response_ids=(), require_no_output=True),
    InputRun(((b"\n\n" + _line("after-blanks"), 0.0),),
             expected_response_ids=(None, None, "after-blanks"), expected_successes=(False, False, True)),
)
eof = _request("eof-final")
R1_SCENARIOS["R1.eof-incomplete-final-record"] = (
    InputRun(((eof, 0.0),), expected_response_ids=("eof-final",), all_responses_succeed=True),
)
R1_SCENARIOS["R1.stdout-stderr-purity"] = _one(
    _line("stdout-only"), expected_ids=("stdout-only",), all_responses_succeed=True,
    require_all_stdout_json=True, require_empty_stderr=True,
)


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


def _prepare_product(product: str, pi: Path, workspace: Path) -> tuple[list[str], Path, dict[str, str]]:
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
        "baseUrl": "http://127.0.0.1:9/v1",
        "models": [model],
    }
    if product == "pi":
        provider["apiKey"] = "fixture-only-key"
    else:
        provider["apiKeyEnv"] = "PISHARP_RPC_FIXTURE_KEY"
    (agent / "models.json").write_text(json.dumps({"providers": {"fixture": provider}}) + "\n", encoding="utf-8")
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


def _read_all(stream, destination: list[bytes]) -> None:
    while True:
        chunk = stream.read(64 * 1024)
        if not chunk:
            return
        destination.append(chunk)


def _execute(product: str, pi: Path, workspace: Path, input_run: InputRun) -> dict[str, Any]:
    command, cwd, environment = _prepare_product(product, pi, workspace)
    process = subprocess.Popen(
        command, cwd=cwd, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, start_new_session=True, bufsize=0,
    )
    stdout_chunks: list[bytes] = []
    stderr_chunks: list[bytes] = []
    stdout_reader = threading.Thread(target=_read_all, args=(process.stdout, stdout_chunks), daemon=True)
    stderr_reader = threading.Thread(target=_read_all, args=(process.stderr, stderr_chunks), daemon=True)
    stdout_reader.start()
    stderr_reader.start()
    input_chunks: list[bytes] = []
    input_error: str | None = None
    try:
        for payload, delay in input_run.chunks:
            if delay:
                time.sleep(delay)
            input_chunks.append(payload)
            try:
                process.stdin.write(payload)
                process.stdin.flush()
            except (BrokenPipeError, OSError) as error:
                input_error = f"{type(error).__name__}: {error}"
                break
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
        "records": lines, "parseFailures": parse_failures,
    }


def _project(value: Any) -> Any:
    if isinstance(value, list):
        return [_project(item) for item in value]
    if not isinstance(value, dict):
        return value
    result: dict[str, Any] = {}
    for key, item in value.items():
        if key in VOLATILE_FIELDS:
            continue
        if key == "error" and value.get("type") == "response":
            result["errorPresent"] = True
            result["errorIsNonEmptyString"] = isinstance(item, str) and bool(item)
            continue
        if key == "data" and value.get("type") == "response":
            result["dataPresent"] = True
            continue
        result[key] = _project(item)
    if value.get("type") == "response" and "data" not in value:
        result["dataPresent"] = False
    if value.get("type") == "response" and "error" not in value:
        result["errorPresent"] = False
    return result


def _observation(run_results: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "runs": [
            {
                "exitCode": result["exitCode"],
                "timedOut": result["timedOut"],
                "inputError": result["inputError"],
                "records": _project(result["records"]),
                "stderr": result["stderr"].decode("utf-8", errors="replace"),
                "parseFailures": result["parseFailures"],
            }
            for result in run_results
        ]
    }


def _check_run(input_run: InputRun, result: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    if result["timedOut"]:
        errors.append("process timed out")
    if result["exitCode"] != 0:
        errors.append(f"process exited {result['exitCode']}")
    responses = [record for record in result["records"] if isinstance(record, dict) and record.get("type") == "response"]
    if input_run.expected_response_ids is not None:
        observed_ids = [record.get("id") if "id" in record else None for record in responses]
        if tuple(observed_ids) != input_run.expected_response_ids:
            errors.append(f"response IDs {observed_ids!r} differ from {input_run.expected_response_ids!r}")
    if input_run.all_responses_succeed and any(record.get("success") is not True for record in responses):
        errors.append("one or more expected successful responses failed")
    if input_run.expected_successes is not None:
        observed_successes = tuple(record.get("success") is True for record in responses)
        if observed_successes != input_run.expected_successes:
            errors.append(f"response success flags {observed_successes!r} differ from {input_run.expected_successes!r}")
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
    scenarios = R1_SCENARIOS.get(case_id)
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
            observations[product_run] = _observation(run_results)
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
    return set(R1_SCENARIOS)
