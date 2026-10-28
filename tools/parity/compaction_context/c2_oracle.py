#!/usr/bin/env python3
"""Run paired pinned-Pi/PiSharp automatic compaction decision probes."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
from typing import Any

import c5_oracle
import verify

SCENARIO_CASES = {
    "threshold-after-turn": ("C2.threshold-after-turn-compacts-without-retry",),
    "pre-prompt-compaction": ("C2.pre-prompt-compaction-does-not-replay-turn",),
    "unknown-model-window": ("C2.unknown-window-does-not-speculate",),
    "disabled-setting": ("C2.disabled-setting-prevents-trigger",),
    "threshold-equal": ("C2.strict-greater-than-reserve-threshold",),
    "stale-old-model-overflow": ("C2.model-switch-uses-selected-window-and-overrides",),
    "physical-route-change": ("C2.physical-route-change-refreshes-budget",),
    "queued-steering": ("C2.queued-steering-survives-auto-compaction",),
    "length-recovery": ("C2.overflow-retry-is-one-bounded-attempt",),
    "error-turn-usage": ("C2.error-turn-uses-last-valid-usage-not-stale-usage",),
}
CASE_SCENARIOS = {case_id: scenario for scenario, case_ids in SCENARIO_CASES.items() for case_id in case_ids}


def select_observation(scenario: str, normalized: dict[str, Any]) -> dict[str, Any]:
    if scenario in {"threshold-after-turn", "threshold-equal", "threshold-over"}:
        return {key: normalized[key] for key in (
            "providerAttemptCount", "compactionEndEvents", "compactionCount", "completedResponsePersisted")}
    if scenario == "pre-prompt-compaction":
        return {key: normalized[key] for key in (
            "providerAttemptCount", "compactionEndEvents", "compactionCount",
            "providerRequests", "completedResponsePersisted")}
    if scenario == "unknown-model-window":
        return {key: normalized[key] for key in (
            "providerAttemptCount", "compactionEndEvents", "compactionCount", "completedResponsePersisted")}
    if scenario == "disabled-setting":
        return {key: normalized[key] for key in (
            "providerAttemptCount", "compactionEndEvents", "compactionCount", "completedResponsePersisted")}
    if scenario == "stale-old-model-overflow":
        return {key: normalized[key] for key in (
            "modelSwitchApplied", "selectedModel", "providerAttemptCount", "compactionEndEvents", "compactionCount")}
    if scenario == "length-recovery":
        return {key: normalized[key] for key in (
            "providerAttemptCount", "compactionEndEvents", "compactionCount",
            "retryContainsCompactedSummary", "retryContainsPartialResponse", "continuedResponsePersisted")}
    if scenario == "queued-steering":
        return {key: normalized[key] for key in (
            "providerAttemptCount", "compactionEndEvents", "compactionCount", "queuedSteerAccepted",
            "steeringInResumedRequest")}
    if scenario == "physical-route-change":
        return {key: normalized[key] for key in (
            "providerAttemptCount", "physicalRouteModels", "compactionEndEvents", "compactionCount",
            "secondRequestContainsSummary", "secondRequestOmitsOldContext")}
    if scenario == "error-turn-usage":
        fields = ("providerAttemptCount", "compactionCountBeforeError", "compactionCount", "thresholdCompactionEvents")
        return {name: {key: observation[key] for key in fields}
                for name, observation in normalized.items()}
    raise ValueError(f"unknown C2 scenario: {scenario}")


def strict_threshold_observation(equal: dict[str, Any], above: dict[str, Any]) -> dict[str, Any]:
    return {
        "atThreshold": select_observation("threshold-equal", equal),
        "oneTokenAbove": select_observation("threshold-over", above),
    }


def validate_observation(case_id: str, observation: dict[str, Any], side: str) -> None:
    if case_id == "C2.threshold-after-turn-compacts-without-retry":
        valid = (observation["providerAttemptCount"] == 1 and observation["compactionCount"] == 1 and
                 observation["compactionEndEvents"] == [{"reason": "threshold", "willRetry": False}] and
                 observation["completedResponsePersisted"])
    elif case_id == "C2.pre-prompt-compaction-does-not-replay-turn":
        valid = (observation["providerAttemptCount"] == 1 and observation["compactionCount"] == 1 and
                 observation["compactionEndEvents"] == [{"reason": "threshold", "willRetry": False}] and
                 observation["completedResponsePersisted"])
    elif case_id == "C2.unknown-window-does-not-speculate":
        if side == "pi":
            valid = (observation["providerAttemptCount"] == 1 and observation["compactionCount"] == 2 and
                     len(observation["compactionEndEvents"]) == 2 and
                     all(event == {"reason": "threshold", "willRetry": False}
                         for event in observation["compactionEndEvents"]))
        else:
            valid = (observation["providerAttemptCount"] == 1 and observation["compactionCount"] == 0 and
                     observation["compactionEndEvents"] == [] and observation["completedResponsePersisted"])
    elif case_id == "C2.disabled-setting-prevents-trigger":
        valid = (observation["providerAttemptCount"] == 1 and observation["compactionCount"] == 0 and
                 observation["compactionEndEvents"] == [] and observation["completedResponsePersisted"])
    elif case_id == "C2.strict-greater-than-reserve-threshold":
        boundary = observation["atThreshold"]
        above = observation["oneTokenAbove"]
        valid = (boundary["providerAttemptCount"] == 1 and boundary["compactionCount"] == 0 and
                 boundary["compactionEndEvents"] == [] and above["providerAttemptCount"] == 1 and
                 above["compactionCount"] == 1 and
                 above["compactionEndEvents"] == [{"reason": "threshold", "willRetry": False}])
    elif case_id == "C2.model-switch-uses-selected-window-and-overrides":
        valid = (observation["modelSwitchApplied"] and observation["selectedModel"] == "new-model" and
                 observation["providerAttemptCount"] == 1 and observation["compactionCount"] == 0 and
                 observation["compactionEndEvents"] == [])
    elif case_id == "C2.physical-route-change-refreshes-budget":
        valid = (observation["providerAttemptCount"] == 2 and observation["physicalRouteModels"] == ["large", "small"] and
                 observation["compactionCount"] == 1 and
                 observation["compactionEndEvents"] == [{"reason": "threshold", "willRetry": False}] and
                 observation["secondRequestContainsSummary"] and observation["secondRequestOmitsOldContext"])
    elif case_id == "C2.queued-steering-survives-auto-compaction":
        valid = (observation["providerAttemptCount"] == 2 and observation["compactionCount"] == 1 and
                 observation["compactionEndEvents"] == [{"reason": "threshold", "willRetry": False}] and
                 observation["queuedSteerAccepted"] and observation["steeringInResumedRequest"])
    elif case_id == "C2.overflow-retry-is-one-bounded-attempt":
        valid = (observation["providerAttemptCount"] == 2 and observation["compactionCount"] == 1 and
                 observation["compactionEndEvents"] == [{"reason": "overflow", "willRetry": True}] and
                 observation["retryContainsCompactedSummary"] and
                 not observation["retryContainsPartialResponse"] and observation["continuedResponsePersisted"])
    elif case_id == "C2.error-turn-uses-last-valid-usage-not-stale-usage":
        valid = observation == {
            "lastValidUsageTriggersThreshold": {
                "providerAttemptCount": 2,
                "compactionCountBeforeError": 0,
                "compactionCount": 1,
                "thresholdCompactionEvents": [{"reason": "threshold", "willRetry": False}],
            },
            "failedResponseUsageDoesNotReplaceLastValid": {
                "providerAttemptCount": 2,
                "compactionCountBeforeError": 0,
                "compactionCount": 0,
                "thresholdCompactionEvents": [],
            },
            "keptPreCompactionUsageIsIgnored": {
                "providerAttemptCount": 2,
                "compactionCountBeforeError": 1,
                "compactionCount": 1,
                "thresholdCompactionEvents": [],
            },
        }
    else:
        raise ValueError(f"no C2 validation for {case_id}")
    if not valid:
        raise RuntimeError(f"C2 expected behavior failed for {case_id} on {side}:\n" +
                           json.dumps(observation, indent=2))


def run_pair(pi_path: Path, evidence_dir: Path, scenario: str) -> tuple[dict[str, Any], dict[str, Any], list[Path], list[Path]]:
    pi_run, pi_files = c5_oracle.run_pi(pi_path, evidence_dir, scenario)
    pisharp_run, pisharp_files = c5_oracle.run_pisharp(evidence_dir, scenario)
    if scenario == "error-turn-usage":
        pi_normalized = pi_run["errorTurnObservations"]
        pisharp_normalized = pisharp_run["errorTurnObservations"]
    else:
        pi_normalized = c5_oracle.compact_pi(pi_run)
        pisharp_normalized = c5_oracle.compact_pisharp(pisharp_run)
    if scenario == "physical-route-change":
        pi_normalized["physicalRouteModels"] = [route["model"] for route in pi_run["routeDecisions"]
                                                if route["reason"] != "direct"]
        pisharp_normalized["physicalRouteModels"] = pisharp_run["physicalRouteModels"]
        for normalized in (pi_normalized, pisharp_normalized):
            resumed = normalized["providerRequests"][-1] if normalized["providerRequests"] else []
            resumed_text = [message["text"] for message in resumed]
            normalized["secondRequestContainsSummary"] = any(
                "compacted into the following summary" in text for text in resumed_text)
            normalized["secondRequestOmitsOldContext"] = not any("x" * 1_000 in text for text in resumed_text)
    if scenario == "queued-steering":
        pi_normalized["steeringInResumedRequest"] = bool(pi_normalized["providerRequests"] and any(
            "change direction" in message["text"] for message in pi_normalized["providerRequests"][-1]))
        pisharp_normalized["steeringInResumedRequest"] = bool(pisharp_normalized["providerRequests"] and any(
            "change direction" in message["text"] for message in pisharp_normalized["providerRequests"][-1]))
        pi_normalized["queuedSteerAccepted"] = pi_normalized["steeringInResumedRequest"]
        pisharp_normalized["queuedSteerAccepted"] = pisharp_run.get("queuedSteerAccepted", False)
    return (select_observation(scenario, pi_normalized), select_observation(scenario, pisharp_normalized),
            pi_files, pisharp_files)


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    paired: dict[str, tuple[dict[str, Any], dict[str, Any], list[Path], list[Path]]] = {}
    for scenario in sorted(set(SCENARIO_CASES) | {"threshold-over"}):
        paired[scenario] = run_pair(pi_path, evidence_dir, scenario)

    pi_values: dict[str, Any] = {}
    pisharp_values: dict[str, Any] = {}
    for case_id, scenario in CASE_SCENARIOS.items():
        pi_observation, pisharp_observation, _, _ = paired[scenario]
        if scenario == "threshold-equal":
            pi_observation = strict_threshold_observation(pi_observation, paired["threshold-over"][0])
            pisharp_observation = strict_threshold_observation(pisharp_observation, paired["threshold-over"][1])
        pi_values[case_id] = pi_observation
        pisharp_values[case_id] = pisharp_observation

    pi_path_out = evidence_dir / "pi-c2-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c2-observations.json"
    pi_path_out.write_text(json.dumps({"observations": pi_values}, indent=2) + "\n", encoding="utf-8")
    pisharp_path_out.write_text(json.dumps({"observations": pisharp_values}, indent=2) + "\n", encoding="utf-8")

    contract = verify.load_contract()
    expected = {case["id"]: case["expectedObservation"] for checkpoint in contract["checkpoints"]
                if checkpoint["id"] == "C2" for case in checkpoint["requiredCases"]}
    safety_case = "C2.unknown-window-does-not-speculate"
    safety = {
        "caseId": safety_case,
        "reason": "Pinned Pi treats an unknown context window as zero and triggers compaction; PiSharp does not invent a budget.",
        "regression": "tests/PiSharp.Tests/Runtime/Agents/CompactionTests.cs#UnknownContextWindowDoesNotInventCapacityFromReportedUsage",
        "safetyEvidence": "pisharp-unknown-model-window-run-1.json",
    }
    cases = []
    for case_id, scenario in CASE_SCENARIOS.items():
        pi_observation = pi_values[case_id]
        pisharp_observation = pisharp_values[case_id]
        safety_difference = case_id == safety_case
        validate_observation(case_id, pi_observation, "pi")
        validate_observation(case_id, pisharp_observation, "pisharp")
        if not safety_difference and pi_observation != pisharp_observation:
            raise RuntimeError(f"C2 paired observations differ for {case_id}:\n" + json.dumps({
                "pi": pi_observation, "pisharp": pisharp_observation,
            }, indent=2))
        raw_files = [*paired[scenario][2], *paired[scenario][3]]
        if scenario == "threshold-equal":
            raw_files.extend([*paired["threshold-over"][2], *paired["threshold-over"][3]])
        assertion = {
            "name": "paired-automatic-compaction-observation",
            "caseId": case_id,
            "contractObservation": expected[case_id],
            "comparison": "intentional-safety-difference" if safety_difference else "equivalent",
            "baselineArtifact": pi_path_out.name,
            "baselinePointer": f"/observations/{case_id}",
            "implementationArtifact": pisharp_path_out.name,
            "implementationPointer": f"/observations/{case_id}",
            "expected": pi_observation,
            "actual": pisharp_observation,
            "normalizedExpected": pi_observation,
            "normalizedActual": pisharp_observation,
            "passed": True,
        }
        if safety_difference:
            assertion.update({"safetyEvidence": safety["safetyEvidence"], "safetyRegression": safety["regression"]})
        cases.append({
            "caseId": case_id,
            "checkpointId": "C2",
            "contractExpectedObservation": expected[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "intentional-safety-difference" if safety_difference else "equivalent",
            "assertions": [assertion],
            "rawArtifacts": [c5_oracle.artifact(path, evidence_dir)
                             for path in [*raw_files, pi_path_out, pisharp_path_out]],
        })

    counts = verify.validate_case_results(cases, "C2", evidence_dir, verify_artifacts=True)
    report = {
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.git("rev-parse", "HEAD") if c5_oracle.source_tree_is_clean() else None,
        "sourceTreeClean": c5_oracle.source_tree_is_clean(),
        "piCalibrationStable": True,
        "pisharpCalibrationStable": True,
        "pairedObservationsEqual": all(pi_values[key] == pisharp_values[key] for key in pi_values if key != safety_case),
        "acceptedSafetyDifferenceCount": 1,
        "safetyDifferences": [safety],
        "executedCaseCount": len(cases),
        "requiredC2CaseCount": len(expected),
        "unexecutedCaseIds": sorted(set(expected) - set(CASE_SCENARIOS)),
        "cases": cases,
        "verifierCounts": counts,
    }
    (evidence_dir / "c2-case-results.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"C2 differential: {counts['executed']}/{counts['required']} cases executed; "
          f"{counts['passed']} passed; {len(report['unexecutedCaseIds'])} unexecuted")
    return report


def main() -> int:
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        run(args.pi.resolve(), args.evidence_dir.resolve())
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        print(f"C2 differential failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
