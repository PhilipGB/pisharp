"""Shared primitives for the ENV-PARSER-001 objective agent-parity milestone.

The module deliberately keeps evaluation state outside the coding agent's
workspace. It provides deterministic fixture hashing, workspace reset, trace
normalisation and TRX parsing used by the runner, evaluator and verifier.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import tempfile
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

FIXTURE_ID = "ENV-PARSER-001"
FIXTURE_ROOT = Path(__file__).with_name("fixture") / FIXTURE_ID
PRIVATE_ROOT = Path(__file__).with_name("private")
BASELINE_PATH = Path(__file__).with_name("baseline.json")
STATE_ROOT = Path("/home/philip/.cache/tmp/opencode/objective-agent-parity")
WORKSPACE = STATE_ROOT / FIXTURE_ID
LOCK_PATH = STATE_ROOT / ".run.lock"
PRODUCTION_RELATIVE = "src/Fixture.Core/EnvFile.cs"
BASELINE_TESTS = {
    "Fixture.Tests.EnvFileTests.StandardAssignmentsParseSuccessfully",
    "Fixture.Tests.EnvFileTests.CommentsAndBlankLinesAreIgnored",
    "Fixture.Tests.EnvFileTests.ValuesContainingAdditionalEqualsSignsArePreserved",
}
EXPECTED_BASELINE_FAILURE = "Fixture.Tests.EnvFileTests.ValuesContainingAdditionalEqualsSignsArePreserved"
PRIVATE_IDS = [f"H{i:02d}" for i in range(1, 9)]
EXCLUDED_PARTS = {"bin", "obj", "TestResults", ".git", ".vs", "node_modules"}
EXCLUDED_SUFFIXES = {".user", ".suo"}


def _spec(spec=None):
    if spec is not None:
        return spec
    from fixtures import get_fixture
    return get_fixture(FIXTURE_ID)



def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def _is_excluded(relative: Path) -> bool:
    return any(part in EXCLUDED_PARTS for part in relative.parts) or relative.suffix in EXCLUDED_SUFFIXES


def file_manifest(root: Path, *, include_new: bool = True) -> dict[str, str]:
    """Return sorted relative-path to SHA-256 for evaluator-relevant files."""
    manifest: dict[str, str] = {}
    if not root.exists():
        return manifest
    for path in sorted(root.rglob("*")):
        if not path.is_file() or path.is_symlink():
            continue
        relative = path.relative_to(root)
        if _is_excluded(relative):
            continue
        manifest[relative.as_posix()] = sha256_file(path)
    return manifest


def aggregate_manifest(manifest: dict[str, str]) -> str:
    digest = hashlib.sha256()
    for path in sorted(manifest):
        digest.update(path.encode("utf-8"))
        digest.update(b"\0")
        digest.update(manifest[path].encode("ascii"))
        digest.update(b"\n")
    return digest.hexdigest()


def baseline_manifest(spec=None) -> dict[str, str]:
    return file_manifest(_spec(spec).root)


def baseline_checksum(spec=None) -> str:
    return aggregate_manifest(baseline_manifest(spec))


def load_baseline(spec=None) -> dict[str, Any]:
    return json.loads(_spec(spec).baseline_path.read_text())


def verify_baseline(spec=None) -> dict[str, Any]:
    spec = _spec(spec)
    expected = load_baseline(spec)
    actual = baseline_manifest(spec)
    actual_checksum = aggregate_manifest(actual)
    if actual_checksum != expected["fixtureChecksum"]:
        raise RuntimeError(
            f"Fixture baseline checksum mismatch: expected {expected['fixtureChecksum']}, got {actual_checksum}")
    if actual != expected["files"]:
        differing = sorted(set(actual) ^ set(expected["files"])) + [
            path for path in sorted(set(actual) & set(expected["files"]))
            if actual[path] != expected["files"][path]]
        raise RuntimeError(f"Fixture baseline manifest mismatch: {differing[:10]}")
    return expected


def reset_workspace(spec=None) -> dict[str, Any]:
    """Replace the agent workspace with a verified baseline copy."""
    spec = _spec(spec)
    STATE_ROOT.mkdir(parents=True, exist_ok=True)
    workspace = spec.workspace
    marker = STATE_ROOT / ".objective-agent-parity"
    if workspace.exists():
        if not marker.exists() and not (workspace.parent == STATE_ROOT):
            raise RuntimeError(f"Refusing to remove unowned workspace: {workspace}")
        shutil.rmtree(workspace)
    marker.touch()
    shutil.copytree(spec.root, workspace, symlinks=False)
    for transient in ("bin", "obj", "TestResults"):
        for path in list(workspace.rglob(transient)):
            if path.is_dir():
                shutil.rmtree(path, ignore_errors=True)
    expected = verify_baseline(spec)
    copied = file_manifest(workspace)
    if aggregate_manifest(copied) != expected["fixtureChecksum"]:
        raise RuntimeError("Workspace copy does not match the verified fixture checksum")
    return expected


def clean_build_outputs(root: Path) -> None:
    for name in ("bin", "obj", "TestResults"):
        for path in list(root.rglob(name)):
            if path.is_dir():
                shutil.rmtree(path, ignore_errors=True)


def run_command(
    command: list[str],
    *,
    cwd: Path | None = None,
    env: dict[str, str] | None = None,
    timeout: float | None = None,
    capture: bool = True,
) -> dict[str, Any]:
    started = time.monotonic()
    try:
        completed = subprocess.run(
            command,
            cwd=str(cwd) if cwd else None,
            env=env,
            stdout=subprocess.PIPE if capture else subprocess.DEVNULL,
            stderr=subprocess.PIPE if capture else subprocess.DEVNULL,
            text=True,
            errors="replace",
            timeout=timeout,
            check=False,
        )
        return dict(command=command, exitCode=completed.returncode,
                    stdout=completed.stdout or "", stderr=completed.stderr or "",
                    durationSeconds=time.monotonic() - started, timedOut=False)
    except subprocess.TimeoutExpired as error:
        return dict(command=command, exitCode=None,
                    stdout=(error.stdout or "") if isinstance(error.stdout, str) else "",
                    stderr=(error.stderr or "") if isinstance(error.stderr, str) else "",
                    durationSeconds=time.monotonic() - started, timedOut=True)


def restricted_preexec(cpu_seconds: int = 600, address_space_bytes: int = 8 * 1024 * 1024 * 1024) -> Any:
    """Identical process limits for both products; address space is optional."""
    try:
        import resource
    except ImportError:
        return None

    def apply() -> None:
        resource.setrlimit(resource.RLIMIT_CPU, (cpu_seconds, cpu_seconds))
        limits = [("RLIMIT_NOFILE", 4096)]
        if address_space_bytes:
            limits.append(("RLIMIT_AS", address_space_bytes))
        for name, value in limits:
            limit = getattr(resource, name, None)
            if limit is not None:
                try:
                    resource.setrlimit(limit, (value, value))
                except (ValueError, OSError):
                    pass
    return apply


def _tree_memory(pid: int) -> tuple[int, int]:
    try:
        import psutil
        process = psutil.Process(pid)
        processes = [process, *process.children(recursive=True)]
    except Exception:
        return 0, 0
    total = 0
    count = 0
    for item in processes:
        try:
            total += item.memory_info().rss
            count += 1
        except Exception:
            continue
    return total, count


def run_agent_process(
    command: list[str],
    *,
    cwd: Path,
    env: dict[str, str],
    timeout: float,
    address_space_bytes: int = 8 * 1024 * 1024 * 1024,
) -> dict[str, Any]:
    """Run one agent with a monotonic deadline, identical limits and peak-memory sampling."""
    started = time.monotonic()
    peak = {"bytes": 0, "processes": 0}
    stop = threading.Event()

    def sample() -> None:
        while not stop.is_set():
            total, count = _tree_memory(process.pid)
            if total > peak["bytes"]:
                peak["bytes"] = total
                peak["processes"] = count
            stop.wait(0.1)

    process = subprocess.Popen(
        command,
        cwd=str(cwd),
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        errors="replace",
        start_new_session=True,
        preexec_fn=restricted_preexec(int(timeout) + 5, address_space_bytes),
    )
    monitor = threading.Thread(target=sample, daemon=True)
    monitor.start()
    timed_out = False
    try:
        stdout, stderr = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            process.kill()
        stdout, stderr = process.communicate()
    finally:
        stop.set()
        monitor.join(timeout=2.0)
    elapsed = time.monotonic() - started
    return dict(command=command, exitCode=process.returncode,
                stdout=stdout or "", stderr=stderr or "",
                durationSeconds=elapsed, timedOut=timed_out,
                peakMemoryBytes=peak["bytes"], peakProcessCount=peak["processes"])


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    events: list[dict[str, Any]] = []
    if not path.exists():
        return events
    for line in path.read_text(errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict):
            events.append(value)
    return events


def parse_trx(path: Path) -> dict[str, str]:
    """Map fully-qualified test names to passed/failed/notExecuted."""
    if not path.exists():
        return {}
    try:
        import xml.etree.ElementTree as ET
        tree = ET.parse(path)
    except Exception:
        return {}
    namespace = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    results: dict[str, str] = {}
    for result in tree.getroot().findall(".//t:UnitTestResult", namespace):
        name = result.get("testName")
        outcome = result.get("outcome")
        if name:
            results[name] = {"Passed": "passed", "Failed": "failed",
                             "NotExecuted": "skipped"}.get(outcome or "", "unknown")
    if not results:
        for result in tree.getroot().iter():
            if result.tag.endswith("UnitTestResult") and result.get("testName"):
                results[result.get("testName")] = {
                    "Passed": "passed", "Failed": "failed",
                    "NotExecuted": "skipped"}.get(result.get("outcome") or "", "unknown")
    return results


def test_outcome_for(results: dict[str, str], suffix: str) -> str:
    for name, outcome in results.items():
        if name.endswith(suffix) or suffix in name:
            return outcome
    return "missing"


@dataclass
class ToolExecution:
    call_id: str
    name: str
    arguments: Any = None
    start_index: int | None = None
    end_index: int | None = None
    result_text: str = ""
    is_error: bool | None = None


@dataclass
class Trace:
    app: str
    events: list[dict[str, Any]] = field(default_factory=list)
    tool_executions: list[ToolExecution] = field(default_factory=list)
    assistant_messages: list[dict[str, Any]] = field(default_factory=list)
    tool_results: list[dict[str, Any]] = field(default_factory=list)
    model_requests: int = 0
    final_text: str = ""
    lifecycle_errors: list[str] = field(default_factory=list)
    duplicate_ids: list[str] = field(default_factory=list)

    def tool_text(self) -> str:
        chunks: list[str] = []
        for execution in self.tool_executions:
            chunks.append(json.dumps(execution.arguments, ensure_ascii=False))
            chunks.append(execution.result_text)
        return "\n".join(chunks)


def _event_kind(event: dict[str, Any]) -> str:
    if event.get("type") == "event" and isinstance(event.get("data"), dict):
        return str(event["data"].get("Type") or "")
    return str(event.get("type") or "")


def _event_data(event: dict[str, Any]) -> dict[str, Any]:
    if event.get("type") == "event" and isinstance(event.get("data"), dict):
        return event["data"]
    return event


def _result_text(result: Any) -> str:
    if isinstance(result, str):
        return result
    if isinstance(result, dict):
        if isinstance(result.get("content"), list):
            return "\n".join(str(item.get("text", "")) for item in result["content"]
                             if isinstance(item, dict))
        if isinstance(result.get("Text"), str):
            return result["Text"]
        if isinstance(result.get("StructuredContent"), str):
            return result["StructuredContent"]
    return json.dumps(result, ensure_ascii=False) if result is not None else ""


def parse_agent_trace(app: str, events: list[dict[str, Any]]) -> Trace:
    trace = Trace(app=app, events=events)
    executions: dict[str, ToolExecution] = {}
    ended: set[str] = set()
    current_text = ""

    for index, event in enumerate(events):
        kind = _event_kind(event)
        data = _event_data(event)
        if app == "pisharp" and kind == "model_request_started":
            trace.model_requests += 1
            current_text = ""
        if app == "pisharp" and kind == "model_text_delta":
            current_text += str(data.get("Text") or "")
        if kind == "message_start" and app == "pi":
            message = event.get("message") or {}
            if message.get("role") == "assistant":
                trace.model_requests += 1
        if kind == "tool_execution_start":
            call_id = str(data.get("toolCallId") or "")
            execution = executions.get(call_id)
            if execution is None:
                execution = ToolExecution(call_id=call_id, name=str(data.get("toolName") or ""))
                executions[call_id] = execution
                trace.tool_executions.append(execution)
            else:
                trace.duplicate_ids.append(call_id)
            execution.start_index = index
            execution.arguments = data.get("args")
        elif kind == "tool_execution_started":
            call_id = str(data.get("OperationId") or "")
            execution = executions.get(call_id)
            if execution is None:
                execution = ToolExecution(call_id=call_id, name=str(data.get("Tool") or ""))
                executions[call_id] = execution
                trace.tool_executions.append(execution)
            else:
                trace.duplicate_ids.append(call_id)
            execution.start_index = index
        elif kind in ("tool_execution_end", "tool_execution_finished"):
            call_id = str(data.get("toolCallId") or data.get("OperationId") or "")
            execution = executions.get(call_id)
            if execution is None:
                execution = ToolExecution(call_id=call_id, name=str(data.get("toolName") or data.get("Tool") or ""))
                executions[call_id] = execution
                trace.tool_executions.append(execution)
                trace.lifecycle_errors.append(f"end without start: {call_id}")
            if execution.end_index is not None:
                trace.duplicate_ids.append(call_id)
            execution.end_index = index
            execution.result_text = _result_text(data.get("result") if "result" in data else data.get("Text"))
            execution.is_error = bool(data.get("isError") if "isError" in data else data.get("IsError"))
            ended.add(call_id)
        elif kind == "message_end" and app == "pi":
            message = event.get("message") or {}
            if message.get("role") == "assistant":
                trace.assistant_messages.append(message)
            elif message.get("role") == "toolResult":
                trace.tool_results.append(message)
        elif kind == "turn_completed" and app == "pisharp":
            pass

    for execution in trace.tool_executions:
        if execution.start_index is None:
            trace.lifecycle_errors.append(f"start missing: {execution.call_id}")
        if execution.end_index is None:
            trace.lifecycle_errors.append(f"end missing: {execution.call_id}")
        if execution.start_index is not None and execution.end_index is not None and execution.end_index <= execution.start_index:
            trace.lifecycle_errors.append(f"end before start: {execution.call_id}")
    if trace.duplicate_ids:
        trace.lifecycle_errors.append("duplicate tool execution ids")

    assistant_chunks: list[str] = []
    for message in trace.assistant_messages:
        content = message.get("content")
        if isinstance(content, list):
            for part in content:
                if isinstance(part, dict) and part.get("type") == "text":
                    assistant_chunks.append(str(part.get("text") or ""))
                elif isinstance(part, dict) and part.get("type") == "toolCall":
                    match = next((e for e in trace.tool_executions
                                  if e.call_id == part.get("id")), None)
                    if match and match.arguments is None:
                        match.arguments = part.get("arguments")
        elif isinstance(content, str):
            assistant_chunks.append(content)
    trace.final_text = assistant_chunks[-1] if assistant_chunks else ""
    if app == "pisharp" and current_text.strip():
        trace.final_text = current_text
    return trace


def session_message(message: dict[str, Any]) -> dict[str, Any]:
    if isinstance(message.get("PiEntry"), dict) and isinstance(message["PiEntry"].get("message"), dict):
        return message["PiEntry"]["message"]
    return message


def _pisharp_contents_to_pi(message: dict[str, Any]) -> list[dict[str, Any]]:
    """Convert PiSharp's serialized content union into Pi-shaped content parts."""
    converted: list[dict[str, Any]] = []
    for part in message.get("contents") or []:
        if not isinstance(part, dict):
            continue
        kind = part.get("$type")
        if kind == "text":
            converted.append(dict(type="text", text=part.get("text") or ""))
        elif kind == "reasoning":
            converted.append(dict(type="thinking", thinking=part.get("text") or ""))
        elif kind == "functionCall":
            converted.append(dict(type="toolCall", id=part.get("callId"),
                                  name=part.get("name"), arguments=part.get("arguments")))
        elif kind == "functionResult":
            result = part.get("result") or {}
            converted.append(dict(type="toolResult", toolCallId=part.get("callId"),
                                  toolName=part.get("name"),
                                  content=[dict(type="text", text=result.get("text") or "")],
                                  isError=bool((part.get("additionalProperties") or {}).get("pisharp.isError"))))
    return converted


