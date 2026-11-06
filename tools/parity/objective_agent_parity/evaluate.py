"""Independent functional and agent-loop scoring for ENV-PARSER-001 runs."""
from __future__ import annotations

import json
import re
import shutil
import sys
from pathlib import Path
from typing import Any

from common import (
    STATE_ROOT,
    _result_text,
    aggregate_manifest,
    baseline_manifest,
    clean_build_outputs,
    file_manifest,
    load_session_messages,
    parse_agent_trace,
    parse_trx,
    read_jsonl,
    run_command,
    session_continuation,
    sha256_file,
    test_outcome_for,
    text_mentions,
    write_json,
)
from fixtures import get_fixture

CACHE_ROOT = STATE_ROOT / "evaluation-cache"
EVALUATOR_VERSION = 6


def _dotnet_env() -> dict[str, str]:
    import os
    env = {name: os.environ[name] for name in
           ("PATH", "HOME", "DOTNET_ROOT", "LD_LIBRARY_PATH", "NUGET_PACKAGES",
            "NUGET_HTTP_CACHE_PATH", "DOTNET_CLI_HOME") if name in os.environ}
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    return env


def _cached(key: str, compute) -> Any:
    CACHE_ROOT.mkdir(parents=True, exist_ok=True)
    path = CACHE_ROOT / f"{key}.json"
    if path.exists():
        try:
            return json.loads(path.read_text())
        except json.JSONDecodeError:
            pass
    value = compute()
    write_json(path, value)
    return value


def _run_project(project: Path, *, timeout: float = 300.0) -> dict[str, Any]:
    report_name = "results.trx"
    result = run_command(
        ["dotnet", "test", str(project.name if project.is_file() else project), "--nologo",
         "--verbosity", "quiet", "--logger", f"trx;LogFileName={report_name}"],
        cwd=project.parent if project.is_file() else project,
        env=_dotnet_env(),
        timeout=timeout,
    )
    search_root = project.parent if project.is_file() else project
    candidates = sorted(search_root.rglob(report_name))
    result["tests"] = parse_trx(candidates[-1]) if candidates else {}
    return result


def _source_hash(spec, snapshot: Path) -> str:
    digest = [spec.id]
    for relative in spec.production_files:
        path = snapshot / relative
        digest.append(f"{relative}:{sha256_file(path)}")
    import hashlib
    return hashlib.sha256("\n".join(digest).encode()).hexdigest()


def evaluate_private(spec, snapshot: Path) -> dict[str, Any]:
    source_hash = _source_hash(spec, snapshot)

    def compute() -> dict[str, Any]:
        directory = STATE_ROOT / "private-eval" / f"{spec.id}-{source_hash}"
        if directory.exists():
            shutil.rmtree(directory)
        directory.mkdir(parents=True)
        for relative in spec.production_files:
            shutil.copy2(snapshot / relative, directory / Path(relative).name)
        shutil.copy2(spec.private_dir / "EnvFilePrivateTests.cs", directory / "EnvFilePrivateTests.cs")
        shutil.copy2(spec.private_dir / "EnvFilePrivateTests.csproj", directory / "EnvFilePrivateTests.csproj")
        result = _run_project(directory / "EnvFilePrivateTests.csproj")
        outcomes = {
            test_id: next((outcome for name, outcome in result["tests"].items()
                           if f".{test_id}_" in name), "missing")
            for test_id in spec.private_ids
        }
        return dict(fixtureId=spec.id, sourceHash=source_hash, command=result["command"],
                    exitCode=result["exitCode"], durationSeconds=result["durationSeconds"],
                    timedOut=result["timedOut"], buildOutput=result["stdout"] + result["stderr"],
                    tests=result["tests"], outcomes=outcomes,
                    passed=sum(1 for value in outcomes.values() if value == "passed"))
    return _cached(f"private-{EVALUATOR_VERSION}-{spec.id}-{source_hash}", compute)


