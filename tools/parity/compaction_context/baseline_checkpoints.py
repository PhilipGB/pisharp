#!/usr/bin/env python3
"""Capture prerequisite, inventory, and verifier-control checkpoints."""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any

import verify

ROOT = Path(__file__).resolve().parents[3]
INVENTORY = ROOT / "docs/parity/fixtures/compaction-context-management-inventory.json"
PUBLISHED_EVIDENCE = ROOT / "artifacts/parity/managed-packages-final-a760d65-stable/evidence.json"
REVIEWED_PR27_HEAD = "2f88019cd8840b2468b1957217ae361e0bab3ad6"
PR27_MERGE = "12064420179f4119bdaf37479c7cba0f5ca40717"
PR27_PUSH_RUN = 40617744882
PR27_PR_RUN = 40617750121

V1_TESTS = {
    "V1.positive-control-valid-evidence-accepted": ["test_valid_positive_control_is_accepted"],
    "V1.negative-altered-pi-pin-rejected": ["test_altered_pi_source_pin_is_rejected"],
    "V1.negative-mutated-provider-request-rejected": ["test_mutated_provider_request_is_rejected"],
    "V1.negative-incorrect-cut-point-rejected": ["test_incorrect_cut_point_is_rejected"],
    "V1.negative-dropped-tool-call-rejected": ["test_dropped_tool_call_is_rejected"],
    "V1.negative-duplicated-tool-call-rejected": ["test_duplicated_tool_call_is_rejected"],
    "V1.negative-wrong-branch-rejected": ["test_wrong_branch_selection_is_rejected"],
    "V1.negative-missing-usage-or-cost-rejected": [
        "test_missing_usage_is_rejected", "test_missing_cost_is_rejected"],
    "V1.negative-wrong-cancellation-rejected": ["test_incorrect_cancellation_is_rejected"],
    "V1.negative-missing-case-rejected": ["test_missing_required_case_is_rejected"],
    "V1.negative-skipped-case-rejected": ["test_skipped_required_case_is_rejected"],
    "V1.negative-stale-source-evidence-rejected": ["test_stale_implementation_source_is_rejected"],
    "V1.negative-failed-ci-rejected": ["test_failed_ci_result_is_rejected"],
    "V1.negative-wrong-head-ci-rejected": ["test_wrong_head_ci_result_is_rejected"],
}


def run_capture(name: str, command: list[str], evidence_dir: Path) -> tuple[Any, list[str]]:
    output_dir = evidence_dir / "commands"
    output_dir.mkdir(parents=True, exist_ok=True)
    process = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, check=False)
    stdout_path = output_dir / f"{name}.stdout.log"
    stderr_path = output_dir / f"{name}.stderr.log"
    stdout_path.write_text(process.stdout, encoding="utf-8")
    stderr_path.write_text(process.stderr, encoding="utf-8")
    if process.returncode:
        raise RuntimeError(f"{name} exited {process.returncode}: {process.stderr}\n{process.stdout}")
    return process, [str(stdout_path.relative_to(evidence_dir)), str(stderr_path.relative_to(evidence_dir))]


def command_json(name: str, command: list[str], evidence_dir: Path) -> tuple[dict[str, Any], list[str]]:
    process, paths = run_capture(name, command, evidence_dir)
    return json.loads(process.stdout), paths


