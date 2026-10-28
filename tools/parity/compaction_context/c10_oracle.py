#!/usr/bin/env python3
"""Run paired pinned-Pi usage accounting and projection observations."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys
from typing import Any

import c5_oracle
import verify

HERE = Path(__file__).resolve().parent
PI_ORACLE = HERE / "pi_c10_oracle.mjs"
PI_TESTS = (
    "packages/coding-agent/test/agent-session-stats.test.ts",
    "packages/coding-agent/test/compaction.test.ts",
)
CASE_IDS = (
    "C10.summary-request-usage-persists-on-active-branch",
    "C10.fallback-context-total-includes-cache-tokens",
    "C10.input-output-cache-read-cache-write-remain-distinct",
    "C10.reasoning-tokens-are-preserved",
    "C10.missing-usage-fields-remain-unknown",
    "C10.unknown-prices-do-not-create-cost",
    "C10.known-pricing-and-input-tiers-are-calculated",
    "C10.split-summary-physical-model-costs-aggregate-correctly",
    "C10.inactive-branch-usage-is-excluded-from-active-total",
    "C10.cli-tui-and-rpc-report-canonical-session-usage",
)
SAFETY_CASES = {
    "C10.missing-usage-fields-remain-unknown",
    "C10.unknown-prices-do-not-create-cost",
}


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _run_pi(pi_path: Path, evidence_dir: Path) -> tuple[dict[str, Any], list[Path]]:
    runs: list[dict[str, Any]] = []
    files: list[Path] = []
    for run_number in (1, 2):
        output = evidence_dir / f"pi-c10-run-{run_number}.json"
        stdout = evidence_dir / f"pi-c10-run-{run_number}.stdout.log"
        stderr = evidence_dir / f"pi-c10-run-{run_number}.stderr.log"
        command = ["node", str(PI_ORACLE), "--pi", str(pi_path), "--output", str(output)]
        process = subprocess.run(command, cwd=pi_path, capture_output=True, text=True, check=False)
        stdout.write_text(process.stdout, encoding="utf-8")
        stderr.write_text(process.stderr, encoding="utf-8")
        if process.returncode:
            raise RuntimeError(f"pinned Pi C10 oracle run {run_number} failed: {process.stderr}\n{process.stdout}")
        runs.append(_json(output))
        files.extend((output, stdout, stderr))
    if runs[0] != runs[1]:
        raise RuntimeError("pinned Pi C10 usage oracle was not stable across two executions")

    command = ["node", str(pi_path / "node_modules/vitest/vitest.mjs"), "--run", *PI_TESTS]
    process = subprocess.run(command, cwd=pi_path, capture_output=True, text=True, check=False)
    stdout = evidence_dir / "pinned-pi-c10-tests.stdout.log"
    stderr = evidence_dir / "pinned-pi-c10-tests.stderr.log"
    stdout.write_text(process.stdout, encoding="utf-8")
    stderr.write_text(process.stderr, encoding="utf-8")
    if process.returncode or "Tests  35 passed | 2 skipped (37)" not in process.stdout:
        raise RuntimeError(f"pinned Pi C10 unit suites failed or had an unexpected count:\n{process.stderr}\n{process.stdout}")
    files.extend((stdout, stderr))
    return runs[0], files


def _normalize(case_id: str, value: dict[str, Any]) -> dict[str, Any]:
    return value


def _paired(pi: dict[str, Any], pisharp: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any]]:
    if set(pi) != set(CASE_IDS) or set(pisharp) != set(CASE_IDS):
        raise RuntimeError("C10 oracle did not produce exactly the frozen case IDs")
    baseline: dict[str, Any] = {}
    implementation: dict[str, Any] = {}
    for case_id in CASE_IDS:
        expected = _normalize(case_id, pi[case_id])
        actual = _normalize(case_id, pisharp[case_id])
        if case_id not in SAFETY_CASES and expected != actual:
            raise RuntimeError(f"C10 paired observations differ for {case_id}:\n" + json.dumps({
                "expected": expected, "actual": actual,
            }, indent=2, ensure_ascii=False))
        if case_id == "C10.missing-usage-fields-remain-unknown" and (
                any(expected.get(key) is not False for key in (
                    "inputIsUnknown", "outputIsUnknown", "cacheReadIsUnknown", "cacheWriteIsUnknown", "totalIsUnknown")) or
                expected.get("reasoningIsUnknown") is not True or expected.get("inputTotalIsKnown") is not False or
                any(actual.get(key) is not True for key in (
                    "inputIsUnknown", "outputIsUnknown", "cacheReadIsUnknown", "cacheWriteIsUnknown", "totalIsUnknown",
                    "reasoningIsUnknown", "inputTotalIsKnown"))):
            raise RuntimeError(f"C10 missing-usage safety behavior changed: {expected} / {actual}")
        if case_id == "C10.unknown-prices-do-not-create-cost" and (
                expected.get("cost") != 0 or actual.get("cost") is not None):
            raise RuntimeError(f"C10 unknown-price safety behavior changed: {expected} / {actual}")
        baseline[case_id] = expected
        implementation[case_id] = actual
    return baseline, implementation


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    pi_run, pi_files = _run_pi(pi_path, evidence_dir)
    pisharp_run, pisharp_files = c5_oracle.run_pisharp(evidence_dir, "c10")
    pi_values = pi_run["cases"]
    pisharp_values = pisharp_run["cases"]
    pi_observations, pisharp_observations = _paired(pi_values, pisharp_values)

    pi_path_out = evidence_dir / "pi-c10-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c10-observations.json"
    pi_path_out.write_text(json.dumps({"observations": pi_observations}, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8")
    pisharp_path_out.write_text(json.dumps({"observations": pisharp_observations}, indent=2, ensure_ascii=False) + "\n",
                                encoding="utf-8")
    contract = verify.load_contract()
    expected_contract = {case["id"]: case["expectedObservation"] for checkpoint in contract["checkpoints"]
                         if checkpoint["id"] == "C10" for case in checkpoint["requiredCases"]}
    raw_paths = [*pi_files, *pisharp_files, pi_path_out, pisharp_path_out]
    raw = [c5_oracle.artifact(path, evidence_dir) for path in raw_paths]
    cases = []
    safety_differences = []
    for case_id in CASE_IDS:
        pointer = f"/observations/{case_id}"
        expected = pi_observations[case_id]
        actual = pisharp_observations[case_id]
        safety = case_id in SAFETY_CASES
        assertion = {
            "name": "paired-usage-accounting-and-session-projection-observation",
            "caseId": case_id,
            "contractObservation": expected_contract[case_id],
            "comparison": "intentional-safety-difference" if safety else "equivalent",
            "baselineArtifact": pi_path_out.name,
            "baselinePointer": pointer,
            "implementationArtifact": pisharp_path_out.name,
            "implementationPointer": pointer,
            "expected": expected,
            "actual": actual,
            "normalizedExpected": expected,
            "normalizedActual": actual,
            "passed": True,
        }
        if safety:
            assertion.update({
                "safetyEvidence": pisharp_path_out.name,
                "safetyRegression": ("CompactionTests.UsageRecordCreatePreservesMissingUsageFieldsAsUnknown" if
                                     case_id == "C10.missing-usage-fields-remain-unknown" else
                                     "CompactionTests.UsageRecordCreateLeavesCostUnknownWithoutModelPricing"),
            })
            if case_id == "C10.missing-usage-fields-remain-unknown":
                reason = "Pi's Usage contract requires numeric input/output/cache counts; MAF marks absent counts as unknown. PiSharp preserves those unknowns and keeps the supplied combined input total separately."
                regression = "CompactionTests.UsageRecordCreatePreservesMissingUsageFieldsAsUnknown"
            else:
                reason = "Pi's required numeric pricing rates represent this zero-rate fixture as a free model; PiSharp preserves absent pricing as unknown so a missing price is not reported as free."
                regression = "CompactionTests.UsageRecordCreateLeavesCostUnknownWithoutModelPricing"
            safety_differences.append({"caseId": case_id, "reason": reason,
                                       "regression": regression, "evidence": pisharp_path_out.name})
        cases.append({
            "caseId": case_id,
            "checkpointId": "C10",
            "contractExpectedObservation": expected_contract[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "intentional-safety-difference" if safety else "equivalent",
            "assertions": [assertion],
            "rawArtifacts": raw,
        })

    counts = verify.validate_case_results(cases, "C10", evidence_dir, verify_artifacts=True)
    report = {
        "checkpointId": "C10",
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.implementation_source_sha() if c5_oracle.source_tree_is_clean() else None,
        "piCalibrationStable": True,
        "pisharpCalibrationStable": True,
        "cases": cases,
        "checkpointResults": [{
            "checkpointId": "C10",
            "requiredCaseCount": counts["required"],
            "executedCaseCount": counts["executed"],
            "failedCaseCount": counts["failed"],
            "skippedCaseCount": counts["skipped"],
            "status": "passed" if counts["passed"] == counts["required"] else "incomplete",
        }],
        "verifierCounts": counts,
        "intentionalSafetyDifferences": safety_differences,
        "pinnedPiUnitTests": {"passed": 35, "skipped": 2, "failed": 0},
    }
    (evidence_dir / "c10-case-results.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n",
                                                           encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        report = run(args.pi.resolve(), args.evidence_dir.resolve())
    except (verify.VerificationError, OSError, RuntimeError, KeyError, TypeError,
            subprocess.CalledProcessError) as error:
        print(f"C10 oracle failed: {error}", file=sys.stderr)
        return 1
    counts = report["verifierCounts"]
    print(f"C10 paired cases: {counts['passed']}/{counts['required']}; 0 failed, 0 skipped")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
