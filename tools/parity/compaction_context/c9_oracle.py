#!/usr/bin/env python3
"""Run paired pinned-Pi and PiSharp branch-summary/navigation observations."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys
from typing import Any

import c5_oracle
import verify

CASE_IDS = (
    "C9.branch-diff-uses-common-ancestor-and-old-path",
    "C9.branch-entries-are-chronological-and-no-duplicate-tool-results",
    "C9.token-budget-retains-recent-summary-context",
    "C9.nested-branch-file-operation-details-accumulate",
    "C9.custom-instructions-append-or-replace-default",
    "C9.summary-output-has-preamble-and-file-details",
    "C9.summary-applies-at-navigation-target-with-label",
    "C9.cancel-or-failure-keeps-selected-head-and-history",
    "C9.public-navigation-prompts-and-skips-as-pinned",
)
PINNED_PI_UI_TEST = "packages/coding-agent/test/interactive-mode-tree-navigation.test.ts"


def _run_pinned_pi_ui_test(pi_path: Path, evidence_dir: Path) -> tuple[dict[str, Any], list[Path]]:
    command = ["node", str(pi_path / "node_modules/vitest/vitest.mjs"), "--run", PINNED_PI_UI_TEST]
    process = subprocess.run(command, cwd=pi_path, capture_output=True, text=True, check=False)
    stdout = evidence_dir / "pinned-pi-ui-test.stdout.log"
    stderr = evidence_dir / "pinned-pi-ui-test.stderr.log"
    stdout.write_text(process.stdout, encoding="utf-8")
    stderr.write_text(process.stderr, encoding="utf-8")
    if process.returncode:
        raise RuntimeError(f"pinned Pi public tree-navigation UI tests failed: {process.stderr}\n{process.stdout}")
    if "Tests  5 passed (5)" not in process.stdout:
        raise RuntimeError("pinned Pi UI test did not report its five expected tests")
    return {"command": command, "passed": 5, "failed": 0}, [stdout, stderr]


def normalize(case_id: str, value: dict[str, Any], side: str) -> dict[str, Any]:
    if case_id == CASE_IDS[0]:
        return {
            "commonAncestorContainsSharedMarker": value["commonAncestorContainsSharedMarker"],
            "oldPathEntryCount": value["oldPathEntryCount"],
            "targetBranchEntriesExcluded": value["targetBranchEntriesExcluded"],
            "summaryFromIdIsOldHead": value.get("summaryFromIdIsOldHead", value.get("summaryFromOldHead", False)),
        }
    if case_id == CASE_IDS[1]:
        return {key: value[key] for key in ("markerOrder", "toolResultDuplicated", "targetBranchIncluded")}
    if case_id == CASE_IDS[2]:
        return {key: value[key] for key in (
            "retainsRecentMessage", "retainsImportantSummary", "dropsOlderMessage")}
    if case_id == CASE_IDS[3]:
        return {key: value[key] for key in ("readFiles", "modifiedFiles", "promptIncludesNestedSummary")}
    if case_id == CASE_IDS[4]:
        return {key: value[key] for key in (
            "appendHasDefaultInstructions", "appendHasCustomInstructions",
            "replaceHasCustomInstructions", "replaceHasDefaultInstructions")}
    if case_id == CASE_IDS[5]:
        return {key: value[key] for key in (
            "summaryStartsWithPreamble", "summaryIncludesGeneratedText",
            "summaryIncludesReadFileDetails", "detailsReadFiles")}
    if case_id == CASE_IDS[6]:
        return {key: value[key] for key in ("parentIsSelectedTarget", "fromIdIsAbandonedHead", "label")}
    if case_id == CASE_IDS[7]:
        return {key: value[key] for key in (
            "failedUnchanged", "cancelledUnchanged", "cancelled")}
    if case_id == CASE_IDS[8]:
        return {key: value[key] for key in (
            "defaultReserveTokens", "defaultPrompts", "skipPromptOverrideSuppressesPrompt",
            "promptTitle", "choices", "defaultChoice")}
    raise ValueError(f"no C9 normalization for {case_id} ({side})")


def validate(case_id: str, pi: dict[str, Any], pisharp: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any]]:
    expected = normalize(case_id, pi, "pi")
    actual = normalize(case_id, pisharp, "pisharp")
    if expected != actual:
        raise RuntimeError(f"C9 paired observations differ for {case_id}:\n" + json.dumps({
            "expected": expected, "actual": actual,
        }, indent=2, ensure_ascii=False))
    return expected, actual


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    pi_run, pi_files = c5_oracle.run_pi(pi_path, evidence_dir, "c9")
    pisharp_run, pisharp_files = c5_oracle.run_pisharp(evidence_dir, "c9")
    pi_ui_test, pi_ui_files = _run_pinned_pi_ui_test(pi_path, evidence_dir)
    pi_values = pi_run["cases"]
    pisharp_values = pisharp_run["cases"]
    if set(pi_values) != set(CASE_IDS) or set(pisharp_values) != set(CASE_IDS):
        raise RuntimeError("C9 oracle did not produce exactly the frozen case IDs")

    contract = verify.load_contract()
    expected_contract = {case["id"]: case["expectedObservation"] for checkpoint in contract["checkpoints"]
                         if checkpoint["id"] == "C9" for case in checkpoint["requiredCases"]}
    pi_path_out = evidence_dir / "pi-c9-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c9-observations.json"
    pi_normalized: dict[str, Any] = {}
    pisharp_normalized: dict[str, Any] = {}
    for case_id in CASE_IDS:
        expected, actual = validate(case_id, pi_values[case_id], pisharp_values[case_id])
        pi_normalized[case_id] = expected
        pisharp_normalized[case_id] = actual

    pi_path_out.write_text(json.dumps({"observations": pi_normalized}, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8")
    pisharp_path_out.write_text(json.dumps({"observations": pisharp_normalized}, indent=2, ensure_ascii=False) + "\n",
                                encoding="utf-8")
    raw = [*pi_files, *pisharp_files, *pi_ui_files, pi_path_out, pisharp_path_out]
    cases = []
    for case_id in CASE_IDS:
        pointer = f"/observations/{case_id}"
        observation = pi_normalized[case_id]
        cases.append({
            "caseId": case_id,
            "checkpointId": "C9",
            "contractExpectedObservation": expected_contract[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "equivalent",
            "assertions": [{
                "name": "paired-branch-summary-and-navigation-observation",
                "caseId": case_id,
                "contractObservation": expected_contract[case_id],
                "comparison": "equivalent",
                "baselineArtifact": pi_path_out.name,
                "baselinePointer": pointer,
                "implementationArtifact": pisharp_path_out.name,
                "implementationPointer": pointer,
                "expected": observation,
                "actual": pisharp_normalized[case_id],
                "normalizedExpected": observation,
                "normalizedActual": pisharp_normalized[case_id],
                "passed": True,
            }],
            "rawArtifacts": [c5_oracle.artifact(path, evidence_dir) for path in raw],
        })
    counts = verify.validate_case_results(cases, "C9", evidence_dir, verify_artifacts=True)
    report = {
        "checkpointId": "C9",
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.implementation_source_sha() if c5_oracle.source_tree_is_clean() else None,
        "cases": cases,
        "checkpointResults": [{
            "checkpointId": "C9",
            "requiredCaseCount": counts["required"],
            "executedCaseCount": counts["executed"],
            "failedCaseCount": counts["failed"],
            "skippedCaseCount": counts["skipped"],
            "status": "passed" if counts["passed"] == counts["required"] else "incomplete",
        }],
        "verifierCounts": counts,
        "pinnedPiUiTest": pi_ui_test,
    }
    (evidence_dir / "c9-case-results.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n",
                                                           encoding="utf-8")
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        report = run(args.pi.resolve(), args.evidence_dir.resolve())
        counts = report["verifierCounts"]
        print(f"C9 paired cases: {counts['passed']}/{counts['required']}; 0 failed, 0 skipped")
        return 0
    except (verify.VerificationError, OSError, RuntimeError, KeyError, TypeError, ValueError,
            subprocess.CalledProcessError) as error:
        print(f"C9 oracle failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
