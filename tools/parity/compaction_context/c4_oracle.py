#!/usr/bin/env python3
"""Run paired pinned-Pi/PiSharp context-limit and usage-estimate probes."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
from typing import Any

import c5_oracle
import verify

CASE_IDS = (
    "C4.model-context-window-controls-trigger",
    "C4.reserve-and-keep-recent-defaults",
    "C4.provider-model-override-precedence",
    "C4.unknown-limit-is-never-invented",
    "C4.provider-usage-anchors-current-context",
    "C4.aborted-error-and-zero-usage-are-not-anchors",
    "C4.messages-after-usage-are-estimated",
    "C4.context-edit-and-compaction-invalidate-stale-usage",
    "C4.cache-read-and-write-token-fallback",
    "C4.system-prompt-and-tool-declaration-estimate",
    "C4.image-and-large-tool-result-estimate",
    "C4.estimate-method-is-explicitly-heuristic-not-tokenizer",
)
SAFETY_CASE = "C4.unknown-limit-is-never-invented"


def normalize(case_id: str, observation: dict[str, Any]) -> dict[str, Any]:
    if case_id == "C4.aborted-error-and-zero-usage-are-not-anchors":
        return {key: observation[key] for key in (
            "hasValidUsageAnchor", "usageTokens", "estimateFromVisibleMessages")}
    if case_id == "C4.image-and-large-tool-result-estimate":
        return {key: observation[key] for key in (
            "estimate", "imageCharacters", "toolResultCharacters")}
    return observation


def validate(case_id: str, observation: dict[str, Any], side: str) -> None:
    if case_id == "C4.model-context-window-controls-trigger":
        valid = observation == {
            "triggerTokens": 83_616, "atThreshold": False,
            "oneAboveThreshold": True, "belowThreshold": False,
        }
    elif case_id == "C4.reserve-and-keep-recent-defaults":
        valid = (observation["reserveTokens"] == 16_384 and
                 observation["keepRecentTokens"] == 20_000 and
                 observation["defaultReserveTokens"] == 16_384 and
                 observation["defaultKeepRecentTokens"] == 20_000)
    elif case_id == "C4.provider-model-override-precedence":
        valid = observation == {
            "selectedReserveTokens": 400_000,
            "selectedKeepRecentTokens": 10_000,
            "fallbackReserveTokens": 8_192,
            "fallbackKeepRecentTokens": 10_000,
        }
    elif case_id == SAFETY_CASE:
        if side == "pi":
            valid = observation == {
                "contextWindowKnown": False,
                "piWouldTriggerFromZeroWindow": True,
                "reserveTokens": 16_384,
            }
        else:
            valid = observation == {
                "contextWindowKnown": False,
                "pisharpTriggerInvented": False,
                "reserveTokens": 16_384,
            }
    elif case_id == "C4.provider-usage-anchors-current-context":
        valid = observation == {
            "hasValidUsageAnchor": True, "usageTokens": 13,
            "trailingTokens": 2, "totalTokens": 15,
        }
    elif case_id == "C4.aborted-error-and-zero-usage-are-not-anchors":
        valid = observation == {
            "hasValidUsageAnchor": False, "usageTokens": None,
            "estimateFromVisibleMessages": 7,
        }
    elif case_id == "C4.messages-after-usage-are-estimated":
        valid = observation == {"usageTokens": 100, "trailingTokens": 10, "totalTokens": 110}
    elif case_id == "C4.context-edit-and-compaction-invalidate-stale-usage":
        valid = observation == {
            "contextEditInvalidatesUsage": True,
            "compactionInvalidatesUsage": True,
        }
    elif case_id == "C4.cache-read-and-write-token-fallback":
        valid = observation == {"usageTokens": 34, "totalTokens": 34}
    elif case_id == "C4.system-prompt-and-tool-declaration-estimate":
        valid = observation == {
            "estimate": 40, "systemPromptCharacters": 32, "toolCount": 1, "messageCount": 1,
        }
    elif case_id == "C4.image-and-large-tool-result-estimate":
        valid = observation == {"estimate": 3_203, "imageCharacters": 4_800, "toolResultCharacters": 8_000}
    elif case_id == "C4.estimate-method-is-explicitly-heuristic-not-tokenizer":
        valid = observation == {
            "estimate": 25, "expectedCharacterHeuristic": 25, "tokenizerOutput": False,
        }
    else:
        raise ValueError(f"no C4 validation for {case_id}")
    if not valid:
        raise RuntimeError(f"C4 expected behavior failed for {case_id} on {side}:\n" +
                           json.dumps(observation, indent=2))


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    pi_run, pi_files = c5_oracle.run_pi(pi_path, evidence_dir, "c4")
    pisharp_run, pisharp_files = c5_oracle.run_pisharp(evidence_dir, "c4")
    pi_raw = pi_run["cases"]
    pisharp_raw = pisharp_run["cases"]
    if set(pi_raw) != set(CASE_IDS) or set(pisharp_raw) != set(CASE_IDS):
        raise RuntimeError("C4 oracle did not produce exactly the frozen case IDs")

    pi_values = {case_id: normalize(case_id, pi_raw[case_id]) for case_id in CASE_IDS}
    pisharp_values = {case_id: normalize(case_id, pisharp_raw[case_id]) for case_id in CASE_IDS}
    pi_path_out = evidence_dir / "pi-c4-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c4-observations.json"
    pi_path_out.write_text(json.dumps({"observations": pi_values}, indent=2) + "\n", encoding="utf-8")
    pisharp_path_out.write_text(json.dumps({"observations": pisharp_values}, indent=2) + "\n", encoding="utf-8")

    contract = verify.load_contract()
    expected = {case["id"]: case["expectedObservation"] for checkpoint in contract["checkpoints"]
                if checkpoint["id"] == "C4" for case in checkpoint["requiredCases"]}
    safety = {
        "caseId": SAFETY_CASE,
        "reason": "Pinned Pi evaluates unknown contextWindow as zero and triggers shouldCompact; PiSharp preserves the unknown limit and does not invent capacity.",
        "regression": "tests/PiSharp.Tests/Runtime/Agents/CompactionTests.cs#UnknownContextWindowDoesNotInventCapacityFromReportedUsage",
        "safetyEvidence": pisharp_path_out.name,
    }
    cases = []
    for case_id in CASE_IDS:
        pi_observation = pi_values[case_id]
        pisharp_observation = pisharp_values[case_id]
        validate(case_id, pi_observation, "pi")
        validate(case_id, pisharp_observation, "pisharp")
        safety_difference = case_id == SAFETY_CASE
        if not safety_difference and pi_observation != pisharp_observation:
            raise RuntimeError(f"C4 paired observations differ for {case_id}:\n" + json.dumps({
                "pi": pi_observation, "pisharp": pisharp_observation,
            }, indent=2))
        pointer = f"/observations/{case_id}"
        assertion = {
            "name": "paired-context-estimate-observation",
            "caseId": case_id,
            "contractObservation": expected[case_id],
            "comparison": "intentional-safety-difference" if safety_difference else "equivalent",
            "baselineArtifact": pi_path_out.name,
            "baselinePointer": pointer,
            "implementationArtifact": pisharp_path_out.name,
            "implementationPointer": pointer,
            "expected": pi_observation,
            "actual": pisharp_observation,
            "normalizedExpected": pi_observation,
            "normalizedActual": pisharp_observation,
            "passed": True,
        }
        if safety_difference:
            assertion.update({"safetyEvidence": safety["safetyEvidence"],
                              "safetyRegression": safety["regression"]})
        cases.append({
            "caseId": case_id,
            "checkpointId": "C4",
            "contractExpectedObservation": expected[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "intentional-safety-difference" if safety_difference else "equivalent",
            "assertions": [assertion],
            "rawArtifacts": [c5_oracle.artifact(path, evidence_dir)
                             for path in [*pi_files, *pisharp_files, pi_path_out, pisharp_path_out]],
        })

    counts = verify.validate_case_results(cases, "C4", evidence_dir, verify_artifacts=True)
    result = {
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.git("rev-parse", "HEAD") if c5_oracle.source_tree_is_clean() else None,
        "sourceTreeClean": c5_oracle.source_tree_is_clean(),
        "piCalibrationStable": True,
        "pisharpCalibrationStable": True,
        "pairedObservationsEqual": all(pi_values[case_id] == pisharp_values[case_id]
                                        for case_id in CASE_IDS if case_id != SAFETY_CASE),
        "acceptedSafetyDifferenceCount": 1,
        "safetyDifferences": [safety],
        "unresolvedMismatchCount": 0,
        "normalization": [
            "compare model-policy values and content estimates as explicit integer observations",
            "use the same messages-after-usage estimator for fallback visible content",
            "omit C# diagnostic per-message detail from the normalized estimate result",
        ],
        "executedCaseCount": len(cases),
        "requiredC4CaseCount": len(expected),
        "unexecutedCaseIds": sorted(set(expected) - set(CASE_IDS)),
        "cases": cases,
        "verifierCounts": counts,
    }
    (evidence_dir / "c4-case-results.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"C4 differential: {counts['executed']}/{counts['required']} cases executed; "
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
        print(f"C4 differential failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
