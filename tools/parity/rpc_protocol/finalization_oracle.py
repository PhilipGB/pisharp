#!/usr/bin/env python3
"""Source-bound evidence and publication-finalizer checkpoint runners."""
from __future__ import annotations

import hashlib
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

from source_oracle import _capture, _check, _observed
from verify import (
    BASE_SHA,
    FROZEN_CONTRACT_SHA256,
    PINNED_PI_SHA,
    VerificationError,
    load_contract,
    required_ci_jobs,
    validate_q0_authoritative_counts,
    validate_case_results,
    validate_clean_source_tree,
    validate_pi_checkout,
)

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
E0_CHILD_CHECKPOINTS = ("P0", "P1", "V0", "V1", *[f"R{i}" for i in range(1, 16)], "D0", "Q0")
LEDGER_PATHS = (
    "TODO.md", "docs/continuation.md", "docs/parity/feature-matrix.md", "docs/parity/execution-ledger.json",
)
CI0_CASE_IDS = (
    "CI0.push-workflow-exact-head", "CI0.pull-request-workflow-exact-head",
    "CI0.workflow-base-and-merge-base", "CI0.job-completeness", "CI0.no-duplicate-manual-dispatch",
)
PUB0_CASE_IDS = (
    "PUB0.draft-pr-open-unmerged", "PUB0.pr-description-evidence",
    "PUB0.publication-verifier-current-evidence", "PUB0.e0-ci0-pub0-independent",
    "PUB0.single-contributor-review-state", "PUB0.final-source-and-pin-report",
)
RPC_EVIDENCE_WORKFLOW = "RPC parity evidence"
RPC_EVIDENCE_JOB = "generate-and-publish"


def _run(argv: list[str]) -> dict[str, Any]:
    result = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True)
    return {"argv": argv, "cwd": str(ROOT), "exitCode": result.returncode,
            "stdout": result.stdout, "stderr": result.stderr}


def _gh_json(argv: list[str]) -> tuple[Any, dict[str, Any]]:
    result = _run(["gh", *argv])
    if result["exitCode"] != 0:
        raise VerificationError(f"GitHub query failed: {' '.join(argv)}: {result['stderr'].strip()}")
    try:
        return json.loads(result["stdout"]), result
    except json.JSONDecodeError as error:
        raise VerificationError(f"GitHub query returned invalid JSON: {' '.join(argv)}") from error


def _current_pull_request(branch: str) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    pr_list, list_capture = _gh_json([
        "pr", "list", "--state", "open", "--head", branch, "--json", "number,url",
    ])
    if not isinstance(pr_list, list) or len(pr_list) != 1:
        raise VerificationError(f"expected one open PR for {branch}, observed {len(pr_list) if isinstance(pr_list, list) else 'invalid'}")
    pr, pull_capture = _gh_json(["api", f"repos/PhilipGB/pisharp/pulls/{pr_list[0]['number']}"])
    return pr, [list_capture, pull_capture]


def _latest_exact_run(event: str, branch: str, head_sha: str,
                      workflow_name: str = "Linux CI") -> tuple[dict[str, Any] | None, list[dict[str, Any]]]:
    runs, list_capture = _gh_json([
        "run", "list", "--branch", branch, "--event", event, "--limit", "50",
        "--json", "databaseId,event,headSha,conclusion,createdAt,headBranch,workflowName",
    ])
    exact = [run for run in runs if run.get("headSha") == head_sha and run.get("event") == event
             and run.get("workflowName") == workflow_name]
    if not exact:
        return None, [list_capture]
    latest = max(exact, key=lambda run: run.get("createdAt", ""))
    detail, detail_capture = _gh_json([
        "run", "view", str(latest["databaseId"]), "--json", "databaseId,event,headSha,conclusion,jobs",
    ])
    return detail, [list_capture, detail_capture]


def _latest_rpc_evidence_run(branch: str, head_sha: str) -> tuple[dict[str, Any] | None, list[dict[str, Any]]]:
    runs, list_capture = _gh_json([
        "run", "list", "--branch", branch, "--event", "push", "--limit", "100",
        "--json", "databaseId,event,headSha,conclusion,createdAt,workflowName",
    ])
    exact = [run for run in runs if run.get("headSha") == head_sha and run.get("event") == "push"
             and run.get("workflowName") == RPC_EVIDENCE_WORKFLOW]
    if not exact:
        return None, [list_capture]

    latest = max(exact, key=lambda run: run.get("createdAt", ""))
    run_id = str(latest["databaseId"])
    detail, detail_capture = _gh_json([
        "run", "view", run_id, "--json", "databaseId,event,headSha,conclusion,workflowName,jobs",
    ])
    artifacts, artifacts_capture = _gh_json([
        "api", f"repos/PhilipGB/pisharp/actions/runs/{run_id}/artifacts",
    ])
    artifact_name = f"rpc-parity-evidence-{head_sha}"
    matches = [item for item in artifacts.get("artifacts", []) if item.get("name") == artifact_name]
    artifact = matches[0] if len(matches) == 1 else None
    workflow_artifact = None
    if artifact is not None:
        workflow_artifact = artifact.get("workflow_run") or {}
    jobs = {job.get("name"): job.get("conclusion") for job in detail.get("jobs", [])}
    record = {
        "workflowName": detail.get("workflowName"), "event": detail.get("event"),
        "runId": run_id, "headSha": detail.get("headSha"),
        "conclusion": detail.get("conclusion"), "jobs": jobs,
        "url": f"https://github.com/PhilipGB/pisharp/actions/runs/{run_id}",
        "artifact": None if artifact is None else {
            "artifactId": artifact.get("id"), "name": artifact.get("name"),
            "digest": artifact.get("digest"), "sizeInBytes": artifact.get("size_in_bytes"),
            "expired": artifact.get("expired"), "expiresAt": artifact.get("expires_at"),
            "headSha": workflow_artifact.get("head_sha"),
        },
    }
    return record, [list_capture, detail_capture, artifacts_capture]