def load_session_messages(app: str, session_root: Path) -> list[dict[str, Any]]:
    messages: list[dict[str, Any]] = []
    if not session_root.exists():
        return messages
    paths = sorted(session_root.rglob("*.jsonl")) if app == "pi" else sorted(session_root.rglob("*.session.json"))
    for path in paths:
        if app == "pi":
            for event in read_jsonl(path):
                if event.get("type") == "message" and isinstance(event.get("message"), dict):
                    messages.append(event["message"])
        else:
            try:
                document = json.loads(path.read_text())
            except (json.JSONDecodeError, OSError):
                continue
            for entry in document.get("Entries") or []:
                payload = entry.get("Payload") if isinstance(entry, dict) else None
                pi_entry = payload.get("PiEntry") if isinstance(payload, dict) else None
                message = pi_entry.get("message") if isinstance(pi_entry, dict) else None
                if not isinstance(message, dict) and isinstance(payload, dict):
                    message = session_message(payload.get("Message") or {})
                if not isinstance(message, dict) or not message:
                    continue
                if "contents" in message:
                    converted = _pisharp_contents_to_pi(message)
                    if message.get("role") == "tool":
                        for part in converted:
                            if part.get("type") == "toolResult":
                                messages.append(dict(role="toolResult", toolCallId=part["toolCallId"],
                                                     toolName=part.get("toolName"),
                                                     content=part.get("content"), isError=part.get("isError")))
                    else:
                        messages.append(dict(role=message.get("role"), content=converted,
                                             additionalProperties=message.get("additionalProperties")))
                else:
                    messages.append(message)
    return messages