def evaluate_visible(spec, snapshot: Path) -> dict[str, Any]:
    source_hash = _source_hash(spec, snapshot)

    def compute() -> dict[str, Any]:
        directory = STATE_ROOT / "visible-eval" / f"{spec.id}-{source_hash}"
        if directory.exists():
            shutil.rmtree(directory)
        shutil.copytree(spec.root, directory, symlinks=False)
        clean_build_outputs(directory)
        for relative in spec.production_files:
            shutil.copy2(snapshot / relative, directory / relative)
        result = _run_project(directory, timeout=300.0)
        outcomes = {name: test_outcome_for(result["tests"], name) for name in sorted(spec.visible_tests)}
        return dict(fixtureId=spec.id, sourceHash=source_hash, command=result["command"],
                    exitCode=result["exitCode"], durationSeconds=result["durationSeconds"],
                    timedOut=result["timedOut"], buildOutput=result["stdout"] + result["stderr"],
                    tests=result["tests"], outcomes=outcomes,
                    allPassed=bool(outcomes) and all(value == "passed" for value in outcomes.values()))
    return _cached(f"visible-{EVALUATOR_VERSION}-{spec.id}-{source_hash}", compute)


def change_integrity(spec, snapshot: Path) -> dict[str, Any]:
    baseline = baseline_manifest(spec)
    actual = file_manifest(snapshot)
    changed = sorted(path for path in set(baseline) | set(actual)
                     if baseline.get(path) != actual.get(path))
    permitted_files = set(spec.production_files)
    permitted = set(changed) <= permitted_files
    production_changed = any(path in changed for path in permitted_files)
    return dict(
        changedFiles=changed,
        changedFileChecksums={path: dict(
            baseline=baseline.get(path),
            run=actual.get(path),
        ) for path in changed},
        addedFiles=sorted(set(actual) - set(baseline)),
        removedFiles=sorted(set(baseline) - set(actual)),
        modifiedFiles=sorted(path for path in set(baseline) & set(actual)
                             if baseline[path] != actual[path]),
        permittedProductionFiles=sorted(permitted_files),
        productionFilesChanged=sorted(path for path in permitted_files if path in changed),
        productionFileChanged=production_changed,
        onlyPermittedProductionFileChanged=permitted,
        snapshotChecksum=aggregate_manifest(actual),
        baselineChecksum=aggregate_manifest(baseline),
        passed=permitted,
    )


def _messages(trace) -> list[dict[str, Any]]:
    return trace.assistant_messages + trace.tool_results


def _content_text(content: Any) -> str:
    if isinstance(content, list):
        return "\n".join(str(item.get("text", "")) for item in content if isinstance(item, dict))
    if isinstance(content, str):
        return content
    return _result_text(content)


def recovery_metrics(trace, session_messages: list[dict[str, Any]]) -> dict[str, Any]:
    executions = trace.tool_executions
    errors = [execution for execution in executions if execution.is_error]
    recovered = 0
    for index, execution in enumerate(executions):
        if execution.is_error is not True:
            continue
        if any(later.is_error is False for later in executions[index + 1:]):
            recovered += 1
    test_executions = [
        execution for execution in executions
        if "dotnet test" in json.dumps(execution.arguments or {}, ensure_ascii=False).lower()
        or (execution.name == "bash" and "dotnet test" in execution.result_text.lower())
    ]
    session_results = [message for message in session_messages if message.get("role") == "toolResult"]
    conveyed = 0
    for index, execution in enumerate(executions):
        if execution.is_error is not True:
            continue
        match = next((message for message in session_results
                      if message.get("toolCallId") == execution.call_id), None)
        if match is None and index < len(session_results):
            match = session_results[index]
        if match is not None and (match.get("isError") is True
                                  or execution.result_text.strip()[:200] in
                                  _content_text(match.get("content"))):
            conveyed += 1
    first_mutation = next((index for index, execution in enumerate(executions)
                           if execution.is_error is False and execution.name in
                           ("edit", "write", "replace", "insert")), None)
    mutation_followed_by_test = bool(
        first_mutation is not None and any(
            "dotnet test" in json.dumps(execution.arguments or {}, ensure_ascii=False).lower()
            for execution in executions[first_mutation + 1:]))
    failed_test_positions = [execution.end_index for execution in test_executions
                             if execution.is_error is True and execution.end_index is not None]
    failed_test_followed_by_change = bool(
        failed_test_positions and any(
            execution.start_index is not None and execution.start_index > failed_test_positions[-1]
            and execution.name in ("edit", "write", "replace", "insert")
            for execution in executions))
    return dict(
        toolErrors=len(errors),
        recoveredToolErrors=recovered,
        testExecutions=len(test_executions),
        errorsConveyedToModel=conveyed,
        mutationFollowedByTest=mutation_followed_by_test,
        failedTestFollowedByChange=failed_test_followed_by_change,
        duplicateToolExecutionIds=sorted(set(trace.duplicate_ids)),
        duplicateSideEffects=bool(trace.duplicate_ids),
    )


