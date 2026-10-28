#!/usr/bin/env python3
"""Assemble every checkpoint report into one contract-derived evidence file."""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

import verify

ROOT = Path(__file__).resolve().parents[3]
FINAL_SOURCE_CHECKPOINTS = {
    *(f"C{number}" for number in range(1, 14)), "D0", "Q0", "E0", "CI0", "PUB0",
}


def _read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise verify.VerificationError(f"cannot read checkpoint evidence {path}: {error}") from error


def _case_list(value: Any, checkpoint_id: str) -> list[dict[str, Any]]:
    cases = value if isinstance(value, list) else value.get("cases") if isinstance(value, dict) else None
    if not isinstance(cases, list):
        raise verify.VerificationError(f"{checkpoint_id} case-results file has no case list")
    return cases


def _qualify_case_paths(case: dict[str, Any], report_directory: str) -> dict[str, Any]:
    result = json.loads(json.dumps(case))
    for artifact in result.get("rawArtifacts", []):
        artifact["path"] = f"{report_directory}/{artifact['path']}"
    for assertion in result.get("assertions", []):
        for key in ("baselineArtifact", "implementationArtifact", "safetyEvidence"):
            if isinstance(assertion.get(key), str):
                assertion[key] = f"{report_directory}/{assertion[key]}"
    return result


def aggregate(evidence_directory: Path) -> dict[str, Any]:
    contract = verify.load_contract()
    evidence_directory = evidence_directory.resolve()
    all_cases: list[dict[str, Any]] = []
    source_shas: set[str] = set()
    checkpoint_results: list[dict[str, Any]] = []

    for checkpoint in contract["checkpoints"]:
        checkpoint_id = checkpoint["id"]
        run_directory = evidence_directory / "runs" / checkpoint_id
        run_path = run_directory / "checkpoint-run.json"
        if not run_path.is_file():
            raise verify.VerificationError(f"missing checkpoint run record: {run_path.relative_to(evidence_directory)}")
        run = _read_json(run_path)
        if run.get("checkpointId") != checkpoint_id or run.get("status") != "passed":
            raise verify.VerificationError(f"checkpoint {checkpoint_id} has no passing run record")
        case_results_path = run.get("caseResultsPath")
        if not isinstance(case_results_path, str):
            case_results_path = f"runs/{checkpoint_id}/case-results.json"
        relative_result_path = Path(case_results_path)
        if relative_result_path.is_absolute() or ".." in relative_result_path.parts:
            raise verify.VerificationError(
                f"checkpoint {checkpoint_id} case-results path escapes evidence directory")
        result_path = (evidence_directory / relative_result_path
                       if relative_result_path.parts and relative_result_path.parts[0] == "runs"
                       else run_directory / relative_result_path)
        case_values = _read_json(result_path)
        cases = _case_list(case_values, checkpoint_id)
        report_directory = result_path.parent.relative_to(evidence_directory).as_posix()
        # Validate in the directory relative to which the oracle emitted its artifact paths.
        verify.validate_case_results(cases, checkpoint_id, result_path.parent, verify_artifacts=True)
        all_cases.extend(_qualify_case_paths(item, report_directory) for item in cases)

        required = len(checkpoint["requiredCases"])
        if len(cases) != required:
            raise verify.VerificationError(
                f"checkpoint {checkpoint_id} produced {len(cases)}/{required} required cases")
        passed = sum(item.get("passed") is True for item in cases)
        executed = sum(item.get("executed") is True for item in cases)
        failed = sum(item.get("passed") is not True for item in cases)
        skipped = sum(item.get("skipped") is True for item in cases)
        checkpoint_results.append({
            "checkpointId": checkpoint_id,
            "requiredCaseCount": required,
            "executedCaseCount": executed,
            "failedCaseCount": failed,
            "skippedCaseCount": skipped,
            "status": "passed" if passed == required and executed == required and failed == 0 and skipped == 0 else "incomplete",
        })
        if checkpoint_id in FINAL_SOURCE_CHECKPOINTS:
            source_sha = run.get("pisharpSourceSha")
            if not isinstance(source_sha, str) or len(source_sha) != 40:
                raise verify.VerificationError(f"checkpoint {checkpoint_id} has no full clean-source SHA")
            source_shas.add(source_sha)

    if len(source_shas) != 1:
        raise verify.VerificationError(f"final checkpoint source SHAs differ: {sorted(source_shas)}")

    evidence = {
        "provenance": {
            "contractSha256": verify.FROZEN_CONTRACT_SHA256,
            "piSha": verify.PINNED_PI_SHA,
            "pisharpSourceSha": next(iter(source_shas)),
        },
        "checkpointResults": checkpoint_results,
        "cases": all_cases,
        "executionExitCode": 0,
    }
    verify.validate_evidence(evidence, evidence_directory, verify_artifacts=True, verify_source=True)
    (evidence_directory / "evidence.json").write_text(
        json.dumps(evidence, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return evidence


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-dir", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        evidence = aggregate(args.evidence_dir)
    except (OSError, verify.VerificationError, subprocess.CalledProcessError) as error:
        print(f"evidence aggregation failed: {error}", file=sys.stderr)
        return 1
    print(f"Aggregated {len(evidence['checkpointResults'])}/22 checkpoints and "
          f"{len(evidence['cases'])}/211 cases at source "
          f"{evidence['provenance']['pisharpSourceSha']}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
