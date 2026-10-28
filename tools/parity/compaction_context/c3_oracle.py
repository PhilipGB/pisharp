#!/usr/bin/env python3
"""Run paired pinned-Pi/PiSharp cut-point and retained-history observations."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
from typing import Any

import c5_oracle
import verify

CASE_IDS = (
    "C3.user-boundary-selection",
    "C3.assistant-boundary-split-turn",
    "C3.tool-call-result-kept-as-one-group",
    "C3.sequential-tool-cycles",
    "C3.parallel-tool-batch-boundary",
    "C3.oversized-trailing-result-falls-back-before-call",
    "C3.never-cut-at-tool-result",
    "C3.incomplete-assistant-and-tool-call-retained",
    "C3.missing-tool-result-provider-projection",
    "C3.unmatched-or-duplicate-tool-result-handling",
    "C3.repeated-compaction-updates-prior-summary",
    "C3.latest-compaction-controls-context",
    "C3.recovery-omission-suffix-boundary",
    "C3.empty-or-insufficient-history-is-no-op",
)


def messages(observation: dict[str, Any], *fields: str) -> list[dict[str, Any]]:
    return [message for field in fields for message in observation.get(field, [])]


def validate(case_id: str, observation: dict[str, Any]) -> None:
    if case_id == "C3.user-boundary-selection":
        valid = (observation["planExists"] and observation["boundaryRole"] == "user" and
                 observation["boundaryIndex"] == 2 and not observation["isSplitTurn"])
    elif case_id == "C3.assistant-boundary-split-turn":
        valid = (observation["planExists"] and observation["boundaryRole"] == "assistant" and
                 observation["isSplitTurn"] and
                 any(message["text"] == "current request" for message in observation["turnPrefixMessages"]))
    elif case_id == "C3.tool-call-result-kept-as-one-group":
        context = observation["contextMessages"]
        valid = (observation["planExists"] and observation["boundaryCallIds"] == ["call-1"] and
                 any(message["callIds"] == ["call-1"] for message in context) and
                 any(message["resultCallIds"] == ["call-1"] for message in context))
    elif case_id == "C3.sequential-tool-cycles":
        prefix = observation["turnPrefixMessages"]
        context = observation["contextMessages"]
        valid = (observation["boundaryCallIds"] == ["cycle-2"] and
                 any(message["callIds"] == ["cycle-1"] for message in prefix) and
                 any(message["resultCallIds"] == ["cycle-1"] for message in prefix) and
                 any(message["callIds"] == ["cycle-2"] for message in context) and
                 any(message["resultCallIds"] == ["cycle-2"] for message in context))
    elif case_id == "C3.parallel-tool-batch-boundary":
        context = observation["contextMessages"]
        valid = (observation["boundaryCallIds"] == ["parallel-1", "parallel-2"] and
                 all(any(message["resultCallIds"] == [call_id] for message in context)
                     for call_id in ("parallel-1", "parallel-2")))
    elif case_id == "C3.oversized-trailing-result-falls-back-before-call":
        valid = (observation["boundaryRole"] == "assistant" and
                 observation["boundaryCallIds"] == ["call-1"] and observation["isSplitTurn"] and
                 [message["text"] for message in observation["messagesToSummarize"]] == ["old history", "old answer"] and
                 [message["text"] for message in observation["turnPrefixMessages"]] == ["read the large file"])
    elif case_id == "C3.never-cut-at-tool-result":
        valid = (observation["planExists"] and observation["boundaryRole"] == "assistant" and
                 observation["boundaryCallIds"] == [] and
                 any(message["resultCallIds"] == ["unmatched-result"] for message in observation["contextMessages"]))
    elif case_id == "C3.incomplete-assistant-and-tool-call-retained":
        valid = (observation["planExists"] and observation["boundaryCallIds"] == ["incomplete-call"] and
                 any(message["callIds"] == ["incomplete-call"] for message in observation["contextMessages"]) and
                 not any(message["resultCallIds"] == ["incomplete-call"] for message in observation["contextMessages"]))
    elif case_id == "C3.missing-tool-result-provider-projection":
        context = observation["contextMessages"]
        valid = (any(message["role"] == "assistant" and message["callIds"] == ["missing-result"]
                     for message in context) and
                 not any("missing-result" in message["resultCallIds"] for message in context))
    elif case_id == "C3.unmatched-or-duplicate-tool-result-handling":
        duplicate_results = [message for message in messages(observation, "messagesToSummarize")
                             if message["resultCallIds"] == ["duplicate-result"]]
        valid = (observation["planExists"] and len(duplicate_results) == 2 and
                 observation["boundaryRole"] == "user" and observation["boundaryText"] == "new request")
    elif case_id == "C3.repeated-compaction-updates-prior-summary":
        summary_text = " ".join(message["text"] for message in observation["messagesToSummarize"])
        valid = (observation["planExists"] and observation["previousSummary"] == "First summary" and
                 "V" * 24 in summary_text and "First summary" not in summary_text)
    elif case_id == "C3.latest-compaction-controls-context":
        valid = (observation["compactionCount"] == 2 and observation["latestSummaryIsSecond"] and
                 observation["previousSummaryOmitted"] and observation["retainedCurrentAnswer"])
    elif case_id == "C3.recovery-omission-suffix-boundary":
        valid = (observation["planExists"] and observation["boundaryRole"] == "assistant" and
                 observation["boundaryCallIds"] == ["failed-call"] and observation["isSplitTurn"] and
                 len(observation["turnPrefixMessages"]) == 1 and
                 observation["turnPrefixMessages"][0]["text"] == "U" * 100)
    elif case_id == "C3.empty-or-insufficient-history-is-no-op":
        valid = observation == {"emptyPlanExists": False, "oneUserPlanExists": False}
    else:
        raise ValueError(f"no C3 validation for {case_id}")
    if not valid:
        raise RuntimeError(f"C3 required behavior failed for {case_id}:\n" +
                           json.dumps(observation, indent=2))


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    pi_run, pi_files = c5_oracle.run_pi(pi_path, evidence_dir, "c3")
    pisharp_run, pisharp_files = c5_oracle.run_pisharp(evidence_dir, "c3")
    pi_values = pi_run["cases"]
    pisharp_values = pisharp_run["cases"]
    if set(pi_values) != set(CASE_IDS) or set(pisharp_values) != set(CASE_IDS):
        raise RuntimeError("C3 oracle did not produce exactly the frozen case IDs")

    pi_path_out = evidence_dir / "pi-c3-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c3-observations.json"
    pi_path_out.write_text(json.dumps({"observations": pi_values}, indent=2) + "\n", encoding="utf-8")
    pisharp_path_out.write_text(json.dumps({"observations": pisharp_values}, indent=2) + "\n", encoding="utf-8")

    contract = verify.load_contract()
    expected = {case["id"]: case["expectedObservation"] for checkpoint in contract["checkpoints"]
                if checkpoint["id"] == "C3" for case in checkpoint["requiredCases"]}
    cases = []
    for case_id in CASE_IDS:
        pi_observation = pi_values[case_id]
        pisharp_observation = pisharp_values[case_id]
        validate(case_id, pi_observation)
        validate(case_id, pisharp_observation)
        if pi_observation != pisharp_observation:
            raise RuntimeError(f"C3 paired observations differ for {case_id}:\n" + json.dumps({
                "pi": pi_observation, "pisharp": pisharp_observation,
            }, indent=2))
        pointer = f"/observations/{case_id}"
        cases.append({
            "caseId": case_id,
            "checkpointId": "C3",
            "contractExpectedObservation": expected[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "equivalent",
            "assertions": [{
                "name": "paired-compaction-cut-point-observation",
                "caseId": case_id,
                "contractObservation": expected[case_id],
                "comparison": "equivalent",
                "baselineArtifact": pi_path_out.name,
                "baselinePointer": pointer,
                "implementationArtifact": pisharp_path_out.name,
                "implementationPointer": pointer,
                "expected": pi_observation,
                "actual": pisharp_observation,
                "normalizedExpected": pi_observation,
                "normalizedActual": pisharp_observation,
                "passed": True,
            }],
            "rawArtifacts": [c5_oracle.artifact(path, evidence_dir)
                             for path in [*pi_files, *pisharp_files, pi_path_out, pisharp_path_out]],
        })

    counts = verify.validate_case_results(cases, "C3", evidence_dir, verify_artifacts=True)
    result = {
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.implementation_source_sha() if c5_oracle.source_tree_is_clean() else None,
        "sourceTreeClean": c5_oracle.source_tree_is_clean(),
        "piCalibrationStable": True,
        "pisharpCalibrationStable": True,
        "pairedObservationsEqual": True,
        "unresolvedMismatchCount": 0,
        "normalization": [
            "normalize MAF tool content to Pi toolCall and toolResult messages",
            "normalize the persisted compaction summary wrapper to Pi compactionSummary content",
            "replace generated entry IDs with stable source indexes",
            "compact repeated long fixture text while preserving its length and edges",
        ],
        "executedCaseCount": len(cases),
        "requiredC3CaseCount": len(expected),
        "unexecutedCaseIds": sorted(set(expected) - set(CASE_IDS)),
        "cases": cases,
        "verifierCounts": counts,
    }
    (evidence_dir / "c3-case-results.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"C3 differential: {counts['executed']}/{counts['required']} cases executed; "
          f"{counts['passed']} passed; {len(result['unexecutedCaseIds'])} unexecuted")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        run(args.pi.resolve(), args.evidence_dir.resolve())
    except (OSError, RuntimeError, ValueError) as error:
        print(f"C3 differential failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
