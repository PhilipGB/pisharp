#!/usr/bin/env python3
"""Run the paired pinned-Pi/PiSharp C5 length-stop recovery differential."""
from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
PI_ORACLE = HERE / "pi_oracle.mjs"
CS_PROJECT = HERE / "pisharp_runner" / "PiSharp.CompactionOracle.csproj"
CASE_IDS = (
    "C5.recoverable-length-stop-retries-once",
    "C5.retry-request-uses-compacted-transcript",
)


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(*arguments: str) -> str:
    return subprocess.run(["git", *arguments], cwd=ROOT, capture_output=True, text=True,
                          check=True).stdout.strip()


def source_tree_is_clean() -> bool:
    changed = subprocess.run(["git", "diff", "--quiet", "HEAD", "--", "src", "tests", "tools/parity"],
                             cwd=ROOT, capture_output=True)
    untracked = subprocess.run(["git", "ls-files", "--others", "--exclude-standard", "--",
                                "src", "tests", "tools/parity"], cwd=ROOT, capture_output=True, text=True)
    return changed.returncode == 0 and not untracked.stdout.strip()


def compact_pi(pi_run: dict[str, Any]) -> dict[str, Any]:
    requests = pi_run["providerRequests"]
    summary_requests = [request for request in requests if any(
        message["role"] == "system" and message["text"].startswith("You are a context summarization assistant")
        for message in request["messages"])]
    provider_requests = [request for request in requests if request not in summary_requests]
    normalized_requests = [[message for message in request["messages"]
                           if not (message["role"] == "system" and message["text"] == "")]
                          for request in provider_requests]
    overflow_events = [{"reason": event["reason"], "willRetry": event["willRetry"]}
                       for event in pi_run["events"] if event["type"] == "compaction_end"]
    return {
        "providerRequests": normalized_requests,
        "summaryRequestCount": len(summary_requests),
        "overflowCompactions": overflow_events,
        "compactionCount": pi_run["compactionCount"],
        "retryContainsPartialResponse": any(
            "partial output" in message["text"] for message in normalized_requests[-1]),
        "retryContainsCompactedSummary": any(
            "compacted history summary" in message["text"] for message in normalized_requests[-1]),
        "continuedResponsePersisted": any(
            message["role"] == "assistant" and message["text"] == "continued response"
            for message in pi_run["activeMessages"]),
    }


def compact_pisharp(result: dict[str, Any]) -> dict[str, Any]:
    overflow_events = [{"reason": event["CompactionReason"], "willRetry": event["CompactionWillRetry"]}
                       for event in result["events"] if event["Type"] == "compaction_end"]
    requests = [[{"role": message["role"], "text": message["text"]}
                 for message in request["messages"]]
                for request in result["providerRequests"]]
    return {
        "providerRequests": requests,
        "summaryRequestCount": result["summaryRequests"],
        "overflowCompactions": overflow_events,
        "compactionCount": sum(entry["Type"] == "compaction" for entry in result["activeEntries"]),
        "retryContainsPartialResponse": any(
            "partial output" in message["text"] for message in requests[-1]),
        "retryContainsCompactedSummary": any(
            "compacted history summary" in message["text"] for message in requests[-1]),
        "continuedResponsePersisted": any(
            message["role"] == "assistant" and message["text"] == "continued response"
            for message in result["activeMessages"]),
    }


