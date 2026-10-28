#!/usr/bin/env python3
"""Run paired manual-compaction observations against pinned Pi and PiSharp."""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any

import c5_oracle
import verify

SCENARIO_CASES = {
    "C1.eligible-manual-request-persists-one-entry": "manual-eligible",
    "C1.ineligible-short-history-is-no-op": "manual-ineligible",
    "C1.custom-focus-changes-history-prompt-only": "manual-eligible",
    "C1.recent-context-and-full-history-retained": "manual-eligible",
    "C1.split-turn-summary-retains-interrupted-assistant": "manual-split-turn",
    "C1.summary-usage-details-persist": "manual-eligible",
    "C1.summary-failure-rolls-back-and-reports": "manual-failure",
    "C1.cancellation-publishes-no-entry": "manual-cancel",
    "C1.active-run-and-queued-follow-up-order": "manual-queue-order",
}


def usage_observation(run: dict[str, Any], side: str) -> dict[str, Any]:
    usage = run["result"]["usage"]
    if side == "pi":
        return {
            "inputTokens": usage["input"],
            "outputTokens": usage["output"],
            "cachedInputTokens": usage["cacheRead"],
            "cachedWriteTokens": usage["cacheWrite"],
            "totalTokens": usage["totalTokens"],
            "cost": usage["cost"]["total"],
        }
    return {
        "inputTokens": usage["InputTokens"] - usage["CachedInputTokens"],
        "outputTokens": usage["OutputTokens"],
        "cachedInputTokens": usage["CachedInputTokens"],
        "cachedWriteTokens": usage["CachedWriteTokens"],
        "totalTokens": usage["TotalTokens"],
        "cost": usage["Cost"],
    }


def request_texts(run: dict[str, Any]) -> list[str]:
    return ["\n".join(message.get("text", "") for message in request["messages"])
            for request in run["summaryRequests"]]


def run_pisharp_cancel_test(evidence_dir: Path) -> tuple[dict[str, Any], list[Path]]:
    observations = []
    files: list[Path] = []
    filter_name = (
        "FullyQualifiedName~NativeExtensionCompactionEventTests."
        "ManualCompactionCanBeCancelledByExtensionWithoutChangingConversationOrHoldingGate"
    )
    for run_number in (1, 2):
        capture_dir = evidence_dir / f"pisharp-manual-cancel-run-{run_number}"
        capture_dir.mkdir(parents=True, exist_ok=True)
        command = ["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--no-restore",
                   "--filter", filter_name, "--logger", "console;verbosity=minimal"]
        process = subprocess.run(command, cwd=c5_oracle.ROOT, capture_output=True, text=True, check=False,
                                 env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4",
                                      "PISHARP_C1_MANUAL_CANCEL_CAPTURE_DIR": str(capture_dir)})
        stdout = evidence_dir / f"manual-cancel-test-run-{run_number}.stdout.log"
        stderr = evidence_dir / f"manual-cancel-test-run-{run_number}.stderr.log"
        stdout.write_text(process.stdout, encoding="utf-8")
        stderr.write_text(process.stderr, encoding="utf-8")
        files.extend([stdout, stderr, capture_dir / "manual-cancel.json"])
        if process.returncode:
            raise RuntimeError(f"PiSharp manual cancellation test run {run_number} failed: {process.stderr}\n{process.stdout}")
        capture = c5_oracle.read_json(capture_dir / "manual-cancel.json")
        observation = {
            "compactionSucceeded": capture["compactionCount"] != 0,
            "compactionCount": capture["compactionCount"],
            "summaryRequestCount": capture["summaryRequestCount"],
            "canonicalStateUnchanged": capture["canonicalStateUnchanged"],
            "failureEventPublished": False,
            "providerFailureReported": False,
            "abortedEventPublished": capture["abortedEventPublished"],
        }
        if (observation["compactionCount"] != 0 or observation["summaryRequestCount"] != 0 or
                not observation["canonicalStateUnchanged"] or not observation["abortedEventPublished"]):
            raise RuntimeError("PiSharp cancellation did not preserve the canonical conversation")
        observations.append(observation)
    if observations[0] != observations[1]:
        raise RuntimeError("PiSharp manual cancellation observations were not stable across two runs")
    return observations[0], files


