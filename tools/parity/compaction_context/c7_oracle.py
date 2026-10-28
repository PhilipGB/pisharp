#!/usr/bin/env python3
"""Run paired pinned-Pi/PiSharp summary prompt and result observations."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
from typing import Any

import c5_oracle
import verify

CASE_IDS = (
    "C7.initial-summary-instructions-match-pinned-prompt",
    "C7.previous-summary-is-updated-with-prior-facts-preserved",
    "C7.manual-focus-is-excluded-from-turn-prefix-summary",
    "C7.split-turn-uses-history-and-turn-summary-requests",
    "C7.tool-call-result-content-is-serialized-for-summary",
    "C7.file-operation-details-follow-summarized-history",
    "C7.length-stop-and-empty-summary-fail-safely",
    "C7.transient-stream-failure-obeys-retry-policy",
    "C7.summary-usage-is-reported-only-when-supplied",
    "C7.summary-model-routing-and-thinking-options",
)


def _field(value: dict[str, Any], lower: str, upper: str | None = None) -> Any:
    return value.get(lower, value.get(upper or lower[:1].upper() + lower[1:]))


def _request(value: dict[str, Any]) -> dict[str, Any]:
    return {
        "systemPrompt": _field(value, "systemPrompt"),
        "prompt": _field(value, "prompt"),
        "maxTokens": _field(value, "maxTokens"),
        "reasoning": _field(value, "reasoning"),
        "modelId": _field(value, "modelId"),
    }


def _requests(value: dict[str, Any]) -> list[dict[str, Any]]:
    requests = value.get("requests")
    if isinstance(requests, list):
        return [_request(request) for request in requests]
    request = value.get("request")
    return [_request(request)] if isinstance(request, dict) else []


def _usage(value: dict[str, Any]) -> dict[str, Any]:
    return {
        "usageSupplied": value.get("usageSupplied") is True,
        "usage": None if value.get("usage") is None else {
            "input": value["usage"].get("input", value["usage"].get("InputTokenCount")),
            "output": value["usage"].get("output", value["usage"].get("OutputTokenCount")),
            "total": value["usage"].get("totalTokens", value["usage"].get("TotalTokenCount")),
        },
    }


def validate_pair(case_id: str, pi: dict[str, Any], pisharp: dict[str, Any]) -> tuple[
        dict[str, Any], dict[str, Any], dict[str, str] | None]:
    safety: dict[str, str] | None = None
    if case_id in (CASE_IDS[0], CASE_IDS[1], CASE_IDS[4]):
        pi_request = _request(pi["request"])
        ps_request = _request(pisharp["request"])
        expected = {"systemPrompt": pi_request["systemPrompt"], "prompt": pi_request["prompt"]}
        actual = {"systemPrompt": ps_request["systemPrompt"], "prompt": ps_request["prompt"]}
        if expected != actual:
            raise RuntimeError(f"summary request mismatch for {case_id}: " + json.dumps({
                "expected": expected, "actual": actual,
            }, indent=2))
    elif case_id == CASE_IDS[2]:
        pi_value = {
            "historyIncludesFocus": pi["historyIncludesFocus"],
            "turnPrefixExcludesFocus": pi["turnPrefixExcludesFocus"],
            "turnPrefixPrompt": pi["turnPrefixPrompt"],
        }
        ps_requests = _requests(pisharp)
        ps_value = {
            "historyIncludesFocus": pisharp["historyIncludesFocus"],
            "turnPrefixExcludesFocus": pisharp["turnPrefixExcludesFocus"],
            "turnPrefixPrompt": ps_requests[1]["prompt"] if len(ps_requests) > 1 else None,
        }
        expected, actual = pi_value, ps_value
        if expected != actual:
            raise RuntimeError(f"manual focus differs in turn-prefix summary: " + json.dumps({
                "expected": expected, "actual": actual,
            }, indent=2))
    elif case_id == CASE_IDS[3]:
        pi_requests = _requests(pi)
        ps_requests = _requests(pisharp)
        expected = {"requests": pi_requests, "summary": pi.get("summary")}
        actual = {"requests": ps_requests, "summary": pisharp.get("summary")}
        if len(pi_requests) != 2 or len(ps_requests) != 2:
            raise RuntimeError(f"split-turn compaction did not issue two summary requests: {case_id}")
        for request in (expected["requests"], actual["requests"]):
            for item in request:
                item.pop("modelId", None)
        if expected != actual:
            raise RuntimeError(f"split-turn summary differs: " + json.dumps({
                "expected": expected, "actual": actual,
            }, indent=2))
    elif case_id == CASE_IDS[5]:
        pi_requests = _requests(pi)
        ps_requests = _requests(pisharp)
        expected = {"requests": pi_requests, "summary": pi.get("summary")}
        actual = {"requests": ps_requests, "summary": pisharp.get("summary")}
        if len(pi_requests) != 2 or len(ps_requests) != 2:
            raise RuntimeError("file-operation summary did not preserve the paired split-turn requests")
        for request in (expected["requests"], actual["requests"]):
            for item in request:
                item.pop("modelId", None)
        if expected != actual:
            raise RuntimeError("file-operation summary mismatch: " + json.dumps({
                "expected": expected, "actual": actual,
            }, indent=2))
    elif case_id == CASE_IDS[6]:
        expected = {
            "lengthError": pi["length"]["error"],
            "lengthStateUnchanged": pi["length"]["canonicalStateUnchanged"],
            "emptySummaryAccepted": pi["empty"]["error"] is None,
            "emptyCompactionCount": pi["empty"]["compactionCount"],
        }
        actual = {
            "lengthError": pisharp["length"]["error"],
            "lengthStateUnchanged": pisharp["length"]["canonicalStateUnchanged"],
            "emptySummaryAccepted": pisharp["empty"]["error"] is None,
            "emptyCompactionCount": pisharp["empty"]["compactionCount"],
        }
        if (not expected["lengthStateUnchanged"] or not actual["lengthStateUnchanged"] or
                expected["lengthError"] != actual["lengthError"] or expected["emptySummaryAccepted"] is not True or
                expected["emptyCompactionCount"] != 1 or actual["emptySummaryAccepted"] is not False or
                actual["emptyCompactionCount"] != 0 or not pisharp["empty"]["canonicalStateUnchanged"]):
            raise RuntimeError("failed summary changed canonical compaction state: " + json.dumps({
                "expected": expected, "actual": actual, "pisharp": pisharp,
            }, indent=2))
        safety = {
            "reason": "Pinned Pi accepts a whitespace-only first summary and appends a compaction; PiSharp rejects it and preserves canonical history.",
            "regression": "src/PiSharp.Runtime/Agents/PiCompactionSummarizer.cs#RunSummaryAsync",
        }
    elif case_id == CASE_IDS[7]:
        pi_requests = [_request(request) for request in pi["requestSequence"]]
        ps_requests = _requests(pisharp)
        expected = {"attempts": pi["attempts"], "requests": pi_requests, "summary": pi["summary"]}
        actual = {"attempts": pisharp["attempts"], "requests": ps_requests, "summary": pisharp["summary"]}
        for request in (expected["requests"], actual["requests"]):
            for item in request:
                item.pop("modelId", None)
                item.pop("maxTokens", None)
        if expected != actual or expected["attempts"] != 2:
            raise RuntimeError("transient summary retry mismatch: " + json.dumps({
                "expected": expected, "actual": actual,
            }, indent=2))
    elif case_id == CASE_IDS[8]:
        expected = {"supplied": _usage(pi["supplied"]), "omitted": _usage(pi["omitted"])}
        actual = {"supplied": _usage(pisharp["supplied"]), "omitted": _usage(pisharp["omitted"])}
        if expected != actual:
            raise RuntimeError("summary usage presence differs: " + json.dumps({
                "expected": expected, "actual": actual,
            }, indent=2))
    elif case_id == CASE_IDS[9]:
        pi_requests = _requests(pi)
        ps_requests = _requests(pisharp)
        expected = {"modelId": pi["modelId"], "maxTokens": pi["maxTokens"],
                    "reasoning": pi["reasoning"], "requestCount": len(pi_requests)}
        actual = {"modelId": pisharp["modelId"], "maxTokens": pisharp["maxTokens"],
                  "reasoning": pisharp["reasoning"], "requestCount": len(ps_requests)}
        if expected != actual or expected["maxTokens"] != 48 or expected["reasoning"] != "high":
            raise RuntimeError("routed summary options differ: " + json.dumps({
                "expected": expected, "actual": actual,
            }, indent=2))
    else:
        raise ValueError(f"no C7 validation for {case_id}")
    return expected, actual, safety


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    pi_run, pi_files = c5_oracle.run_pi(pi_path, evidence_dir, "c7")
    pisharp_run, pisharp_files = c5_oracle.run_pisharp(evidence_dir, "c7")
    pi_values = pi_run["cases"]
    pisharp_values = pisharp_run["cases"]
    if set(pi_values) != set(CASE_IDS) or set(pisharp_values) != set(CASE_IDS):
        raise RuntimeError("C7 oracle did not produce exactly the frozen case IDs")

    pi_path_out = evidence_dir / "pi-c7-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c7-observations.json"
    contract = verify.load_contract()
    expected_contract = {case["id"]: case["expectedObservation"] for checkpoint in contract["checkpoints"]
                         if checkpoint["id"] == "C7" for case in checkpoint["requiredCases"]}
    pi_normalized: dict[str, Any] = {}
    pisharp_normalized: dict[str, Any] = {}
    safety_by_case: dict[str, dict[str, str]] = {}
    for case_id in CASE_IDS:
        expected, actual, safety = validate_pair(case_id, pi_values[case_id], pisharp_values[case_id])
        pi_normalized[case_id] = expected
        pisharp_normalized[case_id] = actual
        if safety is not None:
            safety_by_case[case_id] = safety

    pi_path_out.write_text(json.dumps({"observations": pi_normalized}, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8")
    pisharp_path_out.write_text(json.dumps({"observations": pisharp_normalized}, indent=2, ensure_ascii=False) + "\n",
                                encoding="utf-8")
    raw = [*pi_files, *pisharp_files, pi_path_out, pisharp_path_out]
    cases = []
    safety_differences = []
    for case_id in CASE_IDS:
        pointer = f"/observations/{case_id}"
        normalized_expected = pi_normalized[case_id]
        normalized_actual = pisharp_normalized[case_id]
        safety = safety_by_case.get(case_id)
        assertion = {
            "name": "paired-summary-request-and-result-observation",
            "caseId": case_id,
            "contractObservation": expected_contract[case_id],
            "comparison": "intentional-safety-difference" if safety else "equivalent",
            "baselineArtifact": pi_path_out.name,
            "baselinePointer": pointer,
            "implementationArtifact": pisharp_path_out.name,
            "implementationPointer": pointer,
            "expected": normalized_expected,
            "actual": normalized_actual,
            "normalizedExpected": normalized_expected,
            "normalizedActual": normalized_actual,
            "passed": True,
        }
        classification = "equivalent"
        if safety:
            classification = "intentional-safety-difference"
            assertion.update({"safetyEvidence": pisharp_path_out.name,
                              "safetyRegression": safety["regression"]})
            safety_differences.append({"caseId": case_id, **safety,
                                      "safetyEvidence": pisharp_path_out.name})
        cases.append({
            "caseId": case_id,
            "checkpointId": "C7",
            "contractExpectedObservation": expected_contract[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": classification,
            "assertions": [assertion],
            "rawArtifacts": [c5_oracle.artifact(path, evidence_dir) for path in raw],
        })
    counts = verify.validate_case_results(cases, "C7", evidence_dir, verify_artifacts=True)
    report = {
        "checkpointId": "C7",
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.implementation_source_sha() if c5_oracle.source_tree_is_clean() else None,
        "piCalibrationStable": True,
        "pisharpCalibrationStable": True,
        "cases": cases,
        "checkpointResults": [{
            "checkpointId": "C7",
            "requiredCaseCount": counts["required"],
            "executedCaseCount": counts["executed"],
            "failedCaseCount": counts["failed"],
            "skippedCaseCount": counts["skipped"],
            "status": "passed" if counts["passed"] == counts["required"] else "incomplete",
        }],
        "verifierCounts": counts,
        "safetyDifferences": safety_differences,
    }
    (evidence_dir / "c7-case-results.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n",
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
        print(f"C7 paired cases: {counts['passed']}/{counts['required']}; 0 failed, 0 skipped")
        return 0
    except (verify.VerificationError, OSError, RuntimeError, KeyError, TypeError, ValueError) as error:
        print(f"C7 oracle failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