def enrich_trace_from_session(trace: Trace, messages: list[dict[str, Any]]) -> None:
    """Fill arguments/results and assistant/tool-result ordering from session records."""
    call_arguments: dict[str, Any] = {}
    call_names: dict[str, str] = {}
    ordered_calls: list[dict[str, Any]] = []
    tool_results: list[dict[str, Any]] = []
    assistants: list[dict[str, Any]] = []
    for message in messages:
        role = message.get("role")
        if role == "assistant":
            assistants.append(message)
            content = message.get("content")
            if isinstance(content, list):
                for part in content:
                    if isinstance(part, dict) and part.get("type") == "toolCall":
                        call_id = str(part.get("id"))
                        call_arguments[call_id] = part.get("arguments")
                        call_names[call_id] = str(part.get("name") or "")
                        ordered_calls.append(dict(id=call_id, name=part.get("name"),
                                                  arguments=part.get("arguments")))
        elif role == "toolResult":
            tool_results.append(message)
    for index, execution in enumerate(trace.tool_executions):
        if execution.arguments is None and execution.call_id in call_arguments:
            execution.arguments = call_arguments[execution.call_id]
        if not execution.name and execution.call_id in call_names:
            execution.name = call_names[execution.call_id]
        if execution.arguments is None and trace.app == "pisharp" and index < len(ordered_calls):
            execution.arguments = ordered_calls[index].get("arguments")
            if not execution.name:
                execution.name = str(ordered_calls[index].get("name") or "")
        if not execution.result_text:
            match = next((item for item in tool_results
                          if item.get("toolCallId") == execution.call_id), None)
            if match is None and trace.app == "pisharp" and index < len(tool_results):
                match = tool_results[index]
            if match:
                execution.result_text = _result_text(match.get("content"))
    if not trace.assistant_messages:
        trace.assistant_messages = assistants
    if not trace.tool_results:
        trace.tool_results = tool_results
    if not trace.final_text:
        chunks: list[str] = []
        for message in assistants:
            content = message.get("content")
            if isinstance(content, str):
                chunks.append(content)
            elif isinstance(content, list):
                chunks.extend(str(part.get("text") or "") for part in content
                              if isinstance(part, dict) and part.get("type") == "text")
        trace.final_text = chunks[-1] if chunks else ""


