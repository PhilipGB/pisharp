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


def command(app: str, agent: Path) -> list[str]:
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
            "--no-prompt-templates", "--no-context-files", "--tools", "bash",
            "--approve", *extra, "Run the fixture tool."]


def environment(app: str, agent: Path, workspace: Path) -> dict[str, str]:
    names = ["PATH", "HOME", "DOTNET_ROOT", "LD_LIBRARY_PATH"]
    result = {name: os.environ[name] for name in names if name in os.environ}
    result.update(LANG="C.UTF-8", LC_ALL="C.UTF-8", TERM="xterm-256color",
                  DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", PI_OFFLINE="1")
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
    result = run_agent_process(command(app, agent), cwd=workspace,
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


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path,
                        default=ROOT / "artifacts/parity/objective-agent-contract")
    arguments = parser.parse_args()
    arguments.output.mkdir(parents=True, exist_ok=True)
    build = run_command(["dotnet", "build", str(ROOT / "src/PiSharp.Cli/PiSharp.Cli.csproj"),
                         "-c", "Debug", "--nologo"], cwd=ROOT, timeout=600.0)
    server = FixtureServer(
        models=[],
        props={},
        behavior={
            "chatToolCallsByRequest": [[{
                "id": "call-objective-contract",
                "name": "bash",
                "arguments": {"command": "printf 'objective-contract-line\\n'"},
            }]],
            "chatResponseContentByRequest": ["fixture tool call", "Deterministic contract complete."],
        },
    )
    try:
        results = [evaluate_product(app, server) for app in ("pi", "pisharp")]
    finally:
        server.close()
    report = dict(
        fixtureId="ENV-PARSER-001-contract",
        build=dict(exitCode=build["exitCode"], durationSeconds=build["durationSeconds"]),
        products=results,
        passed=all(item["passed"] for item in results),
    )
    write_json(arguments.output / "contract-report.json", report)
    print(json.dumps(report, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