def score_agent_loop(app: str, spec, trace, session_messages: list[dict[str, Any]],
                     *, actual_visible_passed: bool, integrity: dict[str, Any]) -> dict[str, Any]:
    text = trace.tool_text()
    source_seen = text_mentions(text, spec.source_markers)
    test_seen = text_mentions(text, spec.test_markers)
    if source_seen and test_seen:
        investigation = 5
    elif source_seen or test_seen:
        investigation = 2
    else:
        investigation = 0

    modified_files = set(integrity.get("productionFilesChanged") or [])
    successful_mutations = set()
    for execution in trace.tool_executions:
        if execution.is_error is not False or execution.name not in ("edit", "write", "replace", "insert"):
            continue
        arguments = json.dumps(execution.arguments or {}, ensure_ascii=False)
        for relative in spec.production_files:
            if relative in arguments or Path(relative).name in arguments:
                successful_mutations.add(relative)
    covered = modified_files & successful_mutations
    if len(covered) == len(spec.production_files):
        modification = 5
    elif covered or successful_mutations:
        modification = 3
    elif modified_files:
        modification = 2
    else:
        modification = 0

    test_executions = [
        execution for execution in trace.tool_executions
        if "dotnet test" in json.dumps(execution.arguments or {}, ensure_ascii=False).lower()
        or (execution.name == "bash" and "dotnet test" in execution.result_text.lower())
    ]
    test_ran = bool(test_executions)
    observed_pass = None
    if test_executions:
        last = test_executions[-1]
        result_text = last.result_text
        failed_match = re.search(r"Failed:\s*(\d+)", result_text, re.IGNORECASE)
        passed_match = re.search(r"Passed:\s*(\d+)", result_text, re.IGNORECASE)
        if failed_match:
            failures = int(failed_match.group(1))
            passes = int(passed_match.group(1)) if passed_match else 0
            observed_pass = failures == 0 and passes > 0
        elif re.search(r"\bPassed!\b", result_text):
            observed_pass = True
        elif re.search(r"\bFailed!\b|\berror\b", result_text, re.IGNORECASE):
            observed_pass = False
        elif last.is_error is not None:
            observed_pass = not last.is_error
    if test_ran and observed_pass == actual_visible_passed:
        test_execution = 5
    elif test_ran:
        test_execution = 3
    else:
        test_execution = 0

    lifecycle = 5 if not trace.lifecycle_errors else (2 if len(trace.lifecycle_errors) == 1 else 0)

    continuation = session_continuation(session_messages) if session_messages else {}
    if continuation:
        complete = (continuation["assistantToolCalls"] > 0
                    and continuation["toolResults"] == continuation["assistantToolCalls"]
                    and continuation["followedByAssistant"] >= continuation["toolResults"]
                    and not continuation["missingResults"]
                    and not continuation["orphanResults"]
                    and not continuation["duplicateResults"])
        if complete:
            model_continuation = 5
        elif continuation["followedByAssistant"] > 0 and not continuation["duplicateResults"]:
            model_continuation = 3
        else:
            model_continuation = 0
    else:
        model_continuation = 0

    final = trace.final_text.strip()
    reported_pass = None
    if final:
        lowered = final.lower()
        failed_counts = [int(value) for value in re.findall(r"\bfailed\s*[:=]?\s*(\d+)", lowered)]
        positive_failure = any(value > 0 for value in failed_counts)
        pass_signal = bool(
            re.search(r"\b(?:all\s+)?(?:tests?|suite)\b[^\n]{0,80}\b(?:pass|passes|passed|green)\b", lowered)
            or re.search(r"\b(?:pass|passes|passed|green)\b[^\n]{0,60}\b(?:tests?|suite)\b", lowered)
            or re.search(r"\b\d+\s+passed\b[^\n.\n]{0,40}\b0\s+failed\b", lowered)
            or re.search(r"\b0\s+failed\b[^\n.\n]{0,40}\b\d+\s+passed\b", lowered)
            or "test suite passes" in lowered)
        failure_signal = bool(
            positive_failure
            or re.search(r"\b(?:tests?|suite)\b[^\n]{0,40}\b(?:fail|failed|failing)\b", lowered)
            or "not pass" in lowered)
        if failure_signal and not (pass_signal and not positive_failure):
            reported_pass = False
        elif pass_signal:
            reported_pass = True
    duplicate_side_effects = bool(trace.duplicate_ids)
    if final and reported_pass == actual_visible_passed and not duplicate_side_effects:
        completion = 5
    elif final and reported_pass is None and not duplicate_side_effects:
        completion = 3
    elif final:
        completion = 0
    else:
        completion = 0

    total = investigation + modification + test_execution + lifecycle + model_continuation + completion
    return dict(
        sourceInvestigation=dict(score=investigation, sourceSeen=source_seen, testSeen=test_seen),
        fileModification=dict(score=modification, successfulMutation=bool(successful_mutations),
                              successfulMutationFiles=sorted(successful_mutations),
                              productionFilesChanged=sorted(modified_files)),
        testExecution=dict(score=test_execution, ran=test_ran, executions=len(test_executions),
                           observedPass=observed_pass, actualVisiblePass=actual_visible_passed),
        toolLifecycle=dict(score=lifecycle, errors=trace.lifecycle_errors,
                           duplicateIds=sorted(set(trace.duplicate_ids))),
        modelContinuation=dict(score=model_continuation, **continuation),
        completion=dict(score=completion, reportedPass=reported_pass,
                        actualVisiblePass=actual_visible_passed,
                        duplicateSideEffects=duplicate_side_effects,
                        finalResponse=final),
        recoveryMetrics=recovery_metrics(trace, session_messages),
        total=total,
        max=30,
    )


