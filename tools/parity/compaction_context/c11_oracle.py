#!/usr/bin/env python3
"""Run paired pinned-Pi/PiSharp C11 CLI, protocol, TUI, and provider observations."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
from typing import Any

import c5_oracle
import verify

HERE = Path(__file__).resolve().parent
PI_ORACLE = HERE / "pi_c11_oracle.mjs"


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _run_pi(pi_path: Path, evidence_dir: Path) -> tuple[dict[str, Any], list[Path]]:
    runs: list[dict[str, Any]] = []
    files: list[Path] = []
    for run_number in (1, 2):
        output = evidence_dir / f"pi-c11-run-{run_number}.json"
        stdout = evidence_dir / f"pi-c11-run-{run_number}.stdout.log"
        stderr = evidence_dir / f"pi-c11-run-{run_number}.stderr.log"
        command = ["node", str(PI_ORACLE), "--pi", str(pi_path), "--output", str(output)]
        process = subprocess.run(command, cwd=pi_path, capture_output=True, text=True, check=False)
        stdout.write_text(process.stdout, encoding="utf-8")
        stderr.write_text(process.stderr, encoding="utf-8")
        if process.returncode:
            raise RuntimeError(f"pinned Pi C11 oracle run {run_number} failed:\n{process.stderr}\n{process.stdout}")
        runs.append(_json(output))
        files.extend((output, stdout, stderr))
    if runs[0] != runs[1]:
        raise RuntimeError("pinned Pi C11 oracle was not stable across two executions")
    return runs[0], files


def _paired(pi: dict[str, Any], pisharp: dict[str, Any], case_ids: tuple[str, ...]) -> tuple[
        dict[str, Any], dict[str, Any]]:
    if set(pi) != set(case_ids) or set(pisharp) != set(case_ids):
        raise RuntimeError("C11 oracle did not produce exactly the frozen case IDs")
    for case_id in case_ids:
        if pi[case_id] != pisharp[case_id]:
            raise RuntimeError(f"C11 paired observations differ for {case_id}:\n" + json.dumps({
                "expected": pi[case_id], "actual": pisharp[case_id],
            }, indent=2, ensure_ascii=False))
    return ({case_id: pi[case_id] for case_id in case_ids},
            {case_id: pisharp[case_id] for case_id in case_ids})


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    contract = verify.load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == "C11")
    case_ids = tuple(case["id"] for case in checkpoint["requiredCases"])

    pi_run, pi_files = _run_pi(pi_path, evidence_dir)
    pisharp_run, pisharp_files = c5_oracle.run_pisharp(evidence_dir, "c11")
    pi_observations, pisharp_observations = _paired(
        pi_run["cases"], pisharp_run["cases"], case_ids)

    pi_path_out = evidence_dir / "pi-c11-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c11-observations.json"
    pi_path_out.write_text(json.dumps({"observations": pi_observations}, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8")
    pisharp_path_out.write_text(
        json.dumps({"observations": pisharp_observations}, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8")

    raw = [c5_oracle.artifact(path, evidence_dir)
           for path in [*pi_files, *pisharp_files, pi_path_out, pisharp_path_out]]
    expected_contract = {case["id"]: case["expectedObservation"] for case in checkpoint["requiredCases"]}
    cases = []
    for case_id in case_ids:
        pointer = f"/observations/{case_id}"
        observation = pi_observations[case_id]
        cases.append({
            "caseId": case_id,
            "checkpointId": "C11",
            "contractExpectedObservation": expected_contract[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "equivalent",
            "assertions": [{
                "name": "paired-cli-protocol-tui-and-provider-observation",
                "caseId": case_id,
                "contractObservation": expected_contract[case_id],
                "comparison": "equivalent",
                "baselineArtifact": pi_path_out.name,
                "baselinePointer": pointer,
                "implementationArtifact": pisharp_path_out.name,
                "implementationPointer": pointer,
                "expected": observation,
                "actual": pisharp_observations[case_id],
                "normalizedExpected": observation,
                "normalizedActual": pisharp_observations[case_id],
                "passed": True,
            }],
            "rawArtifacts": raw,
        })

    counts = verify.validate_case_results(cases, "C11", evidence_dir, verify_artifacts=True)
    source_clean = c5_oracle.source_tree_is_clean()
    report = {
        "checkpointId": "C11",
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.implementation_source_sha() if source_clean else None,
        "sourceTreeClean": source_clean,
        "cases": cases,
        "checkpointResults": [{
            "checkpointId": "C11",
            "requiredCaseCount": counts["required"],
            "executedCaseCount": counts["executed"],
            "failedCaseCount": counts["failed"],
            "skippedCaseCount": counts["skipped"],
            "status": "passed" if counts["passed"] == counts["required"] else "incomplete",
        }],
        "verifierCounts": counts,
    }
    (evidence_dir / "c11-case-results.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        report = run(args.pi.resolve(), args.evidence_dir.resolve())
    except (OSError, RuntimeError, ValueError) as error:
        print(f"C11 differential failed: {error}")
        return 1
    counts = report["verifierCounts"]
    clean = "clean source" if report["sourceTreeClean"] else "source is uncommitted"
    print(f"Validated {counts['passed']}/{counts['required']} C11 paired cases; {clean}.")
    return 0 if counts["passed"] == counts["required"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
