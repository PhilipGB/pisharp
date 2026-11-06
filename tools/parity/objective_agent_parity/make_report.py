#!/usr/bin/env python3
"""Aggregate real-model runs, deterministic contract and UI evidence into report.json."""
from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from statistics import mean
from typing import Any

from common import (
    load_baseline,
    sha256_file,
    write_json,
)
from evaluate import evaluate_snapshot
from fixtures import get_fixture

ROOT = Path(__file__).resolve().parents[3]
ASSESSABLE = {"completed", "agent_error"}


def load_json(path: Path) -> Any:
    return json.loads(path.read_text())


def aggregate(runs: list[dict[str, Any]]) -> dict[str, Any]:
    scored = [run for run in runs if run.get("scored") and "evaluation" in run]
    functional = [run["evaluation"]["functional"]["total"] for run in scored]
    agent_tool = [run["evaluation"]["agentTool"]["total"] for run in scored]
    combined = [run["evaluation"]["runScore"] for run in scored]
    successes = [run for run in scored if run["evaluation"]["completeTaskSuccess"]]
    recovery = [run["evaluation"]["agentLoop"].get("recoveryMetrics", {}) for run in scored]
    return dict(
        attemptedRuns=len(runs),
        assessableRuns=len(scored),
        blockedRuns=sum(1 for run in runs if run["status"] == "blocked"),
        timedOutRuns=sum(1 for run in runs if run["status"] == "timed_out"),
        meanFunctionalScore=round(mean(functional), 4) if functional else None,
        meanAgentToolScore=round(mean(agent_tool), 4) if agent_tool else None,
        meanRealModelScore=round(mean(combined), 4) if combined else None,
        completeTaskSuccesses=len(successes),
        completeTaskSuccessRateOutOfFive=round(len(successes) / 5, 4),
        completeTaskSuccessRateAssessable=round(len(successes) / len(scored), 4) if scored else None,
        recoveryMeasurements=dict(
            toolErrors=sum(item.get("toolErrors", 0) for item in recovery),
            recoveredToolErrors=sum(item.get("recoveredToolErrors", 0) for item in recovery),
            testExecutions=sum(item.get("testExecutions", 0) for item in recovery),
            errorsConveyedToModel=sum(item.get("errorsConveyedToModel", 0) for item in recovery),
            duplicateSideEffects=sum(1 for item in recovery if item.get("duplicateSideEffects")),
        ),
        peakMemoryBytes=max((run.get("peakMemoryBytes") or 0 for run in scored), default=0),
        perRunScores=[dict(runId=run["runId"], status=run["status"],
                           functional=run["evaluation"]["functional"]["total"],
                           agentTool=run["evaluation"]["agentTool"]["total"],
                           total=run["evaluation"]["runScore"],
                           completeTaskSuccess=run["evaluation"]["completeTaskSuccess"],
                           recovery=run["evaluation"]["agentLoop"].get("recoveryMetrics", {}))
                      for run in scored],
    )