def session_continuation(messages: list[dict[str, Any]]) -> dict[str, Any]:
    """Check that every tool result is followed by an assistant response and IDs are unique."""
    assistant_tool_calls: list[str] = []
    result_ids: list[str] = []
    followups: list[str] = []
    pending: list[str] = []
    for message in messages:
        role = message.get("role")
        if role == "assistant":
            content = message.get("content")
            calls = []
            if isinstance(content, list):
                calls = [str(part.get("id")) for part in content
                         if isinstance(part, dict) and part.get("type") == "toolCall"]
            assistant_tool_calls.extend(calls)
            if pending and not calls:
                followups.extend(pending)
                pending = []
        elif role == "toolResult":
            call_id = str(message.get("toolCallId") or "")
            result_ids.append(call_id)
            if call_id:
                pending.append(call_id)
    duplicates = sorted({value for value in result_ids if result_ids.count(value) > 1})
    missing_results = sorted(set(assistant_tool_calls) - set(result_ids))
    orphan_results = sorted(set(result_ids) - set(assistant_tool_calls))
    return dict(
        assistantToolCalls=len(assistant_tool_calls),
        toolResults=len(result_ids),
        followedByAssistant=len(followups),
        missingResults=missing_results,
        orphanResults=orphan_results,
        duplicateResults=duplicates,
    )


