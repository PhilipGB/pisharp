#!/usr/bin/env python3
"""Validate an objective-agent-parity report and run falsification controls."""
from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
from statistics import mean
from typing import Any

from common import BASELINE_TESTS, PRIVATE_IDS, load_baseline, write_json

ROOT = Path(__file__).resolve().parents[3]
SEVERITIES = {"P0", "P1", "P2", "Inconclusive"}


def _error(errors: list[str], condition: bool, message: str) -> None:
    if not condition:
        errors.append(message)


def validate(report: dict[str, Any], *, require_complete: bool = True) -> list[str]:
    errors: list[str] = []
    baseline = load_baseline()
    _error(errors, report.get("fixture", {}).get("id") == "ENV-PARSER-001",
           "fixture id is not ENV-PARSER-001")
    _error(errors, report.get("fixture", {}).get("checksum") == baseline["fixtureChecksum"],
           "fixture checksum does not match baseline.json")
    runs = report.get("runs") or []
    _error(errors, len(runs) == 10, f"expected 10 real-model run records, found {len(runs)}")
    ids = [run.get("runId") for run in runs]
    _error(errors, len(ids) == len(set(ids)), "duplicate run IDs")
    for app in ("pi", "pisharp"):
        app_runs = [run for run in runs if run.get("app") == app]
        _error(errors, len(app_runs) == 5, f"expected five {app} runs, found {len(app_runs)}")

    scored = [run for run in runs if run.get("scored")]
    recomputed: dict[str, dict[str, Any]] = {}
    for run in runs:
        evaluation = run.get("evaluation")
        if not run.get("scored"):
            _error(errors, run.get("status") in ("blocked", "timed_out", "agent_error"),
                   f"{run.get('runId')} is not scored but has status {run.get('status')}")
            continue
        _error(errors, isinstance(evaluation, dict), f"{run.get('runId')} is scored without an evaluation")
        if not isinstance(evaluation, dict):
            continue
        private = evaluation.get("privateTests", {})
        outcomes = private.get("outcomes", {})
        _error(errors, sorted(outcomes) == PRIVATE_IDS,
               f"{run.get('runId')} private outcomes do not cover H01-H08")
        private_points = sum(1 for value in outcomes.values() if value == "passed") * 5
        _error(errors, private.get("passed") == sum(1 for value in outcomes.values() if value == "passed"),
               f"{run.get('runId')} private passed count is inconsistent")
        _error(errors, evaluation.get("functional", {}).get("privateTests") == private_points,
               f"{run.get('runId')} private points are inconsistent")
        visible = evaluation.get("visibleTests", {})
        _error(errors, sorted(visible.get("outcomes") or {}) == sorted(BASELINE_TESTS),
               f"{run.get('runId')} visible outcomes do not cover the three visible tests")
        visible_points = 5 if visible.get("allPassed") else 0
        _error(errors, evaluation.get("functional", {}).get("visibleTests") == visible_points,
               f"{run.get('runId')} visible points are inconsistent")
        integrity = evaluation.get("changeIntegrity", {})
        permitted = set(integrity.get("changedFiles") or []) <= {integrity.get("permittedProductionFile")}
        _error(errors, integrity.get("onlyPermittedProductionFileChanged") == permitted,
               f"{run.get('runId')} change-integrity permission flag is inconsistent")
        _error(errors, integrity.get("passed") == integrity.get("onlyPermittedProductionFileChanged"),
               f"{run.get('runId')} change-integrity flag is inconsistent")
        _error(errors, integrity.get("productionFileChanged")
               == (integrity.get("permittedProductionFile") in (integrity.get("changedFiles") or [])),
               f"{run.get('runId')} production-file change flag is inconsistent")
        integrity_points = 5 if integrity.get("passed") else 0
        _error(errors, evaluation.get("functional", {}).get("changeIntegrity") == integrity_points,
               f"{run.get('runId')} integrity points are inconsistent")
        functional_total = private_points + visible_points + integrity_points
        _error(errors, evaluation.get("functional", {}).get("total") == functional_total,
               f"{run.get('runId')} functional total is inconsistent")
        agent = evaluation.get("agentLoop", {})
        agent_keys = ["sourceInvestigation", "fileModification", "testExecution",
                      "toolLifecycle", "modelContinuation", "completion"]
        agent_sum = sum(agent.get(key, {}).get("score", -1000) for key in agent_keys)
        _error(errors, all(0 <= agent.get(key, {}).get("score", -1) <= 5 for key in agent_keys),
               f"{run.get('runId')} agent-loop component is outside 0-5")
        _error(errors, evaluation.get("agentTool", {}).get("total") == agent_sum,
               f"{run.get('runId')} agent/tool total is inconsistent")
        _error(errors, evaluation.get("runScore") == functional_total + agent_sum,
               f"{run.get('runId')} run score is inconsistent")
        expected_success = (private.get("passed") == 8 and bool(visible.get("allPassed"))
                            and bool(integrity.get("passed")))
        _error(errors, evaluation.get("completeTaskSuccess") == expected_success,
               f"{run.get('runId')} task-success flag is inconsistent")

    aggregates = report.get("aggregates", {})
    for app in ("pi", "pisharp"):
        app_scored = [run["evaluation"] for run in runs
                      if run.get("app") == app and run.get("scored")]
        aggregate = aggregates.get(app, {})
        _error(errors, aggregate.get("attemptedRuns") == 5,
               f"{app} attempted-run aggregate is not five")
        _error(errors, aggregate.get("assessableRuns") == len(app_scored),
               f"{app} assessable-run aggregate is inconsistent")
        if app_scored:
            _error(errors, aggregate.get("meanFunctionalScore") == round(
                mean(run["functional"]["total"] for run in app_scored), 4),
                f"{app} functional mean is inconsistent")
            _error(errors, aggregate.get("meanAgentToolScore") == round(
                mean(run["agentTool"]["total"] for run in app_scored), 4),
                f"{app} agent/tool mean is inconsistent")
            _error(errors, aggregate.get("meanRealModelScore") == round(
                mean(run["runScore"] for run in app_scored), 4),
                f"{app} real-model mean is inconsistent")
            successes = sum(1 for run in app_scored if run["completeTaskSuccess"])
            _error(errors, aggregate.get("completeTaskSuccesses") == successes,
                   f"{app} task-success count is inconsistent")
            _error(errors, aggregate.get("completeTaskSuccessRateOutOfFive") == round(successes / 5, 4),
                   f"{app} task-success rate is inconsistent")

    ui = report.get("ui", {})
    if ui.get("status") == "scored":
        cases = ui.get("cases", [])
        _error(errors, len(cases) == 2, "UI report must contain two terminal sizes")
        progress = 0
        visual = 0
        for case in cases:
            checkpoint_sum = sum(item.get("points", 0) for item in case.get("checkpoints", {}).values())
            _error(errors, checkpoint_sum == case.get("progressPoints"),
                   f"UI case {case.get('id')} progress points are inconsistent")
            _error(errors, 0 <= checkpoint_sum <= 10,
                   f"UI case {case.get('id')} progress exceeds 10")
            visual_case = case.get("visual", {})
            visual_sum = sum(visual_case.get(key, {}).get("points", 0) for key in
                             ("labelsIndicatorsPositions", "spacingIndentationWrappingLayout",
                              "coloursAndStyles", "footerCursorFinalState"))
            _error(errors, visual_sum == visual_case.get("total"),
                   f"UI case {case.get('id')} visual points are inconsistent")
            _error(errors, 0 <= visual_sum <= 5, f"UI case {case.get('id')} visual exceeds 5")
            progress += checkpoint_sum
            visual += visual_sum
        _error(errors, ui.get("progressPoints") == progress // 2,
               "UI aggregate progress points are inconsistent")
        _error(errors, ui.get("visualPoints") == visual, "UI aggregate visual points are inconsistent")
        _error(errors, ui.get("total") == ui.get("progressPoints", 0) + ui.get("visualPoints", 0),
               "UI total is inconsistent")
        _error(errors, 0 <= ui.get("total", -1) <= 20, "UI total is outside 0-20")
    else:
        _error(errors, ui.get("status") in ("inconclusive", "blocked"),
               f"unexpected UI status {ui.get('status')}")

    combined = aggregates.get("combined", {})
    ui_pi = 20 if ui.get("status") == "scored" and ui.get("calibration", {}).get("match") else None
    ui_pisharp = ui.get("total") if ui.get("status") == "scored" else None
    deterministic_ui = aggregates.get("deterministicUi", {})
    _error(errors, deterministic_ui.get("pi") == ui_pi,
           "aggregate Pi deterministic UI score is inconsistent")
    _error(errors, deterministic_ui.get("pisharp") == ui_pisharp,
           "aggregate PiSharp deterministic UI score is inconsistent")
    for app, ui_value in (("pi", ui_pi), ("pisharp", ui_pisharp)):
        real = aggregates.get(app, {}).get("meanRealModelScore")
        _error(errors, aggregates.get(app, {}).get("deterministicUiScore") == ui_value,
               f"{app} deterministic UI score is inconsistent")
        _error(errors, combined.get(app) == (None if real is None or ui_value is None
                                             else round(real + ui_value, 4)),
               f"combined {app} score is inconsistent")
    difference = aggregates.get("scoreDifferencePisharpMinusPi")
    if combined.get("pi") is not None and combined.get("pisharp") is not None:
        _error(errors, difference == round(combined["pisharp"] - combined["pi"], 4),
               "Pisharp-minus-Pi difference is inconsistent")

    gates = report.get("validityGates", {})
    for gate in ("sameFixtureBaseline", "modelConfigurationDocumented", "allAttemptedRunsAccountedFor",
                 "privateEvaluatorIndependent", "toolAndFilesystemTracesComplete",
                 "samePiCalibrationPassed", "deterministicScoreCalculation"):
        _error(errors, gates.get(gate) is True, f"validity gate not passed: {gate}")
    if require_complete:
        _error(errors, gates.get("fiveAssessableRunsPerApplication") is True,
               "fewer than five assessable runs per application")
        _error(errors, gates.get("comparisonComplete") is True,
               "real-model comparison is incomplete")

    differences = report.get("classifiedDifferences", [])
    for difference in differences:
        _error(errors, difference.get("severity") in SEVERITIES,
               f"difference {difference.get('id')} has an invalid severity")
        _error(errors, bool(difference.get("evidence")),
               f"difference {difference.get('id')} lacks evidence references")
    highest = report.get("highestPriorityConfirmedDiscrepancy")
    if differences:
        rank = {"P0": 0, "P1": 1, "P2": 2, "Inconclusive": 3}
        expected = sorted(differences, key=lambda item: rank[item["severity"]])[0]
        _error(errors, highest is not None and highest.get("id") == expected.get("id"),
               "highest-priority discrepancy is not the first severity-ranked difference")
    else:
        _error(errors, highest is None, "highest-priority discrepancy should be null when none are classified")
    _error(errors, isinstance(report.get("suggestedNextSingleDeliverable"), str)
           and bool(report.get("suggestedNextSingleDeliverable")),
           "suggested next deliverable is missing")
    return errors


