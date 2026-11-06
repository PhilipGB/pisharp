#!/usr/bin/env python3
"""Publish compact, credential-free evidence from an evaluated run directory."""
from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path

from common import load_baseline, write_json

ROOT = Path(__file__).resolve().parents[3]


def compact_report(report: dict) -> dict:
    result = copy.deepcopy(report)
    for run in result.get("runs", []):
        evaluation = run.get("evaluation")
        if not isinstance(evaluation, dict):
            continue
        for key in ("privateTests", "visibleTests"):
            section = evaluation.get(key) or {}
            section.pop("buildOutput", None)
            section.pop("tests", None)
        for key in ("sourceInvestigation", "fileModification", "testExecution",
                    "toolLifecycle", "modelContinuation", "completion"):
            section = evaluation.get("agentLoop", {}).get(key)
            if isinstance(section, dict):
                section.pop("finalResponse", None)
    return result


def compact_ui(ui: dict) -> dict:
    result = copy.deepcopy(ui)
    for case in result.get("cases", []):
        for difference in case.get("differences", []):
            difference.pop("pi", None)
            difference.pop("pisharp", None)
    return result


def compact_contract(contract: dict) -> dict:
    result = copy.deepcopy(contract)
    for product in result.get("products", []):
        for execution in product.get("toolLifecycle", {}).get("executions", []):
            execution["result"] = execution.get("result", "")[:120]
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--ui", type=Path, required=True)
    parser.add_argument("--contract", type=Path, required=True)
    parser.add_argument("--verification", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    arguments.output.mkdir(parents=True, exist_ok=True)
    report = json.loads(arguments.report.read_text())
    write_json(arguments.output / "report.json", compact_report(report))
    write_json(arguments.output / "ui-report.json", compact_ui(json.loads(arguments.ui.read_text())))
    write_json(arguments.output / "contract-report.json",
               compact_contract(json.loads(arguments.contract.read_text())))
    write_json(arguments.output / "verification.json", json.loads(arguments.verification.read_text()))
    write_json(arguments.output / "fixture-baseline.json", load_baseline())
    write_json(arguments.output / "evidence-index.json", dict(
        fixtureId=report["fixture"]["id"],
        fixtureChecksum=report["fixture"]["checksum"],
        rawArtifacts="artifacts/parity/objective-agent-parity-20261106",
        deterministicUi="ui-report.json",
        deterministicContract="contract-report.json",
        realModelReport="report.json",
        verification="verification.json",
    ))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