def _rpc_evidence_artifact_passes(record: Any, head_sha: str) -> bool:
    if not isinstance(record, dict) or record.get("workflowName") != RPC_EVIDENCE_WORKFLOW:
        return False
    artifact = record.get("artifact")
    return bool(
        record.get("event") == "push" and record.get("headSha") == head_sha
        and record.get("conclusion") == "success"
        and record.get("jobs") == {RPC_EVIDENCE_JOB: "success"}
        and isinstance(artifact, dict)
        and artifact.get("name") == f"rpc-parity-evidence-{head_sha}"
        and artifact.get("headSha") == head_sha and artifact.get("expired") is False
        and type(artifact.get("artifactId")) is int and artifact["artifactId"] > 0
        and type(artifact.get("sizeInBytes")) is int and artifact["sizeInBytes"] > 0
        and isinstance(artifact.get("digest"), str)
        and artifact["digest"].startswith("sha256:") and len(artifact["digest"]) == 71
        and all(character in "0123456789abcdef" for character in artifact["digest"][7:])
    )


def _ci_snapshot(branch: str) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    captures: list[dict[str, Any]] = []
    fetch = _run(["git", "fetch", "--prune", "origin", "main"])
    captures.append(fetch)
    if fetch["exitCode"] != 0:
        raise VerificationError(f"git fetch origin main failed: {fetch['stderr'].strip()}")
    main_sha = subprocess.run(["git", "rev-parse", "origin/main"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
    pr, pr_captures = _current_pull_request(branch)
    captures.extend(pr_captures)
    main_branch, main_capture = _gh_json(["api", "repos/PhilipGB/pisharp/branches/main"])
    captures.append(main_capture)
    main_api_sha = main_branch.get("commit", {}).get("sha")
    head_sha = pr.get("head", {}).get("sha")
    push, push_captures = _latest_exact_run("push", branch, head_sha or "")
    pull, pull_captures = _latest_exact_run("pull_request", branch, head_sha or "")
    rpc_evidence, rpc_evidence_captures = _latest_rpc_evidence_run(branch, head_sha or "")
    captures.extend(push_captures)
    captures.extend(pull_captures)
    captures.extend(rpc_evidence_captures)
    branch_runs, branch_runs_capture = _gh_json([
        "run", "list", "--branch", branch, "--limit", "100",
        "--json", "databaseId,event,headSha,conclusion,createdAt,headBranch,workflowName",
    ])
    captures.append(branch_runs_capture)
    manual_runs = [run for run in branch_runs if run.get("event") in {"workflow_dispatch", "manual"}
                   and run.get("headSha") == head_sha]
    required_jobs = required_ci_jobs()

    def record(run: dict[str, Any] | None, event: str) -> dict[str, Any] | None:
        if not isinstance(run, dict):
            return None
        jobs = {job.get("name"): job.get("conclusion") for job in run.get("jobs", [])}
        return {"event": event, "runId": str(run.get("databaseId")), "headSha": run.get("headSha"),
                "conclusion": run.get("conclusion"), "jobs": jobs,
                **({"baseSha": pr.get("base", {}).get("sha")} if event == "pull_request" else {})}

    push_record = record(push, "push")
    pull_record = record(pull, "pull_request")
    base = pr.get("base", {})
    head = pr.get("head", {})
    snapshot = {
        "branch": branch, "localMainSha": main_sha, "apiMainSha": main_api_sha,
        "localHeadSha": subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True,
                                         text=True, check=True).stdout.strip(),
        "pullRequest": {
            "number": pr.get("number"), "url": pr.get("html_url"), "state": pr.get("state"),
            "draft": pr.get("draft"), "merged": pr.get("merged"),
            "headSha": head.get("sha"), "headRef": head.get("ref"),
            "baseSha": base.get("sha"), "baseRef": base.get("ref"),
            "author": pr.get("user", {}).get("login"), "body": pr.get("body") or "",
        },
        "pushRun": push, "pullRequestRun": pull,
        "pushEvidence": push_record, "pullRequestEvidence": pull_record,
        "rpcParityEvidence": rpc_evidence,
        "manualRunsOnHead": manual_runs, "requiredJobs": sorted(required_jobs),
        "captures": captures,
    }
    return snapshot, captures


def run_ci0_checkpoint(evidence_dir: Path) -> dict[str, int]:
    """Capture exact-head automatic push and pull-request CI for the open milestone PR."""
    validate_clean_source_tree()
    if (evidence_dir / "evidence.json").exists():
        raise VerificationError(f"evidence already exists; choose a new run directory: {evidence_dir}")
    branch = "milestone/rpc-protocol-command-parity"
    snapshot, captures = _ci_snapshot(branch)
    contract = load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == "CI0")
    cases = {case["id"]: case for case in checkpoint["caseDefinitions"]}
    if [case["id"] for case in checkpoint["caseDefinitions"]] != list(CI0_CASE_IDS):
        raise VerificationError("CI0 case order differs from the frozen contract")
    pr = snapshot["pullRequest"]
    push = snapshot.get("pushEvidence")
    pull = snapshot.get("pullRequestEvidence")
    jobs = snapshot["requiredJobs"]
    exact_head = pr["headSha"] == snapshot["localHeadSha"]
    push_ok = bool(push and exact_head and push["headSha"] == pr["headSha"] and push["conclusion"] == "success")
    pull_ok = bool(pull and exact_head and pull["headSha"] == pr["headSha"] and pull["conclusion"] == "success")
    base_ancestor = isinstance(pr.get("baseSha"), str) and subprocess.run(
        ["git", "merge-base", "--is-ancestor", BASE_SHA, pr["baseSha"]], cwd=ROOT, capture_output=True,
    ).returncode == 0
    base_current = pr["baseRef"] == "main" and pr["baseSha"] == snapshot["apiMainSha"] == snapshot["localMainSha"]
    rpc_artifact_ok = _rpc_evidence_artifact_passes(snapshot.get("rpcParityEvidence"), pr["headSha"])
    complete_jobs = all(
        run is not None and set(run["jobs"]) == set(jobs) and all(value == "success" for value in run["jobs"].values())
        for run in (push, pull)
    ) and rpc_artifact_ok
    no_manual = not snapshot["manualRunsOnHead"]
    observations = [
        _check("push-workflow-exact-head", push_ok, {"run": push, "expectedHead": pr["headSha"]}),
        _check("pull-request-workflow-exact-head", pull_ok, {"run": pull, "expectedHead": pr["headSha"]}),
        _check("workflow-base-and-merge-base", base_current and base_ancestor, {
            "pullRequestBase": pr["baseSha"], "latestMain": snapshot["apiMainSha"],
            "containsPrerequisiteMerge": base_ancestor, "prHeadIsCurrentLocalHead": exact_head,
        }),
        _check("job-completeness", complete_jobs, {"requiredJobs": jobs,
               "pushJobSet": sorted((push or {}).get("jobs", {})),
               "pullRequestJobSet": sorted((pull or {}).get("jobs", {})),
               "rpcParityEvidence": snapshot.get("rpcParityEvidence")}),
        _check("no-duplicate-manual-dispatch", no_manual and {push and push["event"], pull and pull["event"]} == {"push", "pull_request"}, {
            "countedEvents": [push and push["event"], pull and pull["event"]],
            "manualRunsOnHead": snapshot["manualRunsOnHead"],
        }),
    ]
    evidence_dir.mkdir(parents=True, exist_ok=True)
    capture_metadata = []
    for index, capture in enumerate(captures, start=1):
        capture_metadata.append(_capture(evidence_dir, f"raw/github-query-{index:02d}.json", capture,
                                         f"github-query-{index:02d}"))
    snapshot_meta = _capture(evidence_dir, "raw/ci-snapshot.json", snapshot, "exact-head-ci-snapshot")
    capture_metadata.append(snapshot_meta)
    results = []
    for case_id, checked in zip(CI0_CASE_IDS, observations):
        case = cases[case_id]
        actual = _observed(case, [checked], snapshot)
        folder = case_id.replace(".", "-")
        expected_meta = _capture(evidence_dir, f"cases/{folder}/acceptance.json", {
            "accepted": True, "expectedObservation": case["expectedObservation"],
        }, f"{case_id}.acceptance")
        actual_meta = _capture(evidence_dir, f"cases/{folder}/observation.json", actual, f"{case_id}.observation")
        passed = actual["accepted"]
        results.append({
            "caseId": case_id, "checkpointId": "CI0", "contractExpectedObservation": case["expectedObservation"],
            "executed": True, "passed": passed, "skipped": False,
            "rawArtifacts": [*capture_metadata, expected_meta, actual_meta],
            "assertions": [{
                "contractObservation": case["expectedObservation"], "comparison": case["comparisonRules"]["mode"],
                "piArtifact": expected_meta["path"], "piPointer": "/accepted", "piValue": True,
                "pisharpArtifact": actual_meta["path"], "pisharpPointer": "/accepted",
                "pisharpValue": passed, "passed": passed,
            }],
        })
    source_evidence_path = evidence_dir.parent / "E0/evidence.json"
    source_evidence = json.loads(source_evidence_path.read_text(encoding="utf-8"))
    quality_evidence_path = evidence_dir.parent / "Q0/evidence.json"
    quality_evidence = json.loads(quality_evidence_path.read_text(encoding="utf-8"))
    quality_counts = validate_q0_authoritative_counts(
        quality_evidence, evidence_dir.parent / "Q0",
        expected_source_commit=source_evidence["implementationSourceCommit"],
    )
    ci_evidence = {"runs": [run for run in (push, pull) if run is not None],
                   "rpcParityEvidence": snapshot.get("rpcParityEvidence")}
    publication = {
        "number": pr["number"], "url": pr["url"], "branch": pr["headRef"],
        "headSha": pr["headSha"], "baseSha": pr["baseSha"], "author": pr["author"],
        "implementationSourceCommit": source_evidence["implementationSourceCommit"],
        "fullDotnetTestCount": quality_counts["total"],
        "bodySha256": hashlib.sha256(pr["body"].encode("utf-8")).hexdigest(),
        "pushRunId": push["runId"] if push else None,
        "pullRequestRunId": pull["runId"] if pull else None,
    }
    source_head = source_evidence["implementationSourceCommit"]
    final_head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
    evidence = {
        "schemaVersion": 1, "contractSha256": FROZEN_CONTRACT_SHA256, "piCommit": PINNED_PI_SHA,
        "implementationSourceCommit": source_head, "finalHeadCommit": final_head,
        "pisharpBaseCommit": BASE_SHA, "checkpointId": "CI0", "caseResults": results,
        "ciEvidence": ci_evidence, "publicationEvidence": publication,
    }
    (evidence_dir / "evidence.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return validate_case_results(results, "CI0", evidence_dir)


def _publication_description_check(pr: dict[str, Any], source_sha: str,
                                   ci_evidence: dict[str, Any], quality_counts: dict[str, int]) -> tuple[bool, dict[str, Any]]:
    contract = load_contract()
    modes = {mode: sum(case["comparisonRules"]["mode"] == mode for case in contract["cases"])
             for mode in ("strict", "equivalent", "intentional-safety-difference")}
    runs = {run["event"]: run for run in ci_evidence.get("runs", [])}
    body = pr.get("body") or ""
    dotnet_count = quality_counts.get("total")
    if type(dotnet_count) is not int or dotnet_count <= 0:
        raise VerificationError("PUB0 requires the numeric total from authoritative Q0 testCounts")
    evidence_workflow = ci_evidence.get("rpcParityEvidence", {})
    evidence_artifact = evidence_workflow.get("artifact", {}) if isinstance(evidence_workflow, dict) else {}
    required_tokens = [
        "24/24", "252/252", PINNED_PI_SHA, BASE_SHA, source_sha, pr.get("headSha", ""),
        str(runs.get("push", {}).get("runId", "")), str(runs.get("pull_request", {}).get("runId", "")),
        f"{dotnet_count:,}/{dotnet_count:,}",
        f"Strict: {modes['strict']}", f"Equivalent: {modes['equivalent']}",
        f"Intentional safety differences: {modes['intentional-safety-difference']}",
        "20 credential-gated", "unverified", "managed-package parity corpus",
        str(evidence_workflow.get("runId", "")) if isinstance(evidence_workflow, dict) else "",
        evidence_artifact.get("name", ""), evidence_artifact.get("digest", ""),
    ]
    matched = all(token and token.lower() in body.lower() for token in required_tokens)
    expected_report = {
        "checkpointCount": 24, "requiredCaseCount": contract["requiredCaseCount"], "comparisonCounts": modes,
        "fullDotnetTestCount": dotnet_count,
        "pushRunId": runs.get("push", {}).get("runId"),
        "pullRequestRunId": runs.get("pull_request", {}).get("runId"),
        "rpcParityEvidenceRunId": evidence_workflow.get("runId") if isinstance(evidence_workflow, dict) else None,
        "rpcParityEvidenceArtifactName": evidence_artifact.get("name"),
        "rpcParityEvidenceArtifactDigest": evidence_artifact.get("digest"),
        "implementationSourceCommit": source_sha, "finalHeadCommit": pr.get("headSha"),
        "allRequiredTokensPresent": matched,
    }
    return matched, expected_report


def run_pub0_checkpoint(evidence_dir: Path) -> dict[str, int]:
    """Verify and receipt the final draft PR state without merging it."""
    validate_clean_source_tree()
    if (evidence_dir / "evidence.json").exists():
        raise VerificationError(f"evidence already exists; choose a new run directory: {evidence_dir}")
    branch = "milestone/rpc-protocol-command-parity"
    snapshot, captures = _ci_snapshot(branch)
    root = evidence_dir.parent
    e0_path = root / "E0/evidence.json"
    ci0_path = root / "CI0/evidence.json"
    e0 = json.loads(e0_path.read_text(encoding="utf-8"))
    ci0 = json.loads(ci0_path.read_text(encoding="utf-8"))
    source_sha = e0.get("implementationSourceCommit")
    final_head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True,
                                text=True, check=True).stdout.strip()
    ci_evidence = ci0.get("ciEvidence", {})
    rpc_evidence = ci_evidence.get("rpcParityEvidence") if isinstance(ci_evidence, dict) else None
    rpc_evidence = rpc_evidence if isinstance(rpc_evidence, dict) else {}
    rpc_artifact = rpc_evidence.get("artifact")
    rpc_artifact = rpc_artifact if isinstance(rpc_artifact, dict) else {}
    pr = snapshot["pullRequest"]
    self_review_capture = _run(["gh", "api", f"repos/PhilipGB/pisharp/pulls/{pr['number']}/reviews"])
    if self_review_capture["exitCode"] != 0:
        raise VerificationError(f"GitHub review query failed: {self_review_capture['stderr'].strip()}")
    reviews = json.loads(self_review_capture["stdout"])
    captures.append(self_review_capture)
    no_self_approval = not any(review.get("user", {}).get("login") == pr["author"]
                              and review.get("state") == "APPROVED" for review in reviews)
    open_draft = pr["state"] == "open" and pr["draft"] is True and pr["merged"] is False
    # Validate canonical Q0 counts against raw test output before reading the published total.
    quality_evidence_path = root / "Q0/evidence.json"
    quality_evidence = json.loads(quality_evidence_path.read_text(encoding="utf-8"))
    quality_counts = validate_q0_authoritative_counts(
        quality_evidence, root / "Q0", expected_source_commit=source_sha,
    )
    body_matches, report = _publication_description_check(
        pr, source_sha, ci_evidence, quality_counts,
    )

    base_latest = pr["baseRef"] == "main" and pr["baseSha"] == snapshot["apiMainSha"] == snapshot["localMainSha"]
    report_matches = (pr["headSha"] == final_head and pr["headRef"] == branch
                      and pr["baseRef"] == "main" and source_sha == e0.get("sourceBoundEvidence", {}).get("implementationSourceCommit"))
    verifier_results = {}
    for checkpoint_id in ("E0", "CI0"):
        result = _run([
            sys.executable, str(HERE / "verify.py"), "check", "--checkpoint", checkpoint_id,
            "--evidence-dir", str(root / checkpoint_id),
        ])
        verifier_results[checkpoint_id] = result
        captures.append(result)
    e0_ci_ok = all(result["exitCode"] == 0 for result in verifier_results.values())

    contract = load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == "PUB0")
    if [case["id"] for case in checkpoint["caseDefinitions"]] != list(PUB0_CASE_IDS):
        raise VerificationError("PUB0 case order differs from the frozen contract")
    case_defs = {case["id"]: case for case in checkpoint["caseDefinitions"]}
    observations = [
        _check("draft-pr-open-unmerged", open_draft and base_latest and report_matches, {
            "state": pr["state"], "draft": pr["draft"], "merged": pr["merged"], "baseIsLatestMain": base_latest,
            "headIsCurrent": pr["headSha"] == final_head,
        }),
        _check("pr-description-evidence", body_matches, report),
        _check("publication-verifier-current-evidence", e0_ci_ok and open_draft and report_matches and body_matches,
               {"E0VerifierExitCode": verifier_results["E0"]["exitCode"],
                "CI0VerifierExitCode": verifier_results["CI0"]["exitCode"],
                "PRHeadIsCurrent": report_matches}),
        _check("e0-ci0-pub0-independent", e0_ci_ok, {
            "E0": verifier_results["E0"]["stdout"] + verifier_results["E0"]["stderr"],
            "CI0": verifier_results["CI0"]["stdout"] + verifier_results["CI0"]["stderr"],
        "PUB0": "An independent PUB0 verification is run after this evidence is written.",
        }),
        _check("single-contributor-review-state", no_self_approval, {
            "author": pr["author"], "selfApproved": not no_self_approval,
            "reviewCount": len(reviews),
        }),
        _check("final-source-and-pin-report", report_matches and body_matches, {
            **report, "pisharpBaseCommit": BASE_SHA, "piCommit": PINNED_PI_SHA,
            "branch": branch, "prNumber": pr["number"], "prUrl": pr["url"],
        }),
    ]

    evidence_dir.mkdir(parents=True, exist_ok=True)
    capture_metadata = []
    for index, capture in enumerate(captures, start=1):
        capture_metadata.append(_capture(evidence_dir, f"raw/publication-query-{index:02d}.json", capture,
                                         f"publication-query-{index:02d}"))
    snapshot_meta = _capture(evidence_dir, "raw/publication-snapshot.json", snapshot, "current-publication-snapshot")
    capture_metadata.append(snapshot_meta)
    results = []
    for case_id, checked in zip(PUB0_CASE_IDS, observations):
        case = case_defs[case_id]
        actual = _observed(case, [checked], report)
        folder = case_id.replace(".", "-")
        expected_meta = _capture(evidence_dir, f"cases/{folder}/acceptance.json", {
            "accepted": True, "expectedObservation": case["expectedObservation"],
        }, f"{case_id}.acceptance")
        actual_meta = _capture(evidence_dir, f"cases/{folder}/observation.json", actual, f"{case_id}.observation")
        passed = actual["accepted"]
        results.append({
            "caseId": case_id, "checkpointId": "PUB0", "contractExpectedObservation": case["expectedObservation"],
            "executed": True, "passed": passed, "skipped": False,
            "rawArtifacts": [*capture_metadata, expected_meta, actual_meta],
            "assertions": [{
                "contractObservation": case["expectedObservation"], "comparison": case["comparisonRules"]["mode"],
                "piArtifact": expected_meta["path"], "piPointer": "/accepted", "piValue": True,
                "pisharpArtifact": actual_meta["path"], "pisharpPointer": "/accepted",
                "pisharpValue": passed, "passed": passed,
            }],
        })
    publication = {
        "number": pr["number"], "url": pr["url"], "branch": pr["headRef"],
        "headSha": pr["headSha"], "baseSha": pr["baseSha"], "author": pr["author"],
        "implementationSourceCommit": source_sha,
        "fullDotnetTestCount": quality_counts["total"],
        "bodySha256": hashlib.sha256(pr["body"].encode("utf-8")).hexdigest(),
        "pushRunId": next((run.get("runId") for run in ci_evidence.get("runs", []) if run.get("event") == "push"), None),
        "pullRequestRunId": next((run.get("runId") for run in ci_evidence.get("runs", []) if run.get("event") == "pull_request"), None),
        "rpcParityEvidenceRunId": rpc_evidence.get("runId"),
        "rpcParityEvidenceArtifactName": rpc_artifact.get("name"),
        "rpcParityEvidenceArtifactDigest": rpc_artifact.get("digest"),
    }
    ci_evidence = ci0.get("ciEvidence", {})
    evidence = {
        "schemaVersion": 1, "contractSha256": FROZEN_CONTRACT_SHA256, "piCommit": PINNED_PI_SHA,
        "implementationSourceCommit": source_sha, "finalHeadCommit": final_head,
        "pisharpBaseCommit": BASE_SHA, "checkpointId": "PUB0", "caseResults": results,
        "ciEvidence": ci_evidence, "publicationEvidence": publication,
    }
    (evidence_dir / "evidence.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    preliminary = _run([
        sys.executable, str(HERE / "verify.py"), "check", "--checkpoint", "PUB0",
        "--evidence-dir", str(evidence_dir),
    ])
    if preliminary["exitCode"] != 0:
        raise VerificationError(f"PUB0 independent publication verification failed: {preliminary['stdout']}{preliminary['stderr']}")
    verifier_meta = _capture(evidence_dir, "raw/preliminary-pub0-verifier.json", preliminary,
                             "independent-preliminary-pub0-verifier")
    results[2]["rawArtifacts"].append(verifier_meta)
    results[3]["rawArtifacts"].append(verifier_meta)
    (evidence_dir / "evidence.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    final = _run([
        sys.executable, str(HERE / "verify.py"), "check", "--checkpoint", "PUB0",
        "--evidence-dir", str(evidence_dir),
    ])
    if final["exitCode"] != 0:
        raise VerificationError(f"final PUB0 verification failed: {final['stdout']}{final['stderr']}")
    return validate_case_results(results, "PUB0", evidence_dir)


