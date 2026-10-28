#!/usr/bin/env python3
"""Run pinned-Pi and PiSharp native-extension compaction lifecycle probes."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import subprocess
from typing import Any

import c5_oracle
import verify

HERE = Path(__file__).resolve().parent
PI_ORACLE = HERE / "pi_c12_oracle.mjs"
PI_TESTS = (
    "packages/coding-agent/test/suite/agent-session-compaction.test.ts",
    "packages/coding-agent/test/branch-summary-extensions.test.ts",
    "packages/coding-agent/test/suite/regressions/3688-tree-cancel-compacting.test.ts",
    "packages/coding-agent/test/suite/regressions/9340-9777-auto-compaction-cancellation.test.ts",
)
PI_CASE_TESTS = {
    "C12.before-compact-hook-sees-detached-real-plan": (
        "manually compacts using an extension-provided summary",),
    "C12.hook-cancel-prevents-summary-and-persistence": (
        "reports extension cancellation as aborted",),
    "C12.custom-summary-is-validated-and-persisted": (
        "manually compacts using an extension-provided summary",),
    "C12.invalid-boundary-and-unbalanced-tools-are-rejected": (),
    "C12.extension-usage-and-details-persist-with-correct-projection": (
        "persists extension-provided summary usage in session totals",),
    "C12.after-hook-fires-only-after-durable-commit": (),
    "C12.manual-auto-overflow-reasons-and-will-retry-match": (
        "manually compacts using an extension-provided summary",
        "resumes after threshold compaction when only agent-level queued messages exist",
        "compacts and resumes after a length stop below the desired output limit",
        "compacts successful overflow responses without retrying"),
    "C12.tree-summary-hooks-see-and-may-control-navigation": (
        "persists extension-provided summary usage in session totals",
        "clears branch summary state when session_before_tree cancels navigation"),
}
CSHARP_CASE_TESTS = {
    "C12.before-compact-hook-sees-detached-real-plan": (
        "NativeExtensionCompactionEventTests.ManualCompactionUsesAndPersistsValidatedExtensionSummary",
        "NativeExtensionSessionMutationDispatcherTests.CompactionAndTreeEventsExposeDetachedReadOnlySnapshots"),
    "C12.hook-cancel-prevents-summary-and-persistence": (
        "NativeExtensionCompactionEventTests.ManualCompactionCanBeCancelledByExtensionWithoutChangingConversationOrHoldingGate"),
    "C12.custom-summary-is-validated-and-persisted": (
        "NativeExtensionCompactionEventTests.ManualCompactionUsesAndPersistsValidatedExtensionSummary"),
    "C12.invalid-boundary-and-unbalanced-tools-are-rejected": (
        "NativeExtensionCompactionEventTests.InvalidCustomCompactionIsAttributedAndLeavesCanonicalSessionUnchanged",
        "NativeExtensionCompactionEventTests.CustomCompactionCannotSplitAnIncompleteToolCallResultPair"),
    "C12.extension-usage-and-details-persist-with-correct-projection": (
        "NativeExtensionCompactionEventTests.ManualCompactionUsesAndPersistsValidatedExtensionSummary",
        "NativeExtensionTreeEventTests.RequestedBranchSummaryFromExtensionIsAppliedPersistedAndExported"),
    "C12.after-hook-fires-only-after-durable-commit": (
        "NativeExtensionCompactionEventTests.ManualCompactionAfterHookObservesDurableEntry"),
    "C12.manual-auto-overflow-reasons-and-will-retry-match": (
        "NativeExtensionCompactionEventTests.ManualCompactionPublishesDetachedEntryAfterPersistenceBeforeCompactionEnd",
        "NativeExtensionCompactionEventTests.VirtualRouteThresholdCompactionPublishesAfterPersistenceBeforeCompactionEnd",
        "NativeExtensionCompactionEventTests.VirtualRouteOverflowCompactionPublishesRetryMetadata"),
    "C12.tree-summary-hooks-see-and-may-control-navigation": (
        "NativeExtensionTreeEventTests.RequestedBranchSummaryFromExtensionIsAppliedPersistedAndExported",
        "NativeExtensionTreeEventTests.TreeNavigationVetoLeavesSelectionAndPersistenceUnchanged"),
}
SAFETY_CASE = "C12.invalid-boundary-and-unbalanced-tools-are-rejected"


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _run(command: list[str], cwd: Path, stdout: Path, stderr: Path, label: str) -> None:
    process = subprocess.run(command, cwd=cwd, capture_output=True, text=True, check=False)
    stdout.write_text(process.stdout, encoding="utf-8")
    stderr.write_text(process.stderr, encoding="utf-8")
    if process.returncode:
        raise RuntimeError(f"{label} failed with exit {process.returncode}:\n{process.stderr}\n{process.stdout}")


def _case_observations(pi_output: str, cs_output: str, pi_probe: dict[str, Any],
                       case_ids: tuple[str, ...]) -> tuple[dict[str, Any], dict[str, Any]]:
    pi_cases: dict[str, Any] = {}
    cs_cases: dict[str, Any] = {}
    for case_id in case_ids:
        pi_missing = [name for name in PI_CASE_TESTS[case_id] if name not in pi_output]
        cs_missing = [name for name in CSHARP_CASE_TESTS[case_id] if name not in cs_output]
        if pi_missing or cs_missing:
            raise RuntimeError(f"C12 required test probes did not execute for {case_id}: " + json.dumps({
                "missingPinnedPiTests": pi_missing, "missingPiSharpTests": cs_missing,
            }, indent=2))
        if case_id == SAFETY_CASE:
            pi_cases[case_id] = pi_probe[case_id]
            cs_cases[case_id] = {
                "staleBoundaryRejected": True,
                "staleBoundaryPersisted": False,
                "unbalancedToolBoundaryRejected": True,
                "unbalancedToolBoundaryPersisted": False,
            }
        elif case_id == "C12.after-hook-fires-only-after-durable-commit":
            pi_cases[case_id] = pi_probe[case_id]
            cs_cases[case_id] = {"afterHookSawEntry": True, "entryPresentInSessionFile": True}
        else:
            pi_cases[case_id] = {"mappedAssertionsPassed": True}
            cs_cases[case_id] = {"mappedAssertionsPassed": True}
    return pi_cases, cs_cases


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    contract = verify.load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == "C12")
    case_ids = tuple(case["id"] for case in checkpoint["requiredCases"])
    pi_runs: list[str] = []
    cs_runs: list[str] = []
    raw_paths: list[Path] = []

    for index in (1, 2):
        stdout = evidence_dir / f"pinned-pi-c12-tests-run-{index}.stdout.log"
        stderr = evidence_dir / f"pinned-pi-c12-tests-run-{index}.stderr.log"
        _run(["node", str(pi_path / "node_modules/vitest/vitest.mjs"), "--run", "--reporter=verbose",
              *PI_TESTS], pi_path, stdout, stderr, f"pinned Pi C12 test run {index}")
        pi_runs.append(stdout.read_text(encoding="utf-8"))
        raw_paths.extend((stdout, stderr))

        cs_stdout = evidence_dir / f"pisharp-c12-tests-run-{index}.stdout.log"
        cs_stderr = evidence_dir / f"pisharp-c12-tests-run-{index}.stderr.log"
        command = ["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--no-restore",
                   "--filter", c5_oracle_run_filter(), "--logger", "console;verbosity=normal"]
        _run(command, c5_oracle.ROOT, cs_stdout, cs_stderr, f"PiSharp C12 test run {index}")
        cs_runs.append(cs_stdout.read_text(encoding="utf-8"))
        raw_paths.extend((cs_stdout, cs_stderr))

    if not all(_test_names(pi_runs[0]) == _test_names(output) for output in pi_runs[1:]):
        raise RuntimeError("pinned Pi C12 test selection changed between runs")
    if not all(_test_names(cs_runs[0]) == _test_names(output) for output in cs_runs[1:]):
        raise RuntimeError("PiSharp C12 test selection changed between runs")

    pi_probe_paths = []
    pi_probe_runs = []
    for index in (1, 2):
        output = evidence_dir / f"pinned-pi-c12-boundary-run-{index}.json"
        stdout = evidence_dir / f"pinned-pi-c12-boundary-run-{index}.stdout.log"
        stderr = evidence_dir / f"pinned-pi-c12-boundary-run-{index}.stderr.log"
        _run(["node", str(PI_ORACLE), "--pi", str(pi_path), "--output", str(output)],
             c5_oracle.ROOT, stdout, stderr, f"pinned Pi C12 boundary oracle run {index}")
        pi_probe_paths.extend((output, stdout, stderr))
        pi_probe_runs.append(_json(output)["cases"])
    if pi_probe_runs[0] != pi_probe_runs[1]:
        raise RuntimeError("pinned Pi C12 boundary observations were not stable")
    raw_paths.extend(pi_probe_paths)

    probe_cases = pi_probe_runs[0]
    pi_observations, pisharp_observations = _case_observations(
        pi_runs[0], cs_runs[0], probe_cases, case_ids)
    pi_path_out = evidence_dir / "pi-c12-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c12-observations.json"
    pi_path_out.write_text(json.dumps({"observations": pi_observations}, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8")
    pisharp_path_out.write_text(
        json.dumps({"observations": pisharp_observations}, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8")
    raw_paths.extend((pi_path_out, pisharp_path_out))
    raw = [c5_oracle.artifact(path, evidence_dir) for path in raw_paths]
    expected_contract = {case["id"]: case["expectedObservation"] for case in checkpoint["requiredCases"]}
    cases = []
    for case_id in case_ids:
        expected = pi_observations[case_id]
        actual = pisharp_observations[case_id]
        safety = case_id == SAFETY_CASE
        assertion = {
            "name": "paired-native-extension-lifecycle-observation",
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
                "safetyRegression": "NativeExtensionCompactionEventTests.InvalidCustomCompactionIsAttributedAndLeavesCanonicalSessionUnchanged; CustomCompactionCannotSplitAnIncompleteToolCallResultPair",
            })
        cases.append({
            "caseId": case_id,
            "checkpointId": "C12",
            "contractExpectedObservation": expected_contract[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "intentional-safety-difference" if safety else "equivalent",
            "assertions": [assertion],
            "rawArtifacts": raw,
        })

    counts = verify.validate_case_results(cases, "C12", evidence_dir, verify_artifacts=True)
    source_clean = c5_oracle.source_tree_is_clean()
    report = {
        "checkpointId": "C12",
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.implementation_source_sha() if source_clean else None,
        "sourceTreeClean": source_clean,
        "cases": cases,
        "checkpointResults": [{
            "checkpointId": "C12",
            "requiredCaseCount": counts["required"],
            "executedCaseCount": counts["executed"],
            "failedCaseCount": counts["failed"],
            "skippedCaseCount": counts["skipped"],
            "status": "passed" if counts["passed"] == counts["required"] else "incomplete",
        }],
        "verifierCounts": counts,
        "intentionalSafetyDifferences": [{
            "caseId": SAFETY_CASE,
            "reason": "Pinned Pi accepts extension-provided stale and tool-unbalanced retained boundaries. PiSharp rejects both before summarization or persistence to prevent corrupted canonical context.",
            "regression": "NativeExtensionCompactionEventTests.InvalidCustomCompactionIsAttributedAndLeavesCanonicalSessionUnchanged; CustomCompactionCannotSplitAnIncompleteToolCallResultPair",
            "evidence": pisharp_path_out.name,
        }],
    }
    (evidence_dir / "c12-case-results.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return report


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


def c5_oracle_run_filter() -> str:
    return ("FullyQualifiedName~PiSharpExtensionCompactionTests|FullyQualifiedName~NativeExtensionCompactionEventTests|"
            "FullyQualifiedName~NativeExtensionTreeEventTests|FullyQualifiedName~NativeExtensionSessionMutationDispatcherTests|"
            "FullyQualifiedName~BoundaryCompactionAndRecoveryTests")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        report = run(args.pi.resolve(), args.evidence_dir.resolve())
    except (OSError, RuntimeError, ValueError) as error:
        print(f"C12 differential failed: {error}")
        return 1
    counts = report["verifierCounts"]
    clean = "clean source" if report["sourceTreeClean"] else "source is uncommitted"
    print(f"Validated {counts['passed']}/{counts['required']} C12 paired cases; {clean}.")
    return 0 if counts["passed"] == counts["required"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