def evaluate_snapshot(spec, run_directory: Path, app: str) -> dict[str, Any]:
    snapshot = run_directory / "workspace"
    for relative in spec.production_files:
        if not (snapshot / relative).is_file():
            raise RuntimeError(f"Run snapshot lacks {relative}: {run_directory}")
    private = evaluate_private(spec, snapshot)
    visible = evaluate_visible(spec, snapshot)
    integrity = change_integrity(spec, snapshot)
    events = read_jsonl(run_directory / "events.jsonl")
    trace = parse_agent_trace(app, events)
    sessions = load_session_messages(app, run_directory / "sessions")
    from common import enrich_trace_from_session
    enrich_trace_from_session(trace, sessions)
    agent = score_agent_loop(app, spec, trace, sessions,
                             actual_visible_passed=visible["allPassed"], integrity=integrity)
    private_points = private["passed"] * 5
    visible_points = 5 if visible["allPassed"] else 0
    integrity_points = 5 if integrity["passed"] else 0
    functional = private_points + visible_points + integrity_points
    return dict(
        fixtureId=spec.id,
        privateTests=private,
        visibleTests=visible,
        changeIntegrity=integrity,
        agentLoop=agent,
        functional=dict(privateTests=private_points, visibleTests=visible_points,
                        changeIntegrity=integrity_points, total=functional, max=50),
        agentTool=dict(total=agent["total"], max=30),
        runScore=functional + agent["total"],
        runMax=80,
        completeTaskSuccess=bool(private["passed"] == 8 and visible["allPassed"] and integrity["passed"]),
    )