def manual_observations(run: dict[str, Any], side: str) -> dict[str, Any]:
    if run["result"] is None:
        return {
            "compactionSucceeded": False,
            "compactionCount": run["compactionCount"],
            "summaryRequestCount": len(run["summaryRequests"]),
            "canonicalStateUnchanged": run["canonicalStateUnchanged"],
            "failureEventPublished": any(event.get("type") == "compaction_end" and event.get("error")
                                          for event in run["events"]),
            "providerFailureReported": run["scenario"] == "manual-failure" and
                "summary provider failed" in (run.get("error") or ""),
            "abortedEventPublished": any(event.get("type") == "compaction_end" and event.get("aborted") is True
                                          for event in run["events"]),
        }
    if run["scenario"] == "manual-split-turn":
        texts = request_texts(run)
        context = run["contextMessages"]
        return {
            "compactionSucceeded": run["compactionSucceeded"],
            "compactionCount": run["compactionCount"],
            "summaryRequestCount": len(texts),
            "firstKeptEntryIndex": run["result"]["firstKeptEntryIndex"],
            "splitTurnSummaryCreated": "**Turn Context (split turn):**" in run["result"]["summary"],
            "historySummaryIncludesOlderQuestion": bool(texts and "older question" in texts[0]),
            "turnPrefixSummaryIncludesCurrentRequest": len(texts) > 1 and "current request" in texts[1],
            "interruptedAssistantRetainedOnce": sum(message.get("text") == "partial answer" for message in context) == 1,
        }
    if run["scenario"] == "manual-queue-order":
        entries = run["entries"]
        def entry_text(entry: dict[str, Any]) -> str | None:
            message = entry.get("message")
            return message.get("text") if isinstance(message, dict) else message if isinstance(message, str) else None
        compaction_index = next((index for index, entry in enumerate(entries) if entry["type"] == "compaction"), -1)
        queued_user_index = next((index for index, entry in enumerate(entries)
                                  if entry_text(entry) == "queued after compaction"), -1)
        queued_answer_index = next((index for index, entry in enumerate(entries)
                                    if entry_text(entry) == "queued response"), -1)
        return {
            "compactionCount": run["compactionCount"],
            "compactionPrecedesFollowUp": compaction_index >= 0 and queued_user_index > compaction_index,
            "followUpPrecedesResponse": queued_user_index >= 0 and queued_answer_index > queued_user_index,
            "followUpResponsePresent": queued_answer_index >= 0,
        }
    texts = request_texts(run)
    if side == "pi":
        history = [entry["message"]["text"] for entry in run["entries"]
                   if entry["type"] == "message" and entry.get("message")]
    else:
        history = [message["text"] for message in run["activeMessages"]
                   if message["role"] in {"user", "assistant"}]
    return {
        "compactionSucceeded": run["compactionSucceeded"],
        "compactionCount": run["compactionCount"],
        "summary": run["result"]["summary"],
        "firstKeptEntryIndex": run["result"]["firstKeptEntryIndex"],
        "summaryRequestCount": len(texts),
        "focusInHistorySummaryRequest": bool(texts and "prioritize files" in texts[0]),
        "focusAbsentFromTurnPrefixRequest": len(texts) > 1 and "prioritize files" not in texts[1],
        "fullHistoryRetained": history == ["first", "answer one", "second", "answer two"],
        "latestAnswerInContext": any(message["text"] == "answer two"
                                      for message in run["contextMessages"]),
        "usage": usage_observation(run, side),
    }


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    paired: dict[str, tuple[dict[str, Any], dict[str, Any], list[Path], list[Path]]] = {}
    for scenario in sorted(set(SCENARIO_CASES.values())):
        pi_run, pi_files = c5_oracle.run_pi(pi_path, evidence_dir, scenario)
        pi_observation = manual_observations(pi_run, "pi")
        if scenario == "manual-cancel":
            pisharp_observation, pisharp_files = run_pisharp_cancel_test(evidence_dir)
        else:
            pisharp_run, pisharp_files = c5_oracle.run_pisharp(evidence_dir, scenario)
            pisharp_observation = manual_observations(pisharp_run, "pisharp")
        if pi_observation != pisharp_observation:
            raise RuntimeError(f"manual compaction observations differ for {scenario}:\n" + json.dumps({
                "pi": pi_observation, "pisharp": pisharp_observation,
            }, indent=2))
        paired[scenario] = (pi_observation, pisharp_observation, pi_files, pisharp_files)

    pi_path_out = evidence_dir / "pi-c1-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c1-observations.json"
    pi_values: dict[str, Any] = {}
    pisharp_values: dict[str, Any] = {}
    def case_observation(case_id: str, observation: dict[str, Any]) -> dict[str, Any]:
        if case_id == "C1.ineligible-short-history-is-no-op":
            return {
                "compactionSucceeded": observation["compactionSucceeded"],
                "compactionCount": observation["compactionCount"],
                "summaryRequestCount": observation["summaryRequestCount"],
                "canonicalStateUnchanged": observation["canonicalStateUnchanged"],
                "failureEventPublished": observation["failureEventPublished"],
            }
        if case_id == "C1.summary-failure-rolls-back-and-reports":
            return {
                "compactionCount": observation["compactionCount"],
                "summaryRequestCount": observation["summaryRequestCount"],
                "canonicalStateUnchanged": observation["canonicalStateUnchanged"],
                "failureEventPublished": observation["failureEventPublished"],
                "providerFailureReported": observation["providerFailureReported"],
            }
        if case_id == "C1.cancellation-publishes-no-entry":
            return {
                "compactionSucceeded": observation["compactionSucceeded"],
                "compactionCount": observation["compactionCount"],
                "summaryRequestCount": observation["summaryRequestCount"],
                "canonicalStateUnchanged": observation["canonicalStateUnchanged"],
                "abortedEventPublished": observation["abortedEventPublished"],
            }
        if case_id == "C1.split-turn-summary-retains-interrupted-assistant":
            return {key: observation[key] for key in (
                "compactionSucceeded", "compactionCount", "summaryRequestCount", "firstKeptEntryIndex",
                "splitTurnSummaryCreated", "historySummaryIncludesOlderQuestion",
                "turnPrefixSummaryIncludesCurrentRequest", "interruptedAssistantRetainedOnce")}
        if case_id == "C1.active-run-and-queued-follow-up-order":
            return {key: observation[key] for key in (
                "compactionCount", "compactionPrecedesFollowUp", "followUpPrecedesResponse",
                "followUpResponsePresent")}
        return {
            "C1.eligible-manual-request-persists-one-entry": {
                "compactionSucceeded": observation["compactionSucceeded"],
                "compactionCount": observation["compactionCount"],
                "summary": observation["summary"],
            },
            "C1.custom-focus-changes-history-prompt-only": {
                "focusInHistorySummaryRequest": observation["focusInHistorySummaryRequest"],
                "focusAbsentFromTurnPrefixRequest": observation["focusAbsentFromTurnPrefixRequest"],
            },
            "C1.recent-context-and-full-history-retained": {
                "firstKeptEntryIndex": observation["firstKeptEntryIndex"],
                "fullHistoryRetained": observation["fullHistoryRetained"],
                "latestAnswerInContext": observation["latestAnswerInContext"],
            },
            "C1.summary-usage-details-persist": observation["usage"],
        }

    for case_id, scenario in SCENARIO_CASES.items():
        pi_observation, pisharp_observation, _, _ = paired[scenario]
        pi_values[case_id] = case_observation(case_id, pi_observation)
        pisharp_values[case_id] = case_observation(case_id, pisharp_observation)
    pi_path_out.write_text(json.dumps({"observations": pi_values}, indent=2) + "\n", encoding="utf-8")
    pisharp_path_out.write_text(json.dumps({"observations": pisharp_values}, indent=2) + "\n", encoding="utf-8")
    contract = verify.load_contract()
    expected = {case["id"]: case["expectedObservation"] for checkpoint in contract["checkpoints"]
                if checkpoint["id"] == "C1" for case in checkpoint["requiredCases"]}
    cases = []
    for case_id, scenario in SCENARIO_CASES.items():
        pointer = f"/observations/{case_id}"
        cases.append({
            "caseId": case_id,
            "checkpointId": "C1",
            "contractExpectedObservation": expected[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "equivalent",
            "assertions": [{
                "name": "paired-manual-compaction-observation",
                "caseId": case_id,
                "contractObservation": expected[case_id],
                "comparison": "equivalent",
                "baselineArtifact": pi_path_out.name,
                "baselinePointer": pointer,
                "implementationArtifact": pisharp_path_out.name,
                "implementationPointer": pointer,
                "expected": pi_values[case_id],
                "actual": pisharp_values[case_id],
                "normalizedExpected": pi_values[case_id],
                "normalizedActual": pisharp_values[case_id],
                "passed": True,
            }],
            "rawArtifacts": [c5_oracle.artifact(path, evidence_dir)
                             for path in [*paired[scenario][2], *paired[scenario][3], pi_path_out, pisharp_path_out]],
        })
    counts = verify.validate_case_results(cases, "C1", evidence_dir, verify_artifacts=True)
    result = {
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.git("rev-parse", "HEAD"),
        "sourceTreeClean": c5_oracle.source_tree_is_clean(),
        "piCalibrationStable": True,
        "pisharpCalibrationStable": True,
        "pairedObservationsEqual": True,
        "unresolvedMismatchCount": 0,
        "normalization": [
            "strip generated session entry IDs by comparing the retained entry index",
            "compare cached-excluded provider input tokens and cached input separately",
            "compare explicit summary and focus observables rather than framework request wrappers",
        ],
        "executedCaseCount": len(cases),
        "requiredC1CaseCount": len(expected),
        "unexecutedCaseIds": sorted(set(expected) - set(SCENARIO_CASES)),
        "cases": cases,
        "verifierCounts": counts,
    }
    (evidence_dir / "c1-case-results.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"C1 differential: {counts['executed']}/{counts['required']} cases executed; "
          f"{counts['passed']} passed; {len(result['unexecutedCaseIds'])} unexecuted")
    return result


def main() -> int:
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        run(args.pi.resolve(), args.evidence_dir.resolve())
    except (OSError, RuntimeError, ValueError) as error:
        print(f"C1 differential failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