def text_mentions(text: str, needles: Iterable[str]) -> bool:
    lowered = text.lower()
    return any(needle.lower() in lowered for needle in needles)


def relative_files(root: Path) -> list[str]:
    return sorted(file_manifest(root))


def copy_tree(source: Path, destination: Path) -> None:
    if destination.exists():
        shutil.rmtree(destination)
    shutil.copytree(source, destination, symlinks=False)


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=False) + "\n")


def redact(value: Any, secrets: Iterable[str]) -> Any:
    text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False)
    for secret in secrets:
        if secret and len(secret) >= 8:
            text = text.replace(secret, "<redacted>")
    if isinstance(value, str):
        return text
    return json.loads(text)


def collect_secrets(paths: Iterable[Path]) -> list[str]:
    secrets: list[str] = []
    for path in paths:
        if not path.is_file():
            continue
        try:
            document = json.loads(path.read_text())
        except (json.JSONDecodeError, OSError):
            continue
        stack = [document]
        while stack:
            value = stack.pop()
            if isinstance(value, dict):
                for key, item in value.items():
                    if key in ("access", "refresh", "key", "secret", "apiKey", "id_token") and isinstance(item, str):
                        secrets.append(item)
                    elif isinstance(item, (dict, list)):
                        stack.append(item)
            elif isinstance(value, list):
                stack.extend(value)
    return secrets


def is_rate_limited(stdout: str, stderr: str) -> bool:
    text = f"{stdout[-8000:]}\n{stderr}".lower()
    patterns = ("rate limit", "rate-limit", "usage limit", "too many requests",
                "quota exceeded", "capacity exceeded", "status 429", "status code 429",
                "http 429", "\"status\":429", "429 too many")
    return any(pattern in text for pattern in patterns)


def monotonic_now() -> float:
    return time.monotonic()