def negative_controls(report: dict[str, Any]) -> list[dict[str, Any]]:
    controls: list[dict[str, Any]] = []

    def check(name: str, mutate) -> None:
        candidate = copy.deepcopy(report)
        mutate(candidate)
        errors = validate(candidate, require_complete=True)
        controls.append(dict(name=name, rejected=bool(errors), errors=errors[:5]))

    def inflate(candidate: dict[str, Any]) -> None:
        run = next(item for item in candidate["runs"] if item.get("scored"))
        run["evaluation"]["functional"]["privateTests"] = 45
        run["evaluation"]["functional"]["total"] = 55

    def over_agent(candidate: dict[str, Any]) -> None:
        run = next(item for item in candidate["runs"] if item.get("scored"))
        run["evaluation"]["agentLoop"]["completion"]["score"] = 99

    def drop_run(candidate: dict[str, Any]) -> None:
        candidate["runs"] = candidate["runs"][:-1]

    def wrong_checksum(candidate: dict[str, Any]) -> None:
        candidate["fixture"]["checksum"] = "0" * 64

    def forged_integrity(candidate: dict[str, Any]) -> None:
        run = next(item for item in candidate["runs"] if item.get("scored"))
        run["evaluation"]["changeIntegrity"]["changedFiles"] = ["tests/Fixture.Tests/EnvFileTests.cs"]
        run["evaluation"]["changeIntegrity"]["onlyPermittedProductionFileChanged"] = True
        run["evaluation"]["changeIntegrity"]["passed"] = True

    def forged_ui(candidate: dict[str, Any]) -> None:
        candidate["ui"]["total"] = 21
        candidate["ui"]["visualPoints"] = 10
        candidate["ui"]["cases"][0]["visual"]["total"] = 5

    def false_success(candidate: dict[str, Any]) -> None:
        run = next(item for item in candidate["runs"] if item.get("scored"))
        run["evaluation"]["completeTaskSuccess"] = True
        run["evaluation"]["privateTests"]["passed"] = 7

    def hidden_private_failure(candidate: dict[str, Any]) -> None:
        run = next(item for item in candidate["runs"] if item.get("scored"))
        run["evaluation"]["privateTests"]["outcomes"]["H02"] = "failed"
        run["evaluation"]["privateTests"]["passed"] = 8

    def wrong_run_score(candidate: dict[str, Any]) -> None:
        run = next(item for item in candidate["runs"] if item.get("scored"))
        run["evaluation"]["functional"]["total"] = 49

    check("inflated-private-score", inflate)
    check("agent-score-out-of-range", over_agent)
    check("missing-run", drop_run)
    check("wrong-fixture-checksum", wrong_checksum)
    check("forged-change-integrity", forged_integrity)
    check("inconsistent-ui-score", forged_ui)
    check("false-task-success", false_success)
    check("hidden-private-failure", hidden_private_failure)
    check("wrong-run-score", wrong_run_score)
    return controls


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--output", type=Path, default=None)
    parser.add_argument("--allow-incomplete", action="store_true")
    arguments = parser.parse_args()
    report = json.loads(arguments.report.read_text())
    errors = validate(report, require_complete=not arguments.allow_incomplete)
    controls = negative_controls(report)
    control_ok = all(control["rejected"] for control in controls)
    result = {
        "valid": not errors,
        "errors": errors,
        "negativeControls": controls,
        "negativeControlsPassed": control_ok,
        "requireComplete": not arguments.allow_incomplete,
    }
    output = arguments.output or arguments.report.with_name("verification.json")
    write_json(output, result)
    print(json.dumps(dict(valid=result["valid"], errors=errors,
                          negativeControlsPassed=control_ok), indent=2))
    return 0 if result["valid"] and control_ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