def artifact(path: Path, evidence_dir: Path) -> dict[str, str]:
    return {"path": path.relative_to(evidence_dir).as_posix(), "sha256": sha256(path)}


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    pi_dir = evidence_dir / "pi-c5-length-stop"
    pi_cmd = ["node", str(PI_ORACLE), "--pi", str(pi_path), "--output", str(pi_dir)]
    pi_process = subprocess.run(pi_cmd, cwd=ROOT, capture_output=True, text=True, check=False)
    (evidence_dir / "pi-c5.stdout.log").write_text(pi_process.stdout, encoding="utf-8")
    (evidence_dir / "pi-c5.stderr.log").write_text(pi_process.stderr, encoding="utf-8")
    if pi_process.returncode:
        raise RuntimeError(f"pinned Pi oracle exited {pi_process.returncode}: {pi_process.stderr}")

    pisharp_paths = [evidence_dir / f"pisharp-c5-run-{index}.json" for index in (1, 2)]
    for path in pisharp_paths:
        command = ["dotnet", "run", "--project", str(CS_PROJECT), "--no-restore", "--", str(path)]
        process = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, check=False,
                                 env={**__import__("os").environ, "DOTNET_PROCESSOR_COUNT": "4"})
        path.with_suffix(".stdout.log").write_text(process.stdout, encoding="utf-8")
        path.with_suffix(".stderr.log").write_text(process.stderr, encoding="utf-8")
        if process.returncode:
            raise RuntimeError(f"PiSharp oracle exited {process.returncode}: {process.stderr}")

    pi_run_1 = read_json(pi_dir / "pi-run-1.json")
    pi_run_2 = read_json(pi_dir / "pi-run-2.json")
    pisharp_1 = read_json(pisharp_paths[0])
    pisharp_2 = read_json(pisharp_paths[1])
    pi_normalized = compact_pi(pi_run_1)
    pisharp_normalized = compact_pisharp(pisharp_1)
    if pi_run_1 != pi_run_2:
        raise RuntimeError("pinned Pi produced different observations across its two runs")
    if pisharp_1 != pisharp_2:
        raise RuntimeError("PiSharp produced different observations across its two runs")
    if pi_normalized != pisharp_normalized:
        raise RuntimeError("paired C5 observations differ:\n" + json.dumps({
            "pi": pi_normalized, "pisharp": pisharp_normalized,
        }, indent=2))
    if pi_normalized["retryContainsPartialResponse"]:
        raise RuntimeError("recovery retry replayed the failed partial response")
    if not pi_normalized["retryContainsCompactedSummary"] or not pi_normalized["continuedResponsePersisted"]:
        raise RuntimeError("recovery retry did not use compacted context and complete")
    if pi_normalized["overflowCompactions"] != [{"reason": "overflow", "willRetry": True}]:
        raise RuntimeError("recovery did not perform exactly one overflow compaction with retry")

    pi_normalized_path = evidence_dir / "pi-c5-normalized.json"
    pisharp_normalized_path = evidence_dir / "pisharp-c5-normalized.json"
    pi_normalized_path.write_text(json.dumps(pi_normalized, indent=2) + "\n", encoding="utf-8")
    pisharp_normalized_path.write_text(json.dumps(pisharp_normalized, indent=2) + "\n", encoding="utf-8")
    paths = [pi_dir / "pi-run-1.json", pi_dir / "pi-run-2.json", pi_dir / "pi-oracle.json",
             evidence_dir / "pi-c5.stdout.log", evidence_dir / "pi-c5.stderr.log",
             *pisharp_paths,
             *(path.with_suffix(".stdout.log") for path in pisharp_paths),
             *(path.with_suffix(".stderr.log") for path in pisharp_paths),
             pi_normalized_path, pisharp_normalized_path]
    raw_artifacts = [artifact(path, evidence_dir) for path in paths]

    def case_observations(normalized: dict[str, Any]) -> dict[str, Any]:
        return {
            CASE_IDS[0]: {
                "providerAttempts": len(normalized["providerRequests"]),
                "overflowCompactions": normalized["overflowCompactions"],
                "summaryRequestCount": normalized["summaryRequestCount"],
                "continuationCompleted": normalized["continuedResponsePersisted"],
            },
            CASE_IDS[1]: {
                "retryMessages": normalized["providerRequests"][-1],
                "partialResponseOmitted": not normalized["retryContainsPartialResponse"],
                "compactedSummaryIncluded": normalized["retryContainsCompactedSummary"],
            },
        }

    pi_observation_values = case_observations(pi_normalized)
    pisharp_observation_values = case_observations(pisharp_normalized)
    pi_observations = evidence_dir / "pi-c5-observations.json"
    pisharp_observations = evidence_dir / "pisharp-c5-observations.json"
    pi_observations.write_text(json.dumps({"observations": pi_observation_values}, indent=2) + "\n", encoding="utf-8")
    pisharp_observations.write_text(json.dumps({"observations": pisharp_observation_values}, indent=2) + "\n", encoding="utf-8")
    raw_artifacts.extend([artifact(pi_observations, evidence_dir), artifact(pisharp_observations, evidence_dir)])

    contract = read_json(HERE / "contract.json")
    expected = {case["id"]: case["expectedObservation"]
                for checkpoint in contract["checkpoints"] if checkpoint["id"] == "C5"
                for case in checkpoint["requiredCases"]}
    case_results = []
    for case_id in CASE_IDS:
        pointer = f"/observations/{case_id}"
        observation = expected[case_id]
        baseline_value = pi_observation_values[case_id]
        implementation_value = pisharp_observation_values[case_id]
        case_results.append({
            "caseId": case_id,
            "checkpointId": "C5",
            "contractExpectedObservation": observation,
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "equivalent",
            "assertions": [{
                "name": "paired-normalized-observation",
                "caseId": case_id,
                "contractObservation": observation,
                "comparison": "equivalent",
                "baselineArtifact": pi_observations.name,
                "baselinePointer": pointer,
                "implementationArtifact": pisharp_observations.name,
                "implementationPointer": pointer,
                "expected": baseline_value,
                "actual": implementation_value,
                "normalizedExpected": baseline_value,
                "normalizedActual": implementation_value,
                "passed": True,
            }],
            "rawArtifacts": raw_artifacts,
        })

    report = {
        "piSha": json.loads((pi_dir / "pi-oracle.json").read_text(encoding="utf-8"))["piSha"],
        "pisharpSourceSha": git("rev-parse", "HEAD") if source_tree_is_clean() else None,
        "sourceTreeClean": source_tree_is_clean(),
        "piCalibrationStable": True,
        "pisharpCalibrationStable": True,
        "pairedObservationsEqual": True,
        "normalization": ["remove empty system message from provider transcript"],
        "classification": "equivalent",
        "completedCaseIds": list(CASE_IDS),
        "executedCaseCount": len(case_results),
        "requiredC5CaseCount": len(expected),
        "unexecutedCaseIds": sorted(set(expected) - set(CASE_IDS)),
        "cases": case_results,
    }
    report_path = evidence_dir / "c5-case-results.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        report = run(args.pi.resolve(), args.evidence_dir.resolve())
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        print(f"C5 differential failed: {error}", file=sys.stderr)
        return 1
    print(f"C5 differential: {report['executedCaseCount']}/{report['requiredC5CaseCount']} cases; "
          f"Pi and PiSharp stable, paired observations equivalent")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
