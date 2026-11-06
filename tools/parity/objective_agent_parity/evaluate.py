"""Independent functional and agent-loop scoring for ENV-PARSER-001 runs."""
from __future__ import annotations

import json
import re
import shutil
import sys
from pathlib import Path
from typing import Any

from common import (
    BASELINE_TESTS,
    FIXTURE_ROOT,
    PRIVATE_IDS,
    PRIVATE_ROOT,
    PRODUCTION_RELATIVE,
    STATE_ROOT,
    aggregate_manifest,
    baseline_manifest,
    clean_build_outputs,
    file_manifest,
    load_baseline,
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

CACHE_ROOT = STATE_ROOT / "evaluation-cache"
EVALUATOR_VERSION = 5


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


def evaluate_private(source: Path) -> dict[str, Any]:
    source_hash = sha256_file(source)

    def compute() -> dict[str, Any]:
        directory = STATE_ROOT / "private-eval" / source_hash
        if directory.exists():
            shutil.rmtree(directory)
        directory.mkdir(parents=True)
        shutil.copy2(source, directory / "EnvFile.cs")
        shutil.copy2(PRIVATE_ROOT / "EnvFilePrivateTests.cs", directory / "EnvFilePrivateTests.cs")
        shutil.copy2(PRIVATE_ROOT / "EnvFilePrivateTests.csproj", directory / "EnvFilePrivateTests.csproj")
        result = _run_project(directory / "EnvFilePrivateTests.csproj")
        outcomes = {
            test_id: next((outcome for name, outcome in result["tests"].items()
                           if f".{test_id}_" in name), "missing")
            for test_id in PRIVATE_IDS
        }
        return dict(sourceHash=source_hash, command=result["command"],
                    exitCode=result["exitCode"], durationSeconds=result["durationSeconds"],
                    timedOut=result["timedOut"], buildOutput=result["stdout"] + result["stderr"],
                    tests=result["tests"], outcomes=outcomes,
                    passed=sum(1 for value in outcomes.values() if value == "passed"))
    return _cached(f"private-{EVALUATOR_VERSION}-{source_hash}", compute)


def evaluate_visible(source: Path) -> dict[str, Any]:
    source_hash = sha256_file(source)

    def compute() -> dict[str, Any]:
        directory = STATE_ROOT / "visible-eval" / source_hash
        if directory.exists():
            shutil.rmtree(directory)
        shutil.copytree(FIXTURE_ROOT, directory, symlinks=False)
        clean_build_outputs(directory)
        shutil.copy2(source, directory / PRODUCTION_RELATIVE)
        result = _run_project(directory, timeout=300.0)
        outcomes = {name: test_outcome_for(result["tests"], name) for name in sorted(BASELINE_TESTS)}
        return dict(sourceHash=source_hash, command=result["command"],
                    exitCode=result["exitCode"], durationSeconds=result["durationSeconds"],
                    timedOut=result["timedOut"], buildOutput=result["stdout"] + result["stderr"],
                    tests=result["tests"], outcomes=outcomes,
                    allPassed=bool(outcomes) and all(value == "passed" for value in outcomes.values()))
    return _cached(f"visible-{EVALUATOR_VERSION}-{source_hash}", compute)


def change_integrity(snapshot: Path) -> dict[str, Any]:
    baseline = baseline_manifest()
    actual = file_manifest(snapshot)
    changed = sorted(path for path in set(baseline) | set(actual)
                     if baseline.get(path) != actual.get(path))
    permitted = set(changed) <= {PRODUCTION_RELATIVE}
    production_changed = PRODUCTION_RELATIVE in changed
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
        permittedProductionFile=PRODUCTION_RELATIVE,
        productionFileChanged=production_changed,
        onlyPermittedProductionFileChanged=permitted,
        snapshotChecksum=aggregate_manifest(actual),
        baselineChecksum=aggregate_manifest(baseline),
        passed=permitted,
    )


def _messages(trace) -> list[dict[str, Any]]:
    return trace.assistant_messages + trace.tool_results


def score_agent_loop(app: str, trace, session_messages: list[dict[str, Any]],
                     *, actual_visible_passed: bool, integrity: dict[str, Any]) -> dict[str, Any]:
    text = trace.tool_text()
    source_seen = text_mentions(text, ["EnvFile.cs", "src/Fixture.Core/EnvFile"])
    test_seen = text_mentions(text, ["EnvFileTests.cs", "tests/Fixture.Tests"])
    if source_seen and test_seen:
        investigation = 5
    elif source_seen or test_seen:
        investigation = 2
    else:
        investigation = 0

    successful_mutation = any(
        execution.is_error is False
        and execution.name in ("edit", "write", "replace", "insert")
        and text_mentions(json.dumps(execution.arguments or {}, ensure_ascii=False), ["EnvFile.cs"])
        for execution in trace.tool_executions)
    if successful_mutation and integrity.get("productionFileChanged"):
        modification = 5
    elif successful_mutation:
        modification = 3
    elif integrity.get("productionFileChanged"):
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
        fileModification=dict(score=modification, successfulMutation=successful_mutation,
                              productionFileChanged=bool(integrity.get("productionFileChanged"))),
        testExecution=dict(score=test_execution, ran=test_ran, executions=len(test_executions),
                           observedPass=observed_pass, actualVisiblePass=actual_visible_passed),
        toolLifecycle=dict(score=lifecycle, errors=trace.lifecycle_errors,
                           duplicateIds=sorted(set(trace.duplicate_ids))),
        modelContinuation=dict(score=model_continuation, **continuation),
        completion=dict(score=completion, reportedPass=reported_pass,
                        actualVisiblePass=actual_visible_passed,
                        duplicateSideEffects=duplicate_side_effects,
                        finalResponse=final),
        total=total,
        max=30,
    )


def evaluate_snapshot(run_directory: Path, app: str) -> dict[str, Any]:
    snapshot = run_directory / "workspace"
    source = snapshot / PRODUCTION_RELATIVE
    if not source.is_file():
        raise RuntimeError(f"Run snapshot lacks {PRODUCTION_RELATIVE}: {run_directory}")
    private = evaluate_private(source)
    visible = evaluate_visible(source)
    integrity = change_integrity(snapshot)
    events = read_jsonl(run_directory / "events.jsonl")
    trace = parse_agent_trace(app, events)
    sessions = load_session_messages(app, run_directory / "sessions")
    from common import enrich_trace_from_session
    enrich_trace_from_session(trace, sessions)
    agent = score_agent_loop(app, trace, sessions,
                             actual_visible_passed=visible["allPassed"], integrity=integrity)
    private_points = private["passed"] * 5
    visible_points = 5 if visible["allPassed"] else 0
    integrity_points = 5 if integrity["passed"] else 0
    functional = private_points + visible_points + integrity_points
    return dict(
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
