#!/usr/bin/env python3
"""Deterministic provider-response contract for the agent loop and terminal tool flow.

Both products receive the same scripted OpenAI-compatible response: one bash
tool call followed by a final assistant answer. The local fixture server keeps
the exact provider request bodies, so continuation is checked at the wire
boundary rather than inferred from the final response.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any

from common import (
    STATE_ROOT,
    parse_agent_trace,
    read_jsonl,
    run_agent_process,
    run_command,
    write_json,
)
from fixture_transforms import DEFECT_A_NEW, DEFECT_A_OLD, DEFECT_A_VALUE_NEW, DEFECT_A_VALUE_OLD, DEFECT_B_NEW, DEFECT_B_OLD
from fixtures import get_fixture
from evaluate import evaluate_visible

ROOT = Path(__file__).resolve().parents[3]
PI_SOURCE = Path("/home/philip/Projects/pi-pinned")
PISHARP_DLL = ROOT / "src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll"
TERMINAL_TOOLS = ROOT / "tools/parity/terminal"
if str(TERMINAL_TOOLS) not in sys.path:
    sys.path.insert(0, str(TERMINAL_TOOLS))
from fixture_http import FixtureServer  # noqa: E402


def prepare_agent(server: FixtureServer, directory: Path) -> None:
    if directory.exists():
        shutil.rmtree(directory)
    directory.mkdir(parents=True, mode=0o700)
    (directory / "settings.json").write_text(json.dumps({"quietStartup": True, "theme": "dark"}))
    model = dict(id="fixture-model", name="Fixture Model", api="openai-completions",
                 reasoning=False, input=["text"], contextWindow=8192, maxTokens=1024,
                 cost=dict(input=0, output=0, cacheRead=0, cacheWrite=0))
    (directory / "models.json").write_text(json.dumps(dict(providers=dict(fixture=dict(
        baseUrl=server.url + "/v1", apiKey="fixture-key", api="openai-completions",
        models=[model])))))


def prepare_workspace(directory: Path) -> None:
    if directory.exists():
        shutil.rmtree(directory)
    directory.mkdir(parents=True)
    (directory / ".contract-marker").write_text("objective-agent-parity")


def command(app: str, agent: Path, prompt: str, tools: str = "bash") -> list[str]:
    if app == "pi":
        prefix = ["node", "--import",
                  (PI_SOURCE / "packages/coding-agent/src/experimental/source-resolver.ts").as_uri(),
                  str(PI_SOURCE / "packages/coding-agent/src/cli.ts")]
        extra = ["--no-mcp"]
    else:
        prefix = ["dotnet", str(PISHARP_DLL)]
        extra = []
    return [*prefix, "--provider", "fixture", "--model", "fixture-model", "--mode", "json",
            "--no-session", "--offline", "--no-extensions", "--no-skills",
            "--no-prompt-templates", "--no-context-files", "--tools", tools,
            "--approve", *extra, prompt]


def environment(app: str, agent: Path, workspace: Path) -> dict[str, str]:
    names = ["PATH", "HOME", "DOTNET_ROOT", "LD_LIBRARY_PATH"]
    result = {name: os.environ[name] for name in names if name in os.environ}
    result.update(LANG="C.UTF-8", LC_ALL="C.UTF-8", TERM="xterm-256color",
                  DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", PI_OFFLINE="1",
                  DOTNET_PROCESSOR_COUNT="2", DOTNET_CLI_USE_MSBUILD_SERVER="0",
                  MSBUILDDISABLENODEREUSE="1")
    result["PI_CODING_AGENT_DIR" if app == "pi" else "PISHARP_AGENT_DIR"] = str(agent)
    return result


def request_messages(request: dict[str, Any]) -> list[dict[str, Any]]:
    body = request.get("body") or {}
    messages = body.get("messages")
    return messages if isinstance(messages, list) else []


def find_continuation(requests: list[dict[str, Any]]) -> dict[str, Any]:
    chat = [request for request in requests
            if request.get("method") == "POST" and request.get("path", "").endswith("/chat/completions")]
    if len(chat) < 2:
        return dict(ok=False, reason=f"expected two chat requests, saw {len(chat)}",
                    requests=len(chat))
    assistant_calls: list[str] = []
    tool_results: list[dict[str, Any]] = []
    for request in chat:
        for message in request_messages(request):
            if message.get("role") == "assistant":
                for call in message.get("tool_calls") or []:
                    assistant_calls.append(str(call.get("id")))
            elif message.get("role") == "tool":
                tool_results.append(dict(id=message.get("tool_call_id"),
                                         content=message.get("content")))
    second = request_messages(chat[1])
    matching_call_index = None
    for index, message in enumerate(second):
        if message.get("role") == "assistant" and any(
                str(call.get("id")) in assistant_calls for call in (message.get("tool_calls") or [])):
            matching_call_index = index
    tool_indexes = [index for index, message in enumerate(second) if message.get("role") == "tool"]
    later_assistant = [index for index, message in enumerate(second)
                       if message.get("role") == "assistant"
                       and index > (matching_call_index if matching_call_index is not None else -1)]
    matched = [item for item in tool_results
               if isinstance(item.get("content"), str)
               and "objective-contract-line" in item["content"]]
    duplicate_ids = sorted({value for value in assistant_calls if assistant_calls.count(value) > 1})
    ordered = (matching_call_index is not None and bool(tool_indexes)
               and tool_indexes[0] > matching_call_index and not later_assistant)
    ok = (len(chat) == 2 and len(assistant_calls) == 1 and len(tool_results) == 1
          and len(matched) == 1 and ordered and not duplicate_ids)
    return dict(
        ok=ok, requests=len(chat), assistantToolCallIds=assistant_calls,
        toolResultCount=len(tool_results), matchedResultCount=len(matched),
        duplicateIds=duplicate_ids,
        secondRequestContainsToolResultAfterCall=ordered,
        resultIds=[item.get("id") for item in tool_results],
    )


def evaluate_product(app: str, server: FixtureServer, *, timeout: float = 120.0) -> dict[str, Any]:
    agent = STATE_ROOT / "contract" / "agent"
    workspace = STATE_ROOT / "contract" / "workspace"
    prepare_agent(server, agent)
    prepare_workspace(workspace)
    server.reset()
    result = run_agent_process(command(app, agent, "Run the fixture tool.", "bash"), cwd=workspace,
                               env=environment(app, agent, workspace), timeout=timeout)
    events_path = STATE_ROOT / "contract" / f"{app}.jsonl"
    events_path.write_text(result["stdout"])
    events = read_jsonl(events_path)
    trace = parse_agent_trace(app, events)
    continuation = find_continuation(server.requests)
    lifecycle_ok = (len(trace.tool_executions) == 1
                    and trace.tool_executions[0].name == "bash"
                    and trace.tool_executions[0].start_index is not None
                    and trace.tool_executions[0].end_index is not None
                    and not trace.lifecycle_errors
                    and "objective-contract-line" in trace.tool_executions[0].result_text)
    final = trace.final_text
    if app == "pisharp":
        final = next((str(event.get("data", {}).get("Text") or "") for event in reversed(events)
                      if event.get("data", {}).get("Type") == "agent_run_completed"
                      and event.get("data", {}).get("Text")), final)
    completion_ok = "Deterministic contract complete." in (final or "")
    settled = any((event.get("type") == "agent_settled")
                  or (event.get("data", {}).get("Type") in ("agent_settled", "agent_run_completed"))
                  for event in events)
    return dict(
        app=app, exitCode=result["exitCode"], timedOut=result["timedOut"],
        toolLifecycle=dict(ok=lifecycle_ok, errors=trace.lifecycle_errors,
                           executions=[dict(id=item.call_id, name=item.name,
                                            result=item.result_text[:200], isError=item.is_error)
                                       for item in trace.tool_executions]),
        modelContinuation=continuation,
        completion=dict(ok=completion_ok, settled=settled, finalText=(final or "")[-500:]),
        passed=bool(result["exitCode"] == 0 and not result["timedOut"] and lifecycle_ok
                    and continuation["ok"] and completion_ok and settled),
    )


def prepare_fixture_workspace(spec, directory: Path) -> None:
    if directory.exists():
        shutil.rmtree(directory)
    shutil.copytree(spec.root, directory, symlinks=False)
    for name in ("bin", "obj", "TestResults"):
        for path in list(directory.rglob(name)):
            if path.is_dir():
                shutil.rmtree(path, ignore_errors=True)
    result = run_command(["dotnet", "restore", "Fixture.slnx", "--nologo"],
                         cwd=directory, env=contract_environment("pi"), timeout=300.0)
    if result["exitCode"] != 0:
        raise RuntimeError(f"contract workspace restore failed: {result['stderr'][-2000:]}")


def contract_environment(app: str, agent: Path | None = None) -> dict[str, str]:
    names = ["PATH", "HOME", "DOTNET_ROOT", "LD_LIBRARY_PATH", "NUGET_PACKAGES",
             "NUGET_HTTP_CACHE_PATH", "DOTNET_CLI_HOME"]
    result = {name: os.environ[name] for name in names if name in os.environ}
    result.update(LANG="C.UTF-8", LC_ALL="C.UTF-8", TERM="xterm-256color",
                  DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", PI_OFFLINE="1",
                  DOTNET_PROCESSOR_COUNT="2", DOTNET_CLI_USE_MSBUILD_SERVER="0",
                  MSBUILDDISABLENODEREUSE="1")
    if agent is not None:
        result["PI_CODING_AGENT_DIR" if app == "pi" else "PISHARP_AGENT_DIR"] = str(agent)
    return result


def recovery_behavior() -> dict[str, Any]:
    command = "dotnet test Fixture.slnx --no-restore"
    calls = [
        dict(id="recovery-01", name="read", arguments={"path": "src/Fixture.Core/EnvFile.cs"}),
        dict(id="recovery-02", name="read", arguments={"path": "src/Fixture.Core/EnvInterpolator.cs"}),
        dict(id="recovery-03", name="bash", arguments={"command": command, "timeout": 120}),
        dict(id="recovery-04", name="read", arguments={"path": "src/Fixture.Core/Missing.cs"}),
        dict(id="recovery-05", name="read", arguments={"path": "src/Fixture.Core/EnvFile.cs"}),
        dict(id="recovery-06", name="edit", arguments={
            "path": "src/Fixture.Core/EnvFile.cs",
            "edits": [
                dict(oldText=DEFECT_A_OLD, newText=DEFECT_A_NEW),
                dict(oldText=DEFECT_A_VALUE_OLD, newText=DEFECT_A_VALUE_NEW),
            ],
        }),
        dict(id="recovery-07", name="bash", arguments={"command": command, "timeout": 120}),
        dict(id="recovery-08", name="edit", arguments={
            "path": "src/Fixture.Core/EnvInterpolator.cs",
            "edits": [dict(oldText=DEFECT_B_OLD, newText=DEFECT_B_NEW)],
        }),
        dict(id="recovery-09", name="bash", arguments={"command": command, "timeout": 120}),
    ]
    return {
        "chatToolCallsByRequest": [[call] for call in calls] + [[]],
        "chatResponseContentByRequest": [""] * len(calls) + ["Multi-step contract complete."],
    }


def recovery_continuation(requests: list[dict[str, Any]], expected_calls: int) -> dict[str, Any]:
    chat = [request for request in requests
            if request.get("method") == "POST" and request.get("path", "").endswith("/chat/completions")]
    counts = []
    ids_seen: list[str] = []
    duplicates: list[str] = []
    failing_result_present = False
    for request in chat:
        messages = request_messages(request)
        result_ids = [str(message.get("tool_call_id")) for message in messages
                      if message.get("role") == "tool"]
        counts.append(len(result_ids))
        duplicates.extend(sorted({value for value in result_ids if result_ids.count(value) > 1}))
        ids_seen.extend(result_ids)
        for message in messages:
            if message.get("role") == "tool":
                content = str(message.get("content") or "")
                if "Missing.cs" in content and any(marker in content.lower() for marker in
                                                   ("not found", "no such file", "enoent", "error")):
                    failing_result_present = True
    expected_counts = list(range(expected_calls + 1))
    expected_ids = [f"recovery-{index:02d}" for index in range(1, expected_calls + 1)]
    final_result_ids = [str(message.get("tool_call_id")) for message in request_messages(chat[-1])
                        if message.get("role") == "tool"] if chat else []
    unique_seen = sorted(set(ids_seen))
    ok = (len(chat) == expected_calls + 1
          and counts == expected_counts
          and not duplicates
          and unique_seen == sorted(expected_ids)
          and sorted(final_result_ids) == sorted(expected_ids)
          and failing_result_present)
    return dict(ok=ok, requests=len(chat), resultCounts=counts,
                expectedResultCounts=expected_counts,
                duplicateIds=duplicates, resultIds=unique_seen,
                failingResultPresent=failing_result_present)


def evaluate_recovery_product(app: str, server: FixtureServer, spec,
                              *, timeout: float = 300.0) -> dict[str, Any]:
    agent = STATE_ROOT / "contract-002" / "agent"
    workspace = STATE_ROOT / "contract-002" / "workspace"
    prepare_agent(server, agent)
    prepare_fixture_workspace(spec, workspace)
    server.reset()
    result = run_agent_process(
        command(app, agent, "Exercise the multi-step repair.", "read,bash,edit"),
        cwd=workspace, env=contract_environment(app, agent), timeout=timeout)
    events_path = STATE_ROOT / "contract-002" / f"{app}.jsonl"
    events_path.write_text(result["stdout"])
    events = read_jsonl(events_path)
    trace = parse_agent_trace(app, events)
    expected_names = ["read", "read", "bash", "read", "read", "edit", "bash", "edit", "bash"]
    expected_errors = [False, False, True, True, False, False, True, False, False]
    lifecycle_ok = (len(trace.tool_executions) == len(expected_names)
                    and [item.name for item in trace.tool_executions] == expected_names
                    and [item.is_error for item in trace.tool_executions] == expected_errors
                    and not trace.lifecycle_errors)
    continuation = recovery_continuation(server.requests, len(expected_names))
    final = trace.final_text
    completion_ok = "Multi-step contract complete." in (final or "")
    settled = any((event.get("type") == "agent_settled")
                  or (event.get("data", {}).get("Type") in ("agent_settled", "agent_run_completed"))
                  for event in events)
    visible = evaluate_visible(spec, workspace)
    changed = [relative for relative in spec.production_files
               if (workspace / relative).read_bytes() != (spec.root / relative).read_bytes()]
    recovered = sum(
        1 for index, execution in enumerate(trace.tool_executions)
        if execution.is_error is True
        and any(later.is_error is False for later in trace.tool_executions[index + 1:]))
    passed = bool(result["exitCode"] == 0 and not result["timedOut"] and lifecycle_ok
                  and continuation["ok"] and completion_ok and settled
                  and visible["allPassed"] and sorted(changed) == sorted(spec.production_files))
    return dict(
        app=app, exitCode=result["exitCode"], timedOut=result["timedOut"],
        durationSeconds=result["durationSeconds"],
        toolLifecycle=dict(ok=lifecycle_ok, errors=trace.lifecycle_errors,
                           executions=[dict(id=item.call_id, name=item.name,
                                            isError=item.is_error,
                                            result=item.result_text[:300])
                                       for item in trace.tool_executions]),
        modelContinuation=continuation,
        completion=dict(ok=completion_ok, settled=settled, finalText=(final or "")[-500:]),
        recovery=dict(toolErrors=sum(1 for item in trace.tool_executions if item.is_error is True),
                      recoveredToolErrors=recovered,
                      testExecutions=sum(1 for item in trace.tool_executions
                                         if "dotnet test" in json.dumps(item.arguments or {}).lower()
                                         or "dotnet test" in item.result_text.lower()
                                         or "Test run for" in item.result_text
                                         or "Passed!" in item.result_text
                                         or "Failed!" in item.result_text),
                      failingReadConveyed=continuation["failingResultPresent"]),
        finalWorkspace=dict(changedProductionFiles=changed, visiblePassed=visible["allPassed"]),
        passed=passed,
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", default="ENV-PARSER-001",
                        choices=["ENV-PARSER-001", "ENV-PARSER-002"])
    parser.add_argument("--output", type=Path,
                        default=ROOT / "artifacts/parity/objective-agent-contract")
    arguments = parser.parse_args()
    spec = get_fixture(arguments.fixture)
    arguments.output.mkdir(parents=True, exist_ok=True)
    build = run_command(["dotnet", "build", str(ROOT / "src/PiSharp.Cli/PiSharp.Cli.csproj"),
                         "-c", "Debug", "--nologo"], cwd=ROOT, timeout=600.0)
    if spec.contract_mode == "recovery":
        behavior = recovery_behavior()
    else:
        behavior = {
            "chatToolCallsByRequest": [[{
                "id": "call-objective-contract",
                "name": "bash",
                "arguments": {"command": "printf 'objective-contract-line\\n'"},
            }]],
            "chatResponseContentByRequest": ["fixture tool call", "Deterministic contract complete."],
        }
    server = FixtureServer(models=[], props={}, behavior=behavior)
    try:
        if spec.contract_mode == "recovery":
            results = [evaluate_recovery_product(app, server, spec)
                       for app in ("pi", "pisharp")]
        else:
            results = [evaluate_product(app, server) for app in ("pi", "pisharp")]
    finally:
        server.close()
    report = dict(
        fixtureId=f"{spec.id}-contract",
        mode=spec.contract_mode,
        build=dict(exitCode=build["exitCode"], durationSeconds=build["durationSeconds"]),
        products=results,
        passed=all(item["passed"] for item in results),
    )
    write_json(arguments.output / "contract-report.json", report)
    print(json.dumps(report, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
