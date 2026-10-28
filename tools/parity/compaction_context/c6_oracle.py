#!/usr/bin/env python3
"""Run paired pinned-Pi/PiSharp in-flight tool continuation observations."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
from typing import Any

import c5_oracle
import verify

CASE_IDS = (
    "C6.transient-projection-does-not-change-canonical-history",
    "C6.single-large-current-tool-result-is-bounded",
    "C6.sequential-tool-cycles-preserve-order",
    "C6.parallel-calls-and-results-remain-matched",
    "C6.large-tool-output-summary-retains-latest-outcome",
    "C6.nested-tool-call-sequence-is-closed-before-cut",
    "C6.incomplete-call-graph-fails-before-provider-send",
    "C6.duplicate-or-unmatched-results-never-replay-tools",
    "C6.steering-after-tool-result-keeps-active-turn",
    "C6.post-compaction-request-contains-valid-provider-history",
)

SAFETY_REGRESSIONS = {
    CASE_IDS[1]: (
        "Pi keeps the active 8,000-character result in the provider request; PiSharp projects a bounded summary while retaining the raw canonical result.",
        "tests/PiSharp.Tests/Runtime/Sessions/InFlightContextBudgetTests.cs#OversizedActiveToolResultIsSummarizedInsteadOfKeptInTransientRequest",
    ),
    CASE_IDS[4]: (
        "Pi keeps both large and latest raw tool outcomes in the request; PiSharp summarizes the prefix and retains the latest outcome.",
        "tests/PiSharp.Tests/Runtime/Agents/CompactionTests.cs#LongSummaryTranscriptRetainsLatestMessageWithinBound",
    ),
    CASE_IDS[6]: (
        "Pi sends the incomplete call graph to its provider hook; PiSharp rejects the transient continuation before sending it.",
        "tests/PiSharp.Tests/Runtime/Agents/CompactionTests.cs#OverflowProjectionRefusesMalformedEarlierToolGraph",
    ),
    CASE_IDS[7]: (
        "Pi sends duplicate tool results to its provider hook; PiSharp rejects the malformed graph before sending the transient continuation.",
        "tests/PiSharp.Tests/Runtime/Agents/CompactionTests.cs#OverflowProjectionRefusesMalformedEarlierToolGraph",
    ),
}


def tool_history(observation: dict[str, Any]) -> list[dict[str, Any]]:
    return [message for message in observation.get("providerRequestMessages", [])
            if message.get("callIds") or message.get("resultCallIds")]


def raw_tool_history(messages: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [message for message in messages if message.get("callIds") or message.get("resultCallIds")]


def validate_pair(case_id: str, pi: dict[str, Any], pisharp: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any]]:
    if case_id == CASE_IDS[0]:
        valid = pi["canonicalHistoryUnchanged"] and pisharp["canonicalHistoryUnchanged"] and \
            pi["providerSendCount"] == 2 and pisharp["providerSendCount"] == 1 and \
            pi["providerCallIds"] == ["large-1"] and pi["providerResultCallIds"] == ["large-1"] and \
            pisharp["summarizedCallIds"] == ["large-1"] and pisharp["summarizedResultCallIds"] == ["large-1"]
        expected = {"canonicalHistoryUnchanged": pi["canonicalHistoryUnchanged"]}
        actual = {"canonicalHistoryUnchanged": pisharp["canonicalHistoryUnchanged"]}
    elif case_id == CASE_IDS[1]:
        raw = raw_tool_history(pi["providerRequests"][-1]["messages"])
        canonical = raw_tool_history(pisharp["canonicalMessages"])
        valid = pi["providerHistoryValid"] and pi["projectedToolResultCharacters"] == 8_000 and \
            pisharp["projectedToolResultCharacters"] == 0 and pisharp["latestOutcomeRetained"] and \
            any(message["resultCharacters"] == 8_000 for message in raw) and \
            any(message["resultCharacters"] == 8_000 for message in canonical) and \
            pisharp["canonicalHistoryUnchanged"]
        expected = {"canonicalResultPreserved": True, "providerResultCharacters": pi["projectedToolResultCharacters"]}
        actual = {"canonicalResultPreserved": pisharp["canonicalHistoryUnchanged"],
                  "providerResultCharacters": pisharp["projectedToolResultCharacters"],
                  "summaryRetained": pisharp["latestOutcomeRetained"]}
    elif case_id == CASE_IDS[2]:
        valid = pi["providerHistoryValid"] and pi["providerCallIds"] == ["sequence-1", "sequence-2"] and \
            pi["providerResultCallIds"] == ["sequence-1", "sequence-2"] and \
            pisharp["summarizedHistoryValid"] and pisharp["summarizedCallIds"] == ["sequence-1", "sequence-2"] and \
            pisharp["summarizedResultCallIds"] == ["sequence-1", "sequence-2"]
        expected = {"orderedCallIds": pi["providerCallIds"], "orderedResultCallIds": pi["providerResultCallIds"]}
        actual = {"orderedCallIds": pisharp["summarizedCallIds"],
                  "orderedResultCallIds": pisharp["summarizedResultCallIds"]}
    elif case_id == CASE_IDS[3]:
        pi_calls = sorted(pi["providerCallIds"])
        pi_results = sorted(pi["providerResultCallIds"])
        ps_calls = sorted(pisharp["summarizedCallIds"])
        ps_results = sorted(pisharp["summarizedResultCallIds"])
        valid = pi["providerHistoryValid"] and pisharp["summarizedHistoryValid"] and \
            pi_calls == ["parallel-1", "parallel-2"] and pi_calls == pi_results and ps_calls == pi_calls and ps_results == pi_calls
        expected = {"matchedCallResultIds": pi_calls}
        actual = {"matchedCallResultIds": ps_calls if ps_calls == ps_results else []}
    elif case_id == CASE_IDS[4]:
        valid = pi["providerHistoryValid"] and pi["latestOutcomeRetained"] and \
            pi["projectedToolResultCharacters"] >= 8_000 and pisharp["latestOutcomeRetained"] and \
            "LATEST TOOL OUTCOME" in (pisharp["summaryText"] or "") and \
            pisharp["projectedToolResultCharacters"] == 0
        expected = {"latestOutcomeRetained": pi["latestOutcomeRetained"],
                    "providerResultCharacters": pi["projectedToolResultCharacters"]}
        actual = {"latestOutcomeRetained": pisharp["latestOutcomeRetained"],
                  "providerResultCharacters": pisharp["projectedToolResultCharacters"]}
    elif case_id == CASE_IDS[5]:
        valid = pi["providerHistoryValid"] and pi["nestedExecutionComplete"] is True and \
            pi["nestedCallIds"] == ["outer-call/1"] and pi["providerCallIds"] == ["outer-call"] and \
            pi["providerResultCallIds"] == ["outer-call"] and pisharp["nestedExecutionComplete"] is True and \
            pisharp["nestedCallIds"] == ["outer-call/1"] and pisharp["summarizedHistoryValid"] and \
            pisharp["summarizedCallIds"] == ["outer-call"] and pisharp["summarizedResultCallIds"] == ["outer-call"]
        expected = {"nestedExecutionComplete": True, "nestedCallIds": pi["nestedCallIds"],
                    "closedParentCallIds": pi["providerCallIds"]}
        actual = {"nestedExecutionComplete": pisharp["nestedExecutionComplete"],
                  "nestedCallIds": pisharp["nestedCallIds"],
                  "closedParentCallIds": pisharp["summarizedCallIds"]}
    elif case_id == CASE_IDS[6]:
        valid = pi["providerSendCount"] == 1 and not pi["providerHistoryValid"] and \
            pi["providerCallIds"] == ["pending-call"] and pi["providerResultCallIds"] == [] and \
            pisharp["providerSendCount"] == 0 and not pisharp["providerHistoryValid"] and \
            "no completed tool call/result pair" in pisharp["error"]
        expected = {"providerSendCount": pi["providerSendCount"], "providerHistoryValid": pi["providerHistoryValid"]}
        actual = {"providerSendCount": pisharp["providerSendCount"],
                  "providerHistoryValid": pisharp["providerHistoryValid"]}
    elif case_id == CASE_IDS[7]:
        valid = pi["providerSendCount"] == 1 and not pi["providerHistoryValid"] and \
            pi["providerCallIds"] == ["once-only"] and pi["providerResultCallIds"] == ["once-only", "once-only"] and \
            pisharp["providerSendCount"] == 0 and not pisharp["providerHistoryValid"] and \
            "no completed tool call/result pair" in pisharp["error"]
        expected = {"providerSendCount": pi["providerSendCount"], "providerHistoryValid": pi["providerHistoryValid"]}
        actual = {"providerSendCount": pisharp["providerSendCount"],
                  "providerHistoryValid": pisharp["providerHistoryValid"]}
    elif case_id == CASE_IDS[8]:
        valid = pi["steeringPromptPreserved"] and pi["providerHistoryValid"] and \
            pisharp["steeringPromptPreserved"] and pisharp["providerHistoryValid"] and \
            pisharp["canonicalHistoryUnchanged"]
        expected = {"steeringPromptPreserved": True}
        actual = {"steeringPromptPreserved": pisharp["steeringPromptPreserved"]}
    elif case_id == CASE_IDS[9]:
        valid = pi["providerHistoryValid"] and pisharp["providerHistoryValid"] and \
            pisharp["summarizedHistoryValid"] and len(pi["providerCallIds"]) == 2 and \
            len(pisharp["summarizedCallIds"]) == 2
        expected = {"providerHistoryValid": True, "closedCallResultGroupCount": len(pi["providerCallIds"])}
        actual = {"providerHistoryValid": pisharp["providerHistoryValid"],
                  "closedCallResultGroupCount": len(pisharp["summarizedCallIds"])}
    else:
        raise ValueError(f"no C6 validation for {case_id}")

    if not valid or (case_id not in SAFETY_REGRESSIONS and expected != actual):
        raise RuntimeError(f"C6 required behavior failed for {case_id}:\n" + json.dumps({
            "valid": valid, "expected": expected, "actual": actual,
            "pi": pi, "pisharp": pisharp,
        }, indent=2))
    return expected, actual


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    pi_run, pi_files = c5_oracle.run_pi(pi_path, evidence_dir, "c6")
    pisharp_run, pisharp_files = c5_oracle.run_pisharp(evidence_dir, "c6")
    pi_values = pi_run["cases"]
    pisharp_values = pisharp_run["cases"]
    if set(pi_values) != set(CASE_IDS) or set(pisharp_values) != set(CASE_IDS):
        raise RuntimeError("C6 oracle did not produce exactly the frozen case IDs")

    pi_path_out = evidence_dir / "pi-c6-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c6-observations.json"

    contract = verify.load_contract()
    expected_contract = {case["id"]: case["expectedObservation"] for checkpoint in contract["checkpoints"]
                         if checkpoint["id"] == "C6" for case in checkpoint["requiredCases"]}
    cases = []
    pi_normalized: dict[str, Any] = {}
    pisharp_normalized: dict[str, Any] = {}
    for case_id in CASE_IDS:
        pi = pi_values[case_id]
        pisharp = pisharp_values[case_id]
        normalized_expected, normalized_actual = validate_pair(case_id, pi, pisharp)
        pi_normalized[case_id] = normalized_expected
        pisharp_normalized[case_id] = normalized_actual

    pi_path_out.write_text(json.dumps({"observations": pi_normalized}, indent=2) + "\n", encoding="utf-8")
    pisharp_path_out.write_text(json.dumps({"observations": pisharp_normalized}, indent=2) + "\n", encoding="utf-8")

    for case_id in CASE_IDS:
        normalized_expected = pi_normalized[case_id]
        normalized_actual = pisharp_normalized[case_id]
        safety = SAFETY_REGRESSIONS.get(case_id)
        pointer = f"/observations/{case_id}"
        assertion = {
            "name": "paired-in-flight-tool-continuation-observation",
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
        if safety:
            assertion.update({"safetyEvidence": pisharp_files[0].relative_to(evidence_dir).as_posix(),
                              "safetyRegression": safety[1]})
        case = {
            "caseId": case_id,
            "checkpointId": "C6",
            "contractExpectedObservation": expected_contract[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "intentional-safety-difference" if safety else "equivalent",
            "assertions": [assertion],
            "rawArtifacts": [c5_oracle.artifact(path, evidence_dir)
                             for path in [*pi_files, *pisharp_files, pi_path_out, pisharp_path_out]],
        }
        cases.append(case)

    counts = verify.validate_case_results(cases, "C6", evidence_dir, verify_artifacts=True)
    differences = [{"caseId": case_id, "reason": reason, "regression": regression,
                    "safetyEvidence": pisharp_files[0].relative_to(evidence_dir).as_posix()}
                   for case_id, (reason, regression) in SAFETY_REGRESSIONS.items()]
    result = {
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.git("rev-parse", "HEAD") if c5_oracle.source_tree_is_clean() else None,
        "sourceTreeClean": c5_oracle.source_tree_is_clean(),
        "piCalibrationStable": True,
        "pisharpCalibrationStable": True,
        "pairedObservationsEqual": False,
        "acceptedSafetyDifferenceCount": len(differences),
        "safetyDifferences": differences,
        "unresolvedMismatchCount": 0,
        "normalization": [
            "compare durable raw-history preservation separately from the transient provider request",
            "compare ordered call/result groups in summarized prefixes to raw pinned-Pi continuation requests",
            "sort parallel call and result IDs before checking complete batch matching",
            "compare nested call completion metadata and the closed model-issued parent call/result pair",
            "classify large-result bounding and malformed-graph rejection as accepted safety differences",
        ],
        "executedCaseCount": len(cases),
        "requiredC6CaseCount": len(expected_contract),
        "unexecutedCaseIds": sorted(set(expected_contract) - set(CASE_IDS)),
        "cases": cases,
        "verifierCounts": counts,
    }
    (evidence_dir / "c6-case-results.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"C6 differential: {counts['executed']}/{counts['required']} cases executed; "
          f"{counts['passed']} passed; {len(result['unexecutedCaseIds'])} unexecuted")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        run(args.pi.resolve(), args.evidence_dir.resolve())
    except (OSError, RuntimeError, ValueError, verify.VerificationError) as error:
        print(f"C6 differential failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
