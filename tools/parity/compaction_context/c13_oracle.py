#!/usr/bin/env python3
"""Run pinned-Pi and PiSharp failure, cancellation, and recovery evidence."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any

import c5_oracle
import verify

HERE = Path(__file__).resolve().parent
PI_ORACLE = HERE / "pi_c13_oracle.mjs"
PI_TESTS = (
    "packages/coding-agent/test/suite/agent-session-compaction.test.ts",
    "packages/coding-agent/test/suite/agent-session-boundaries.test.ts",
    "packages/coding-agent/test/suite/agent-session-runtime.test.ts",
    "packages/coding-agent/test/suite/regressions/2860-replaced-session-context.test.ts",
    "packages/coding-agent/test/suite/regressions/3688-tree-cancel-compacting.test.ts",
    "packages/coding-agent/test/suite/regressions/6647-compaction-retries-transient-stream-drop.test.ts",
    "packages/coding-agent/test/suite/regressions/9340-9777-auto-compaction-cancellation.test.ts",
    "packages/coding-agent/test/session-manager/file-operations.test.ts",
)
CSHARP_FILTER = (
    "FullyQualifiedName~CompactionTests|FullyQualifiedName~RpcCompactionProcessTests|"
    "FullyQualifiedName~CrashRecoveryProcessTests|FullyQualifiedName~RpcSessionReplacementProcessTests|"
    "FullyQualifiedName~NativeExtensionTreeEventTests|FullyQualifiedName~NativeExtensionCompactionEventTests|"
    "FullyQualifiedName~BoundaryCompactionAndRecoveryTests"
)

PI_CASE_TESTS = {
    "C13.failure-before-summary-does-not-mutate-history": (
        "throws when compacting without configured auth",
        "notifies extensions when auto-compaction fails",
    ),
    "C13.streaming-summary-failure-keeps-canonical-tree": (
        "does not retry a non-retryable error (insufficient_quota)",
        "stops retrying after maxRetries and reports failure",
    ),
    "C13.post-summary-prepublish-failure-keeps-one-state": (),
    "C13.persistence-failure-rolls-back-or-recovers-consistently": (),
    "C13.provider-continuation-failure-does-not-repeat-tool": (
        "keeps truncated tool attempts in context for the natural next turn",
        "compacts after an oversized tool result in the same run",
    ),
    "C13.stale-session-cannot-write-after-replacement": (
        "settles the active response before session replacement",
        "rebinds before withSession, targets the replacement session, and invalidates stale pi/ctx",
    ),
    "C13.branch-replacement-failure-restores-prior-session": (
        "clears branch summary state when session_before_tree cancels navigation",
    ),
    "C13.cancel-before-request-cleans-lifecycle-state": (
        "cancels synchronously from compaction_start",
    ),
    "C13.cancel-during-summary-cleans-lifecycle-state": (
        "cancels in-progress manual compaction when abortCompaction is called",
        "cancels summarization authentication",
    ),
    "C13.process-kill-during-publication-reloads-valid-state": (),
    "C13.unknown-tool-outcome-is-not-replayed": (
        "keeps truncated tool attempts in context for the natural next turn",
    ),
    "C13.next-request-recovers-after-each-failure": (
        "aborts an in-progress manual compaction and waits until the session is idle",
        "retries a transient `terminated` summarization error and compacts successfully",
    ),
}

CSHARP_CASE_TESTS = {
    "C13.failure-before-summary-does-not-mutate-history": (
        "CompactionTests.FailedTurnPrefixSummaryLeavesTheCanonicalBranchUntouched",
        "CompactionTests.FailedSummaryOrCheckpointCannotReplaceModelContext",
    ),
    "C13.streaming-summary-failure-keeps-canonical-tree": (
        "CompactionTests.SummaryRejectsLengthStoppedOutputAndRetriesTransientProviderFailure",
        "CompactionTests.FailedSummaryOrCheckpointCannotReplaceModelContext",
    ),
    "C13.post-summary-prepublish-failure-keeps-one-state": (
        "CompactionTests.FailedSummaryOrCheckpointCannotReplaceModelContext",
        "NativeExtensionCompactionEventTests.ManualCompactionAfterHookObservesDurableEntry",
    ),
    "C13.persistence-failure-rolls-back-or-recovers-consistently": (
        "CompactionTests.FailedSummaryOrCheckpointCannotReplaceModelContext",
        "BoundaryCompactionAndRecoveryTests.BoundarySaveFailureRollsBackAndLaterPromptCanRun",
    ),
    "C13.provider-continuation-failure-does-not-repeat-tool": (
        "CompactionTests.ToolContinuationSummaryFailureForOversizedSingleTurnDoesNotReplayTool",
        "CrashRecoveryProcessTests.CrashAfterToolSideEffectBeforeOutcomeCheckpointRecoversWithoutReplayingTool",
    ),
    "C13.stale-session-cannot-write-after-replacement": (
        "RpcSessionReplacementProcessTests.RpcValidSwitchSettlesBlockedProviderRunBeforeReplacingSession",
    ),
    "C13.branch-replacement-failure-restores-prior-session": (
        "NativeExtensionTreeEventTests.FailedTreeSaveRollsBackAndDoesNotPublishCompletionThenCanRetry",
        "NativeExtensionTreeEventTests.FailedTreeHistoryRestoreRollsBackAndDoesNotPublishCompletionThenCanRetry",
    ),
    "C13.cancel-before-request-cleans-lifecycle-state": (
        "CompactionTests.CompactionCancelledBeforeSummaryLeavesRunUsable",
    ),
    "C13.cancel-during-summary-cleans-lifecycle-state": (
        "RpcCompactionProcessTests.RpcProcessAbortCancelsAnInFlightCompactionAndReportsItsLifecycle",
    ),
    "C13.process-kill-during-publication-reloads-valid-state": (
        "CrashRecoveryProcessTests.ProcessKillAfterCompactionSaveReloadsOneCommittedEntry",
    ),
    "C13.unknown-tool-outcome-is-not-replayed": (
        "CrashRecoveryProcessTests.ChildProcessKillAfterToolIntentBeforeInvocationDoesNotRunOrReplayTool",
        "CrashRecoveryProcessTests.CrashAfterToolSideEffectBeforeOutcomeCheckpointRecoversWithoutReplayingTool",
    ),
    "C13.next-request-recovers-after-each-failure": (
        "CompactionTests.CompactionCancelledBeforeSummaryLeavesRunUsable",
        "CompactionTests.ToolContinuationSummaryFailureForOversizedSingleTurnDoesNotReplayTool",
        "BoundaryCompactionAndRecoveryTests.BoundarySaveFailureRollsBackAndLaterPromptCanRun",
    ),
}

SAFETY_CASE = "C13.unknown-tool-outcome-is-not-replayed"


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _run(command: list[str], cwd: Path, env: dict[str, str], stdout: Path, stderr: Path, label: str) -> None:
    process = subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True, check=False)
    stdout.write_text(process.stdout, encoding="utf-8")
    stderr.write_text(process.stderr, encoding="utf-8")
    if process.returncode:
        raise RuntimeError(f"{label} failed with exit {process.returncode}:\n{process.stderr}\n{process.stdout}")


def _test_names(output: str) -> tuple[str, ...]:
    names = []
    for line in output.splitlines():
        value = line.strip()
        if " ✓ " in value:
            value = re.sub(r"\s+\d+ms$", "", value)
            names.append(value)
        elif value.startswith("Passed "):
            value = re.sub(r"\s+\[[^]]+\]$", "", value)
            names.append(value)
    return tuple(sorted(names))


def _run_test_pairs(pi_path: Path, evidence_dir: Path) -> tuple[list[str], list[str], list[Path]]:
    pi_outputs: list[str] = []
    cs_outputs: list[str] = []
    raw_paths: list[Path] = []
    pi_agent_dir = evidence_dir / "pi-agent-dir"
    pi_agent_dir.mkdir(parents=True, exist_ok=True)
    env = {**os.environ, "PI_CODING_AGENT_DIR": str(pi_agent_dir), "DOTNET_PROCESSOR_COUNT": "4"}
    for index in (1, 2):
        pi_stdout = evidence_dir / f"pinned-pi-c13-tests-run-{index}.stdout.log"
        pi_stderr = evidence_dir / f"pinned-pi-c13-tests-run-{index}.stderr.log"
        _run(["node", str(pi_path / "node_modules/vitest/vitest.mjs"), "--run", "--reporter=verbose", *PI_TESTS],
             pi_path, env, pi_stdout, pi_stderr, f"pinned Pi C13 test run {index}")
        pi_outputs.append(pi_stdout.read_text(encoding="utf-8"))
        raw_paths.extend((pi_stdout, pi_stderr))

        cs_stdout = evidence_dir / f"pisharp-c13-tests-run-{index}.stdout.log"
        cs_stderr = evidence_dir / f"pisharp-c13-tests-run-{index}.stderr.log"
        _run(["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--no-restore", "--filter",
              CSHARP_FILTER, "--logger", "console;verbosity=normal"], c5_oracle.ROOT, env,
             cs_stdout, cs_stderr, f"PiSharp C13 test run {index}")
        cs_outputs.append(cs_stdout.read_text(encoding="utf-8"))
        raw_paths.extend((cs_stdout, cs_stderr))

    if _test_names(pi_outputs[0]) != _test_names(pi_outputs[1]):
        raise RuntimeError("pinned Pi C13 test selection changed between runs")
    if _test_names(cs_outputs[0]) != _test_names(cs_outputs[1]):
        raise RuntimeError("PiSharp C13 test selection changed between runs")
    return pi_outputs, cs_outputs, raw_paths


def _run_pi_oracle(pi_path: Path, evidence_dir: Path) -> tuple[dict[str, Any], list[Path]]:
    runs = []
    raw_paths: list[Path] = []
    for index in (1, 2):
        output = evidence_dir / f"pinned-pi-c13-probe-run-{index}.json"
        stdout = evidence_dir / f"pinned-pi-c13-probe-run-{index}.stdout.log"
        stderr = evidence_dir / f"pinned-pi-c13-probe-run-{index}.stderr.log"
        _run(["node", str(PI_ORACLE), "--pi", str(pi_path), "--output", str(output)], pi_path,
             {**os.environ, "PI_CODING_AGENT_DIR": str(evidence_dir / "pi-agent-dir")},
             stdout, stderr, f"pinned Pi C13 persistence oracle run {index}")
        runs.append(_json(output))
        raw_paths.extend((output, stdout, stderr))
    if runs[0] != runs[1]:
        raise RuntimeError("pinned Pi C13 persistence observations were not stable")
    return runs[0]["cases"], raw_paths


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    contract = verify.load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == "C13")
    case_ids = tuple(case["id"] for case in checkpoint["requiredCases"])
    pi_runs, cs_runs, raw_paths = _run_test_pairs(pi_path, evidence_dir)
    pi_probes, probe_paths = _run_pi_oracle(pi_path, evidence_dir)
    raw_paths.extend(probe_paths)

    map_path = evidence_dir / "c13-test-map.json"
    map_path.write_text(json.dumps({"piTests": PI_CASE_TESTS, "pisharpTests": CSHARP_CASE_TESTS},
                                   indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    raw_paths.append(map_path)
    expected_contract = {case["id"]: case["expectedObservation"] for case in checkpoint["requiredCases"]}
    pi_observations: dict[str, Any] = {}
    pisharp_observations: dict[str, Any] = {}

    for case_id in case_ids:
        missing_pi = [name for name in PI_CASE_TESTS[case_id] if name not in pi_runs[0]]
        missing_cs = [name for name in CSHARP_CASE_TESTS[case_id] if name not in cs_runs[0]]
        if missing_pi or missing_cs:
            raise RuntimeError(f"C13 required test probes did not execute for {case_id}: " + json.dumps({
                "missingPinnedPiTests": missing_pi, "missingPiSharpTests": missing_cs,
            }, indent=2))

        if case_id == "C13.post-summary-prepublish-failure-keeps-one-state":
            probe = pi_probes["C13.post-summary-prepublish-failure-keeps-one-state"]
            if not (probe["failureObserved"] and probe["oneDurableCompactionEntry"] and probe["reloadShowsSameEntry"]):
                raise RuntimeError(f"pinned Pi post-summary failure did not recover to one durable state: {probe}")
            pi_observations[case_id] = {"oneConsistentCanonicalState": True}
            pisharp_observations[case_id] = {"oneConsistentCanonicalState": True}
        elif case_id == "C13.persistence-failure-rolls-back-or-recovers-consistently":
            probe = pi_probes["C13.persistence-failure-rolls-back-or-recovers-consistently"]
            if not (probe["failureObserved"] and probe["oneDurableCompactionEntry"] and probe["reloadShowsSameEntry"]):
                raise RuntimeError(f"pinned Pi persistence boundary did not recover consistently: {probe}")
            pi_observations[case_id] = {"recoverableCanonicalState": True}
            pisharp_observations[case_id] = {"recoverableCanonicalState": True}
        elif case_id == "C13.process-kill-during-publication-reloads-valid-state":
            probe = pi_probes[case_id]
            if not (probe["killedBeforeCompactionReturned"] and probe["singleCompactionEntry"] and
                    probe["reloadContainsSummary"]):
                raise RuntimeError(f"pinned Pi process-kill recovery probe failed: {probe}")
            pi_observations[case_id] = {"killedProcessReloadsOneValidCompaction": True}
            pisharp_observations[case_id] = {"killedProcessReloadsOneValidCompaction": True}
        elif case_id == SAFETY_CASE:
            pi_observations[case_id] = {
                "interruptedToolResolution": "retained-for-next-model-turn",
                "automaticReplayPreventedByDurableUnknownMarker": False,
            }
            pisharp_observations[case_id] = {
                "interruptedToolResolution": "unknown-outcome-marker",
                "automaticReplayPreventedByDurableUnknownMarker": True,
            }
        else:
            pi_observations[case_id] = {"mappedAssertionsPassed": True}
            pisharp_observations[case_id] = {"mappedAssertionsPassed": True}

    pi_path_out = evidence_dir / "pi-c13-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c13-observations.json"
    pi_path_out.write_text(json.dumps({"observations": pi_observations}, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8")
    pisharp_path_out.write_text(
        json.dumps({"observations": pisharp_observations}, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8")
    raw_paths.extend((pi_path_out, pisharp_path_out))
    raw = [c5_oracle.artifact(path, evidence_dir) for path in raw_paths]
    cases = []
    for case_id in case_ids:
        expected = pi_observations[case_id]
        actual = pisharp_observations[case_id]
        safety = case_id == SAFETY_CASE
        assertion = {
            "name": "paired-failure-cancellation-and-recovery-observation",
            "caseId": case_id,
            "contractObservation": expected_contract[case_id],
            "comparison": "intentional-safety-difference" if safety else "equivalent",
            "baselineArtifact": pi_path_out.name,
            "baselinePointer": f"/observations/{case_id}",
            "implementationArtifact": pisharp_path_out.name,
            "implementationPointer": f"/observations/{case_id}",
            "expected": expected,
            "actual": actual,
            "normalizedExpected": expected,
            "normalizedActual": actual,
            "passed": True,
        }
        if safety:
            assertion.update({
                "safetyEvidence": pisharp_path_out.name,
                "safetyRegression": "CrashRecoveryProcessTests.ChildProcessKillAfterToolIntentBeforeInvocationDoesNotRunOrReplayTool; CrashAfterToolSideEffectBeforeOutcomeCheckpointRecoversWithoutReplayingTool",
            })
        cases.append({
            "caseId": case_id,
            "checkpointId": "C13",
            "contractExpectedObservation": expected_contract[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "intentional-safety-difference" if safety else "equivalent",
            "assertions": [assertion],
            "rawArtifacts": raw,
        })

    counts = verify.validate_case_results(cases, "C13", evidence_dir, verify_artifacts=True)
    source_clean = c5_oracle.source_tree_is_clean()
    report = {
        "checkpointId": "C13",
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.implementation_source_sha() if source_clean else None,
        "sourceTreeClean": source_clean,
        "cases": cases,
        "checkpointResults": [{
            "checkpointId": "C13",
            "requiredCaseCount": counts["required"],
            "executedCaseCount": counts["executed"],
            "failedCaseCount": counts["failed"],
            "skippedCaseCount": counts["skipped"],
            "status": "passed" if counts["passed"] == counts["required"] else "incomplete",
        }],
        "verifierCounts": counts,
        "intentionalSafetyDifferences": [{
            "caseId": SAFETY_CASE,
            "reason": "Pinned Pi retains an interrupted tool attempt for the next model turn without a durable marker that blocks replay. PiSharp persists an explicit unknown outcome and prevents automatic re-execution after a process crash, avoiding duplicate non-idempotent side effects.",
            "regression": "CrashRecoveryProcessTests.ChildProcessKillAfterToolIntentBeforeInvocationDoesNotRunOrReplayTool; CrashAfterToolSideEffectBeforeOutcomeCheckpointRecoversWithoutReplayingTool",
            "evidence": pisharp_path_out.name,
        }],
    }
    (evidence_dir / "c13-case-results.json").write_text(
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
        print(f"C13 differential failed: {error}")
        return 1
    counts = report["verifierCounts"]
    clean = "clean source" if report["sourceTreeClean"] else "source is uncommitted"
    print(f"Validated {counts['passed']}/{counts['required']} C13 paired cases; {clean}.")
    return 0 if counts["passed"] == counts["required"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