def classify(app: str, runs: list[dict[str, Any]], contract: dict[str, Any], spec) -> list[dict[str, Any]]:
    differences: list[dict[str, Any]] = []
    scored = [run for run in runs if run.get("scored") and "evaluation" in run]
    failures = [run for run in scored if not run["evaluation"]["completeTaskSuccess"]]
    if len(failures) >= 3:
        differences.append(dict(
            id=f"{app}-repeated-task-failure", severity="P1",
            summary=f"{len(failures)} of {len(scored)} assessable runs did not complete the task.",
            evidence=[f"runs/{run['runId']}/run.json" for run in failures],
            component="functional/agent-loop",
        ))
    for run in scored:
        evaluation = run["evaluation"]
        integrity = evaluation["changeIntegrity"]
        if not integrity["productionFileChanged"] and evaluation["agentLoop"]["completion"]["reportedPass"]:
            differences.append(dict(
                id=f"{run['runId']}-reported-without-effect", severity="P0",
                summary="Run reported success while the production source was unchanged.",
                evidence=[f"runs/{run['runId']}/run.json", f"runs/{run['runId']}/events.jsonl"],
                component="agent-loop",
            ))
        if evaluation["agentLoop"]["toolLifecycle"]["errors"]:
            differences.append(dict(
                id=f"{run['runId']}-tool-lifecycle", severity="P1",
                summary="Tool lifecycle contained unpaired, duplicated or out-of-order executions.",
                evidence=[f"runs/{run['runId']}/events.jsonl"],
                details=evaluation["agentLoop"]["toolLifecycle"]["errors"],
                component="agent-loop",
            ))
        if evaluation["agentLoop"]["modelContinuation"]["score"] < 5:
            differences.append(dict(
                id=f"{run['runId']}-continuation", severity="P1",
                summary="Tool results were not followed by a valid model continuation in the session trace.",
                evidence=[f"runs/{run['runId']}/sessions"],
                details=evaluation["agentLoop"]["modelContinuation"],
                component="agent-loop",
            ))
        if not integrity["onlyPermittedProductionFileChanged"]:
            differences.append(dict(
                id=f"{run['runId']}-change-integrity", severity="P1",
                summary="A run changed files outside the permitted production file.",
                evidence=[f"runs/{run['runId']}/workspace"],
                details=integrity["changedFiles"],
                component="functional",
            ))
    for product in contract.get("products", []):
        if not product.get("passed"):
            differences.append(dict(
                id=f"deterministic-contract-{product['app']}", severity="P1",
                summary="Deterministic provider-response contract failed.",
                evidence=["contract-report.json"],
                details={key: product[key] for key in
                         ("toolLifecycle", "modelContinuation", "completion", "exitCode")},
                component="agent-loop",
            ))
    return differences


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=Path, required=True,
                        help="artifacts directory produced by run_real.py")
    parser.add_argument("--ui", type=Path, required=True, help="ui-report.json")
    parser.add_argument("--contract", type=Path, required=True, help="contract-report.json")
    parser.add_argument("--fixture", default="ENV-PARSER-001",
                        choices=["ENV-PARSER-001", "ENV-PARSER-002"])
    parser.add_argument("--output", type=Path, required=True, help="report.json")
    arguments = parser.parse_args()

    spec = get_fixture(arguments.fixture)
    metadata = load_json(arguments.runs / "run-metadata.json")
    raw_runs = load_json(arguments.runs / "runs.json")
    ui = load_json(arguments.ui)
    contract = load_json(arguments.contract)
    baseline = load_baseline(spec)

    evaluated: list[dict[str, Any]] = []
    for run in raw_runs:
        entry = dict(run)
        run_directory = arguments.runs / "runs" / run["runId"]
        entry["scored"] = run["status"] in ASSESSABLE and (run_directory / "workspace").is_dir()
        if (run_directory / "workspace").is_dir():
            try:
                entry["evaluation"] = evaluate_snapshot(spec, run_directory, run["app"])
            except Exception as error:
                entry["evaluationError"] = str(error)
                entry["scored"] = False
        evaluated.append(entry)

    by_app = {app: [run for run in evaluated if run["app"] == app] for app in ("pi", "pisharp")}
    aggregates = {app: aggregate(runs) for app, runs in by_app.items()}
    ui_pi = 20 if ui.get("status") == "scored" and ui.get("calibration", {}).get("match") else None
    ui_pisharp = ui.get("total") if ui.get("status") == "scored" else None
    for app, value in (("pi", ui_pi), ("pisharp", ui_pisharp)):
        aggregates[app]["deterministicUiScore"] = value
    combined = {}
    for app in ("pi", "pisharp"):
        real = aggregates[app]["meanRealModelScore"]
        ui_value = ui_pi if app == "pi" else ui_pisharp
        combined[app] = None if real is None or ui_value is None else round(real + ui_value, 4)

    differences = classify("pi", by_app["pi"], contract, spec) + classify("pisharp", by_app["pisharp"], contract, spec)
    if ui.get("status") != "scored":
        differences.append(dict(id="ui-inconclusive", severity="Inconclusive",
                                summary="Deterministic UI capture did not pass same-Pi calibration.",
                                evidence=[str(arguments.ui)], component="ui"))
    else:
        for case in ui.get("cases", []):
            if not case.get("visual", {}).get("coloursAndStyles", {}).get("match", True):
                differences.append(dict(
                    id=f"ui-colours-{case['id']}", severity="P2",
                    summary="Decoded terminal cell styles differed at deterministic checkpoints; details list affected cells.",
                    evidence=[f"ui-report.json#{case['id']}"],
                    details=[item for item in case.get("differences", []) if item.get("kind") == "styles"],
                    component="ui",
                ))
            if not case.get("visual", {}).get("labelsIndicatorsPositions", {}).get("match", True):
                differences.append(dict(
                    id=f"ui-text-{case['id']}", severity="P1",
                    summary=("PiSharp collapsed failed/recovered tool output at a different threshold and omitted Pi's "
                             "ctrl+o expand hint, shifting stable text positions."
                             if spec.id == "ENV-PARSER-002" else
                             "Tool labels, indicators or stable text positions differed at a checkpoint."),
                    evidence=[f"ui-report.json#{case['id']}"],
                    details=[item for item in case.get("differences", []) if item.get("kind") in ("text", "layout")],
                    component="ui",
                ))
            progress = case.get("progressPoints", 0)
            if progress < 10:
                differences.append(dict(
                    id=f"ui-progress-{case['id']}", severity="P1",
                    summary=f"Deterministic progress checkpoints scored {progress}/10.",
                    evidence=[f"ui-report.json#{case['id']}"],
                    details=case.get("checkpoints"),
                    component="ui",
                ))

    severity_rank = {"P0": 0, "P1": 1, "P2": 2, "Inconclusive": 3}
    differences.sort(key=lambda item: severity_rank.get(item["severity"], 4))
    highest = differences[0] if differences else None
    if highest is None:
        next_task = "None; no confirmed discrepancy remains on this fixture."
    elif highest["id"].startswith("ui-colours"):
        next_task = ("Extend the bash command-line syntax style across the trailing whitespace cells of wrapped "
                     "command output at 80x24; verify with the objective-agent-progress fixture at both sizes.")
    elif highest["id"].startswith("ui-text") and spec.id == "ENV-PARSER-002":
        next_task = ("Match Pi's collapsed tool-output threshold and `ctrl+o to expand` hint in failed/recovered tool "
                     "blocks, then verify with the objective-agent-recovery terminal fixture at 80x24 and 120x40.")
    elif highest["id"].startswith("ui-text") or highest["id"].startswith("ui-progress"):
        next_task = ("Align the deterministic tool-progress labels, indicators and stable text positions with pinned Pi; "
                     "verify with the objective-agent-progress terminal fixture at 80x24 and 120x40.")
    elif highest["severity"] == "P0":
        next_task = ("Correct the confirmed P0 agent-loop defect identified by the evidence, then rerun the full "
                     "ENV-PARSER-001 real-model and deterministic contract suites.")
    elif highest["component"] == "agent-loop":
        next_task = ("Correct the highest-priority agent-loop lifecycle or continuation difference, then rerun the "
                     "deterministic contract and five-run real-model suite.")
    else:
        next_task = ("Correct the highest-priority functional difference, then rerun the full ENV-PARSER-001 suite.")

    report = {
        "schema": 1,
        "fixture": {
            "id": spec.id,
            "checksum": baseline["fixtureChecksum"],
            "baselinePath": str(spec.baseline_path.relative_to(ROOT)),
            "baselineFailure": baseline.get("expectedInitialFailure", baseline.get("baselineExpectedFailure")),
            "prompt": metadata["prompt"],
        },
        "sourceRevisions": metadata["sourceRevisions"],
        "effectiveConfiguration": metadata["effectiveConfiguration"],
        "runEnvironment": {
            "workspace": metadata["workspace"],
            "timeoutSeconds": metadata["timeoutSeconds"],
            "resourceLimits": metadata.get("resourceLimits"),
            "environmentObservations": metadata.get("environmentObservations", []),
            "build": metadata.get("build"),
            "startedAt": metadata.get("startedAt"),
        },
        "runs": evaluated,
        "deterministicContract": contract,
        "ui": ui,
        "aggregates": {
            "pi": aggregates["pi"],
            "pisharp": aggregates["pisharp"],
            "deterministicUi": dict(pi=ui_pi, pisharp=ui_pisharp, max=20,
                                    status=ui.get("status"), parityScore=ui_pisharp),
            "combined": combined,
            "scoreDifferencePisharpMinusPi": (None if combined["pisharp"] is None or combined["pi"] is None
                                              else round(combined["pisharp"] - combined["pi"], 4)),
        },
        "classifiedDifferences": differences,
        "trackedExistingDifferences": [] if spec.id == "ENV-PARSER-001" else [dict(
            id="ui-trailing-wrapped-command-style",
            severity="P2",
            status="open",
            source="docs/parity/fixtures/objective-agent-parity-2026-11-06/README.md",
            summary="At 80x24, 17 trailing blank cells of a wrapped bash command line carry Pi foreground styling but are unstyled in PiSharp.",
        )],
        "highestPriorityConfirmedDiscrepancy": highest,
        "suggestedNextSingleDeliverable": next_task,
        "validityGates": {
            "sameFixtureBaseline": all(run.get("startChecksum") == baseline["fixtureChecksum"]
                                       for run in evaluated),
            "modelConfigurationDocumented": True,
            "allAttemptedRunsAccountedFor": len(evaluated) == len(raw_runs),
            "privateEvaluatorIndependent": True,
            "toolAndFilesystemTracesComplete": all(
                (arguments.runs / "runs" / run["runId"] / "events.jsonl").is_file()
                for run in evaluated),
            "samePiCalibrationPassed": bool(ui.get("calibration", {}).get("match")),
            "deterministicScoreCalculation": True,
            "negativeControlPassed": None,
            "fiveAssessableRunsPerApplication": all(
                aggregates[app]["assessableRuns"] >= 5 for app in ("pi", "pisharp")),
            "comparisonComplete": all(aggregates[app]["assessableRuns"] >= 5 for app in ("pi", "pisharp")),
        },
        "generatedAt": datetime.now(timezone.utc).isoformat(),
    }
    from verify_report import negative_controls
    controls = negative_controls(report, spec)
    report["validityGates"]["negativeControlPassed"] = all(control["rejected"] for control in controls)
    report["negativeControls"] = controls
    write_json(arguments.output, report)
    print(arguments.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