def _manifest_for_checkpoint(root: Path, checkpoint_id: str) -> tuple[dict[str, Any], str, str]:
    evidence_path = root / checkpoint_id / "evidence.json"
    if not evidence_path.is_file():
        raise VerificationError(f"missing required checkpoint evidence: {checkpoint_id}")
    data = json.loads(evidence_path.read_text(encoding="utf-8"))
    digest = hashlib.sha256(evidence_path.read_bytes()).hexdigest()
    return data, f"{checkpoint_id}/evidence.json", digest


def run_e0_checkpoint(pi_checkout: Path, evidence_dir: Path) -> dict[str, int]:
    """Re-run every prior checkpoint verifier and build an exact raw-capture manifest."""
    validate_clean_source_tree()
    validate_pi_checkout(pi_checkout)
    if (evidence_dir / "evidence.json").exists():
        raise VerificationError(f"evidence already exists; choose a new run directory: {evidence_dir}")
    root = evidence_dir.parent
    contract = load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == "E0")
    case_defs = {case["id"]: case for case in checkpoint["caseDefinitions"]}
    required_ids = [case["id"] for case in checkpoint["caseDefinitions"]]
    if required_ids != [
        "E0.inventory-source-pin", "E0.contract-case-index", "E0.implementation-source-identity",
        "E0.raw-capture-manifest", "E0.artifact-hash-integrity", "E0.normalization-register",
        "E0.safety-difference-register", "E0.test-reference-integrity", "E0.quality-summary-identity",
        "E0.repo-ledger-updates",
    ]:
        raise VerificationError("E0 case ordering differs from the frozen contract")

    artifacts = []
    child_evidence_records = []
    child_data: dict[str, dict[str, Any]] = {}
    child_check_outputs = []
    shared_source: str | None = None
    all_checkpoints_pass = True
    for checkpoint_id in E0_CHILD_CHECKPOINTS:
        child, relative_evidence, evidence_sha = _manifest_for_checkpoint(root, checkpoint_id)
        child_data[checkpoint_id] = child
        source_sha = child.get("implementationSourceCommit")
        shared_source = source_sha if shared_source is None else shared_source
        same_source = source_sha == shared_source
        child_evidence_records.append({
            "checkpointId": checkpoint_id, "evidencePath": relative_evidence,
            "evidenceSha256": evidence_sha, "implementationSourceCommit": source_sha,
        })
        if checkpoint_id == "V1":
            control = child.get("v1ControlEvidence", {})
            report = control.get("report", {})
            artifacts.append({"checkpointId": "V1", "caseId": "V1", "path": f"V1/{report.get('path')}",
                               "role": "verifier-control-report", "sha256": report.get("sha256")})
        else:
            for result in child.get("caseResults", []):
                for artifact in result.get("rawArtifacts", []):
                    artifacts.append({
                        "checkpointId": checkpoint_id, "caseId": result.get("caseId"),
                        "path": f"{checkpoint_id}/{artifact.get('path')}",
                        "role": artifact.get("role"), "sha256": artifact.get("sha256"),
                    })
        command = [sys.executable, str(HERE / "verify.py"), "check", "--checkpoint", checkpoint_id,
                   "--evidence-dir", str(root / checkpoint_id)]
        if checkpoint_id == "V1":
            command = [sys.executable, str(HERE / "verify.py"), "check", "--checkpoint", "V1",
                       "--evidence-dir", str(root / "V1")]
        result = _run(command)
        result["sameImplementationSource"] = same_source
        child_check_outputs.append(result)
        all_checkpoints_pass = all_checkpoints_pass and result["exitCode"] == 0 and same_source

    inventory = json.loads((ROOT / "docs/parity/fixtures/rpc-protocol-command-parity/inventory.json").read_text(encoding="utf-8"))
    contract_index = json.loads((ROOT / "docs/parity/fixtures/rpc-protocol-command-parity/required-case-index.json").read_text(encoding="utf-8"))
    load_contract()
    case_index_passes = contract_index.get("caseIds") == contract["requiredCaseIds"]
    source_pin_passes = inventory.get("sourcePins", {}).get("piCommit") == PINNED_PI_SHA
    source_identity_passes = (
        shared_source is not None and all(item.get("implementationSourceCommit") == shared_source
                                          for item in child_evidence_records)
    )
    raw_files_pass = True
    for artifact in artifacts:
        relative = artifact.get("path")
        digest = artifact.get("sha256")
        path = root / str(relative)
        if not isinstance(relative, str) or not isinstance(digest, str) or not path.is_file():
            raw_files_pass = False
        elif hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raw_files_pass = False
    expected_artifact_counts = sum(
        len(result.get("rawArtifacts", [])) for checkpoint_id, child in child_data.items()
        if checkpoint_id != "V1" for result in child.get("caseResults", [])
    ) + 1
    raw_manifest_complete = len(artifacts) == expected_artifact_counts and bool(artifacts)

    case_modes = {case["id"]: case["comparisonRules"]["mode"] for case in contract["cases"]}
    unresolved_count = sum(mode == "unresolved" for mode in case_modes.values())
    safety_case_ids = [case_id for case_id, mode in case_modes.items() if mode == "intentional-safety-difference"]
    safety_evidence = True
    for checkpoint_id, child in child_data.items():
        if checkpoint_id == "V1":
            continue
        by_id = {result.get("caseId"): result for result in child.get("caseResults", [])}
        for case_id in safety_case_ids:
            if not case_id.startswith(f"{checkpoint_id}."):
                continue
            result = by_id.get(case_id, {})
            for assertion in result.get("assertions", []):
                safety_evidence = safety_evidence and bool(
                    assertion.get("safetyDifferenceId") and assertion.get("safetyRegression")
                    and assertion.get("safetyEvidence") in {artifact.get("path") for artifact in result.get("rawArtifacts", [])}
                )

    test_paths = set()
    for item in [*inventory.get("commandInventory", []), *inventory.get("eventInventory", []),
                 *inventory.get("extensionUiMethods", [])]:
        for group in ("existingTests", "pisharpOwners"):
            for reference in item.get(group, []):
                if isinstance(reference, dict) and reference.get("path"):
                    test_paths.add(reference["path"])
    references_exist = bool(test_paths) and all((ROOT / relative).is_file() for relative in test_paths)
    q0 = child_data.get("Q0", {})
    q0_results = q0.get("caseResults", [])
    quality_ok = (q0.get("implementationSourceCommit") == shared_source
                  and len(q0_results) == len(next(item for item in contract["checkpoints"] if item["id"] == "Q0")["caseDefinitions"])
                  and all(item.get("passed") is True and item.get("skipped") is False for item in q0_results))
    ledger_texts = {relative: (ROOT / relative).read_text(encoding="utf-8") for relative in LEDGER_PATHS}
    ledger_required = (BASE_SHA, PINNED_PI_SHA, str(shared_source), "24/24", "252/252")
    ledger_pins_consistent = all(all(token in body for token in ledger_required) for body in ledger_texts.values())
    ledger_limitations = all(any(token in body.lower() for token in ("credential", "managed-package", "managed package"))
                             for body in ledger_texts.values())

    facts = {
        "piCommit": PINNED_PI_SHA, "pisharpBaseCommit": BASE_SHA, "implementationSourceCommit": shared_source,
        "requiredCheckpoints": list(E0_CHILD_CHECKPOINTS), "caseCount": contract["requiredCaseCount"],
        "artifactCount": len(artifacts), "safetyDifferenceCaseIds": safety_case_ids,
        "unresolvedCaseCount": unresolved_count, "referencedTestCount": len(test_paths),
        "repoLedgers": list(LEDGER_PATHS), "checkpointVerification": child_check_outputs,
    }
    checks = [
        _check("inventory-source-pin", source_pin_passes and len(inventory["sourcePins"]["sourceFileHashes"]) > 0,
               {"piCommit": inventory.get("sourcePins", {}).get("piCommit"),
                "trackedSourceFiles": len(inventory["sourcePins"]["sourceFileHashes"])}),
        _check("contract-case-index", case_index_passes, {"contractSha256": contract_index.get("contractSha256"),
               "indexedCaseCount": contract_index.get("caseCount"), "contractCaseCount": contract["requiredCaseCount"]}),
        _check("implementation-source-identity", source_identity_passes and all_checkpoints_pass,
               {"implementationSourceCommit": shared_source, "allCheckpointReceiptsCurrent": all_checkpoints_pass}),
        _check("raw-capture-manifest", raw_manifest_complete and raw_files_pass,
               {"artifactCount": len(artifacts), "allFilesPresent": raw_files_pass}),
        _check("artifact-hash-integrity", raw_files_pass, {"allHashesVerified": raw_files_pass}),
        _check("normalization-register", unresolved_count == 0,
               {"unresolvedCases": unresolved_count, "strictCaseCount": sum(mode == "strict" for mode in case_modes.values()),
                "equivalentCaseCount": sum(mode == "equivalent" for mode in case_modes.values())}),
        _check("safety-difference-register", safety_evidence and unresolved_count == 0,
               {"safetyDifferenceCaseIds": safety_case_ids, "allHaveRawEvidenceAndRegression": safety_evidence}),
        _check("test-reference-integrity", references_exist,
               {"referencedTestAndOwnerPaths": len(test_paths), "allExist": references_exist}),
        _check("quality-summary-identity", quality_ok,
               {"qualitySourceCommit": q0.get("implementationSourceCommit"),
                "qualityCasesPassed": sum(item.get("passed") is True for item in q0_results),
                "qualityCasesRequired": len(q0_results)}),
        _check("repo-ledger-updates", ledger_pins_consistent and ledger_limitations,
               {"ledgers": list(LEDGER_PATHS), "pinsAndTotalsConsistent": ledger_pins_consistent,
                "limitationsRecorded": ledger_limitations}),
    ]

    evidence_dir.mkdir(parents=True, exist_ok=True)
    manifest = {
        "schemaVersion": 1, "piCommit": PINNED_PI_SHA, "pisharpBaseCommit": BASE_SHA,
        "implementationSourceCommit": shared_source,
        "checkpoints": child_evidence_records,
        "artifacts": sorted(artifacts, key=lambda item: (item["checkpointId"], item["caseId"], item["path"], item["role"])),
    }
    manifest_meta = _capture(evidence_dir, "evidence-manifest.json", manifest, "source-bound-artifact-manifest")
    raw_check_meta = []
    for index, result in enumerate(child_check_outputs, start=1):
        raw_check_meta.append(_capture(evidence_dir, f"raw/checkpoint-check-{index:02d}.json", result,
                                       f"checkpoint-verifier-{E0_CHILD_CHECKPOINTS[index - 1]}"))

    case_results = []
    for case_id, checked in zip(required_ids, checks):
        case = case_defs[case_id]
        actual = _observed(case, [checked], facts)
        folder = case_id.replace(".", "-")
        oracle_meta = _capture(evidence_dir, f"cases/{folder}/acceptance.json", {
            "accepted": True, "expectedObservation": case["expectedObservation"],
        }, f"{case_id}.acceptance")
        observation_meta = _capture(evidence_dir, f"cases/{folder}/observation.json", actual,
                                    f"{case_id}.observation")
        passed = actual["accepted"]
        case_results.append({
            "caseId": case_id, "checkpointId": "E0",
            "contractExpectedObservation": case["expectedObservation"],
            "executed": True, "passed": passed, "skipped": False,
            "rawArtifacts": [manifest_meta, *raw_check_meta, oracle_meta, observation_meta],
            "assertions": [{
                "contractObservation": case["expectedObservation"],
                "comparison": case["comparisonRules"]["mode"],
                "piArtifact": oracle_meta["path"], "piPointer": "/accepted", "piValue": True,
                "pisharpArtifact": observation_meta["path"], "pisharpPointer": "/accepted",
                "pisharpValue": passed, "passed": passed,
            }],
        })
    current_head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
    evidence = {
        "schemaVersion": 1, "contractSha256": FROZEN_CONTRACT_SHA256, "piCommit": PINNED_PI_SHA,
        "implementationSourceCommit": shared_source, "finalHeadCommit": current_head,
        "pisharpBaseCommit": BASE_SHA, "checkpointId": "E0", "caseResults": case_results,
        "sourceBoundEvidence": {
            "implementationSourceCommit": shared_source,
            "checkpoints": child_evidence_records,
            "artifactManifest": {"path": f"E0/{manifest_meta['path']}", "sha256": manifest_meta["sha256"]},
        },
    }
    (evidence_dir / "evidence.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return validate_case_results(case_results, "E0", evidence_dir)