def git_text(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()


def artifact(path: Path, evidence_dir: Path) -> dict[str, str]:
    return {
        "path": str(path.relative_to(evidence_dir)),
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
    }


def validate_p0(evidence_dir: Path) -> tuple[dict[str, Any], list[str]]:
    pr, pr_logs = command_json("pr-27", ["gh", "pr", "view", "27", "--json",
        "number,url,state,isDraft,mergedAt,mergeCommit,headRefOid,baseRefName"], evidence_dir)
    push, push_logs = command_json(f"run-{PR27_PUSH_RUN}", ["gh", "run", "view", str(PR27_PUSH_RUN),
        "--json", "databaseId,url,headSha,status,conclusion,event,jobs,workflowName"], evidence_dir)
    pull_request, pr_run_logs = command_json(f"run-{PR27_PR_RUN}", ["gh", "run", "view", str(PR27_PR_RUN),
        "--json", "databaseId,url,headSha,status,conclusion,event,jobs,workflowName"], evidence_dir)
    _, fetch_logs = run_capture("fetch-main", ["git", "fetch", "origin", "main"], evidence_dir)
    main_sha = git_text("rev-parse", "FETCH_HEAD")
    ancestry = {}
    for label, sha in (("reviewedHead", REVIEWED_PR27_HEAD), ("mergeCommit", PR27_MERGE)):
        result = subprocess.run(["git", "merge-base", "--is-ancestor", sha, main_sha],
                                cwd=ROOT, capture_output=True)
        ancestry[label] = result.returncode == 0

    published = json.loads(PUBLISHED_EVIDENCE.read_text(encoding="utf-8"))
    evidence_counts = {
        "checkpointCount": len(published.get("checkpointResults", [])),
        "caseCount": len(published.get("cases", [])),
        "passedCheckpointCount": sum(item.get("status") == "passed"
                                      for item in published.get("checkpointResults", [])),
        "passedCaseCount": sum(item.get("passed") is True for item in published.get("cases", [])),
        "skippedCaseCount": sum(item.get("skipped") is True for item in published.get("cases", [])),
        "executionExitCode": published.get("executionExitCode"),
    }
    shutil.copy2(PUBLISHED_EVIDENCE, evidence_dir / "published-pr27-evidence.json")
    runs = [push, pull_request]
    workflow_ok = all(run.get("headSha") == REVIEWED_PR27_HEAD and run.get("status") == "completed" and
                      run.get("conclusion") == "success" and run.get("event") == event and
                      bool(run.get("jobs")) and all(job.get("conclusion") == "success" for job in run["jobs"])
                      for run, event in zip(runs, ("push", "pull_request"), strict=True))
    actual = {
        "reviewedHead": pr.get("headRefOid"),
        "mergeCommit": (pr.get("mergeCommit") or {}).get("oid"),
        "prMergedToMain": pr.get("state") == "MERGED" and pr.get("baseRefName") == "main" and
            pr.get("isDraft") is False and bool(pr.get("mergedAt")),
        "mainSha": main_sha,
        "mainContainsReviewedHeadAndMerge": all(ancestry.values()),
        "workflowRuns": [{"runId": run["databaseId"], "headSha": run["headSha"],
                           "status": run["status"], "conclusion": run["conclusion"],
                           "event": run["event"], "jobs": run["jobs"]} for run in runs],
        "pushAndPullRequestWorkflowsPassed": workflow_ok,
        "publishedEvidence": evidence_counts,
    }
    logs = [*pr_logs, *push_logs, *pr_run_logs, *fetch_logs, "published-pr27-evidence.json"]
    return actual, logs


def validate_p1(pi_path: Path, evidence_dir: Path) -> tuple[dict[str, Any], list[str]]:
    _, pi_logs = run_capture("pinned-pi-head", ["git", "-C", str(pi_path), "rev-parse", "HEAD"], evidence_dir)
    _, fetch_logs = run_capture("fetch-main", ["git", "fetch", "origin", "main"], evidence_dir)
    pi_sha = subprocess.run(["git", "-C", str(pi_path), "rev-parse", "HEAD"],
                            capture_output=True, text=True, check=True).stdout.strip()
    merge_base = git_text("merge-base", "HEAD", "FETCH_HEAD") if git_text("rev-parse", "FETCH_HEAD") else ""
    commits = [item for item in git_text("rev-list", "--reverse", f"{PR27_MERGE}..HEAD").splitlines() if item]
    first_parent = git_text("rev-parse", f"{commits[0]}^") if commits else ""
    baseline_tree = git_text("rev-parse", f"{PR27_MERGE}^{{tree}}")
    baseline_project = subprocess.run(["git", "show", f"{PR27_MERGE}:src/PiSharp.Runtime/PiSharp.Runtime.csproj"],
                                      cwd=ROOT, capture_output=True, text=True, check=True).stdout
    import re
    package_match = re.search(r'<PackageReference\s+Include="Microsoft\.Agents\.AI"\s+Version="([^"]+)"',
                              baseline_project)
    assets = json.loads((ROOT / "tests/PiSharp.Tests/obj/project.assets.json").read_text(encoding="utf-8"))
    resolved_maf = next((name.split("/", 1)[1] for name in assets["libraries"]
                         if name.startswith("Microsoft.Agents.AI/")), None)
    status = git_text("status", "--porcelain")
    actual = {
        "branch": git_text("branch", "--show-current"),
        "branchParentIsActualMerge": first_parent == PR27_MERGE,
        "branchMergeBase": merge_base,
        "piSha": pi_sha,
        "baselineCommit": PR27_MERGE,
        "baselineTree": baseline_tree,
        "baselineMafVersion": package_match.group(1) if package_match else None,
        "resolvedMafVersion": resolved_maf,
        "worktreeClean": status == "",
        "mafVersionRecordedInInventory": json.loads(INVENTORY.read_text(encoding="utf-8"))["framework"]["installedVersion"],
    }
    return actual, [*pi_logs, *fetch_logs]


def validate_v0(pi_path: Path, evidence_dir: Path) -> tuple[dict[str, Any], list[str]]:
    contract = verify.load_contract()
    inventory = json.loads(INVENTORY.read_text(encoding="utf-8"))
    required = {case["id"] for checkpoint in contract["checkpoints"] for case in checkpoint["requiredCases"]}
    records = inventory["records"]
    owner_records = records[:13]
    listed: list[str] = [case_id for record in records for case_id in record.get("cases", [])]
    source_owner_completeness = all(record.get("upstream") and record.get("pisharp") and
                                    record.get("inputs") and record.get("outputs") and record.get("state")
                                    for record in owner_records)
    tests_mapped = all(record.get("existingTests") and record.get("pinnedPiTests") for record in owner_records)
    source_references_exist = True
    missing_source_references = []
    for record in owner_records:
        for side, references in (("pi", record.get("upstream", [])),
                                 ("pisharp", record.get("pisharp", []))):
            for reference in references:
                source_path = reference.split("#", 1)[0]
                if not source_path.startswith(("packages/", "src/", "tests/", "tools/", ".github/")):
                    continue
                full_path = (pi_path / source_path if side == "pi"
                             else ROOT / source_path)
                if not full_path.exists():
                    source_references_exist = False
                    missing_source_references.append(f"{side}:{reference}")
    counts = {case["id"]: len(case["requiredCases"]) for case in contract["checkpoints"]}
    differences = inventory.get("confirmedBaselineDifferences", [])
    actual = {
        "recordCount": len(records),
        "ownerGroups": [record["id"] for record in records],
        "sourceOwnersComplete": source_owner_completeness,
        "inputOutputAndStateMapped": all(record.get("inputs") and record.get("outputs") and record.get("state")
                                          for record in records),
        "caseCoverageCount": len(set(listed)),
        "requiredContractCaseCount": len(required),
        "missingCaseIds": sorted(required - set(listed)),
        "unknownCaseIds": sorted(set(listed) - required),
        "duplicateCaseIds": sorted(case_id for case_id in set(listed) if listed.count(case_id) > 1),
        "existingAndPinnedTestsMapped": tests_mapped,
        "sourceOwnerPathsExist": source_references_exist,
        "missingSourceOwnerPaths": missing_source_references,
        "checkpointCaseCounts": counts,
        "confirmedDifferencesHaveStatusAndEvidence": all(item.get("status") and item.get("evidence") and
                                                         item.get("requiredCases") for item in differences),
        "confirmedDifferenceIds": [item["id"] for item in differences],
        "contractSourceReferencesComplete": all(item.get("sourceReferences") for item in contract["checkpoints"]),
    }
    return actual, []


def validate_v1(evidence_dir: Path) -> tuple[dict[str, Any], list[str]]:
    command = [sys.executable, "-m", "unittest", "discover", "-s",
               "tools/parity/compaction_context", "-p", "test_verifier.py", "-v"]
    process, log_paths = run_capture("verifier-controls", command, evidence_dir)
    output = process.stdout + process.stderr
    actual: dict[str, Any] = {}
    for case_id, methods in V1_TESTS.items():
        actual[case_id] = {
            "methods": methods,
            "passed": all(f"{method} (" in output and f"{method} (" + "test_verifier" in output
                          for method in methods),
        }
    actual["summary"] = "OK" in output and "Ran 20 tests" in output
    log_paths.append("commands/verifier-controls.stdout.log")
    log_paths.append("commands/verifier-controls.stderr.log")
    if not all(value["passed"] for value in actual.values() if isinstance(value, dict)) or not actual["summary"]:
        raise RuntimeError("one or more verifier positive/negative controls were missing or failed")
    return actual, log_paths


def expectations(checkpoint: str) -> dict[str, Any]:
    if checkpoint == "P0":
        return {
            "P0.reviewed-head-is-exact": REVIEWED_PR27_HEAD,
            "P0.actual-merge-sha-is-recorded": PR27_MERGE,
            "P0.main-contains-pr-head-and-merge": True,
            "P0.push-and-pr-ci-pass-on-head": True,
            "P0.published-evidence-has-27-checkpoints-240-cases": {
                "checkpointCount": 27, "caseCount": 240, "passedCheckpointCount": 27,
                "passedCaseCount": 240, "skippedCaseCount": 0, "executionExitCode": 0,
            },
        }
    if checkpoint == "P1":
        return {
            "P1.branch-parent-is-actual-merge": True,
            "P1.pinned-pi-checkout-is-b06c1f2": verify.PINNED_PI_SHA,
            "P1.baseline-tree-and-package-versions-recorded": True,
            "P1.worktree-starts-clean": True,
            "P1.maf-installed-version-is-recorded": "1.21.0",
        }
    if checkpoint == "V0":
        return {
            "V0.manual-and-auto-source-owners": True,
            "V0.cut-point-and-retention-source-owners": True,
            "V0.context-budget-and-provider-overflow-owners": True,
            "V0.tool-loop-and-provider-request-owners": True,
            "V0.summary-generation-and-branch-summary-owners": True,
            "V0.session-jsonl-and-branch-state-owners": True,
            "V0.usage-reasoning-pricing-owners": True,
            "V0.cli-rpc-json-tui-owners": True,
            "V0.extension-hook-owners": True,
            "V0.failure-cancellation-recovery-owners": True,
            "V0.observable-input-output-state-map": True,
            "V0.existing-tests-mapped-by-case": True,
            "V0.confirmed-differences-separated-from-unknowns": True,
            "V0.every-required-behavior-has-a-case": {
                "contractRequiredCaseCount": 211,
                "uniqueCaseCoverageCount": 211,
                "missingCaseIds": [],
                "unknownCaseIds": [],
                "duplicateCaseIds": [],
            },
        }
    if checkpoint == "V1":
        return {case_id: {"passed": True} for case_id in V1_TESTS} | {"V1.test-suite": True}
    raise ValueError(checkpoint)


def observations(checkpoint: str, measured: dict[str, Any]) -> dict[str, Any]:
    if checkpoint == "P0":
        return {
            "P0.reviewed-head-is-exact": measured["reviewedHead"],
            "P0.actual-merge-sha-is-recorded": measured["mergeCommit"],
            "P0.main-contains-pr-head-and-merge": measured["mainContainsReviewedHeadAndMerge"],
            "P0.push-and-pr-ci-pass-on-head": measured["pushAndPullRequestWorkflowsPassed"],
            "P0.published-evidence-has-27-checkpoints-240-cases": measured["publishedEvidence"],
        }
    if checkpoint == "P1":
        return {
            "P1.branch-parent-is-actual-merge": measured["branchParentIsActualMerge"],
            "P1.pinned-pi-checkout-is-b06c1f2": measured["piSha"],
            "P1.baseline-tree-and-package-versions-recorded":
                bool(measured["baselineTree"] and measured["baselineMafVersion"] and measured["resolvedMafVersion"]),
            "P1.worktree-starts-clean": measured["worktreeClean"],
            "P1.maf-installed-version-is-recorded": measured["resolvedMafVersion"],
        }
    if checkpoint == "V0":
        return {
            "V0.manual-and-auto-source-owners": measured["sourceOwnersComplete"] and
                measured["sourceOwnerPathsExist"] and all(name in measured["ownerGroups"] for name in
                ("manual-compaction", "automatic-compaction")),
            "V0.cut-point-and-retention-source-owners": measured["sourceOwnerPathsExist"] and
                "cut-points-retention" in measured["ownerGroups"],
            "V0.context-budget-and-provider-overflow-owners": measured["sourceOwnerPathsExist"] and
                all(name in measured["ownerGroups"] for name in
                    ("context-budgets-estimation", "provider-overflow-retry")),
            "V0.tool-loop-and-provider-request-owners": measured["sourceOwnerPathsExist"] and
                "tool-continuation-projection" in measured["ownerGroups"],
            "V0.summary-generation-and-branch-summary-owners": measured["sourceOwnerPathsExist"] and
                all(name in measured["ownerGroups"] for name in
                    ("summary-generation", "branch-summary-navigation")),
            "V0.session-jsonl-and-branch-state-owners": measured["sourceOwnerPathsExist"] and
                "session-persistence-branches" in measured["ownerGroups"],
            "V0.usage-reasoning-pricing-owners": measured["sourceOwnerPathsExist"] and
                "usage-reasoning-costs" in measured["ownerGroups"],
            "V0.cli-rpc-json-tui-owners": measured["sourceOwnerPathsExist"] and
                "public-surfaces-provider-boundaries" in measured["ownerGroups"],
            "V0.extension-hook-owners": measured["sourceOwnerPathsExist"] and
                "native-extension-hooks" in measured["ownerGroups"],
            "V0.failure-cancellation-recovery-owners": measured["sourceOwnerPathsExist"] and
                "failure-cancellation-recovery" in measured["ownerGroups"],
            "V0.observable-input-output-state-map": measured["inputOutputAndStateMapped"],
            "V0.existing-tests-mapped-by-case": measured["existingAndPinnedTestsMapped"],
            "V0.confirmed-differences-separated-from-unknowns": measured["confirmedDifferencesHaveStatusAndEvidence"],
            "V0.every-required-behavior-has-a-case": {
                "contractRequiredCaseCount": measured["requiredContractCaseCount"],
                "uniqueCaseCoverageCount": measured["caseCoverageCount"],
                "missingCaseIds": measured["missingCaseIds"],
                "unknownCaseIds": measured["unknownCaseIds"],
                "duplicateCaseIds": measured["duplicateCaseIds"],
            },
        }
    if checkpoint == "V1":
        return {case_id: {"passed": measured[case_id]["passed"]} for case_id in V1_TESTS} | {
            "V1.test-suite": measured["summary"]}
    raise ValueError(checkpoint)


def run(checkpoint: str, pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    if checkpoint == "P0":
        measured, log_paths = validate_p0(evidence_dir)
    elif checkpoint == "P1":
        measured, log_paths = validate_p1(pi_path, evidence_dir)
    elif checkpoint == "V0":
        measured, log_paths = validate_v0(pi_path, evidence_dir)
    elif checkpoint == "V1":
        measured, log_paths = validate_v1(evidence_dir)
    else:
        raise ValueError(f"unsupported checkpoint: {checkpoint}")
    expected = expectations(checkpoint)
    actual = observations(checkpoint, measured)
    expected_path = evidence_dir / "expected-observations.json"
    actual_path = evidence_dir / "actual-observations.json"
    measured_path = evidence_dir / "measured-source-evidence.json"
    expected_path.write_text(json.dumps({"observations": expected}, indent=2) + "\n", encoding="utf-8")
    actual_path.write_text(json.dumps({"observations": actual}, indent=2) + "\n", encoding="utf-8")
    measured_path.write_text(json.dumps(measured, indent=2) + "\n", encoding="utf-8")
    expected_cases = {case["id"]: case for item in verify.load_contract()["checkpoints"]
                      if item["id"] == checkpoint for case in item["requiredCases"]}
    case_results = []
    for case_id, case in expected_cases.items():
        passed = expected[case_id] == actual[case_id]
        raw_paths = [expected_path, actual_path, measured_path,
                     *[evidence_dir / path for path in log_paths]]
        raw_artifacts = [artifact(path, evidence_dir) for path in raw_paths]
        case_results.append({
            "caseId": case_id,
            "checkpointId": checkpoint,
            "contractExpectedObservation": case["expectedObservation"],
            "executed": True,
            "passed": passed,
            "skipped": False,
            "classification": "equivalent",
            "assertions": [{
                "name": "source-pinned-checkpoint-observation",
                "caseId": case_id,
                "contractObservation": case["expectedObservation"],
                "comparison": "strict",
                "baselineArtifact": expected_path.name,
                "baselinePointer": f"/observations/{case_id}",
                "implementationArtifact": actual_path.name,
                "implementationPointer": f"/observations/{case_id}",
                "expected": expected[case_id],
                "actual": actual[case_id],
                "passed": passed,
            }],
            "rawArtifacts": raw_artifacts,
        })
    report = {
        "checkpointId": checkpoint,
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": git_text("rev-parse", "HEAD"),
        "sourceTreeClean": git_text("status", "--porcelain") == "",
        "requiredCaseCount": len(expected_cases),
        "executedCaseCount": len(case_results),
        "failedCaseCount": sum(not item["passed"] for item in case_results),
        "skippedCaseCount": 0,
        "caseResultsPath": "case-results.json",
        "status": "passed" if len(case_results) == len(expected_cases) and all(item["passed"] for item in case_results)
            else "incomplete",
        "measuredEvidence": measured,
    }
    (evidence_dir / "case-results.json").write_text(json.dumps(case_results, indent=2) + "\n", encoding="utf-8")
    (evidence_dir / "checkpoint-run.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    counts = verify.validate_case_results(case_results, checkpoint, evidence_dir, verify_artifacts=True)
    print(f"{checkpoint} evidence: {counts['passed']}/{counts['required']} cases; {report['status']}")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkpoint", required=True, choices=("P0", "P1", "V0", "V1"))
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        run(args.checkpoint, args.pi.resolve(), args.evidence_dir.resolve())
    except (OSError, RuntimeError, ValueError, subprocess.CalledProcessError, verify.VerificationError) as error:
        print(f"baseline checkpoint failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
