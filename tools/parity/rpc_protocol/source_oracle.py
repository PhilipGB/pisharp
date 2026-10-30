#!/usr/bin/env python3
"""Capture repository, inventory, and prerequisite publication observations."""
from __future__ import annotations

import hashlib
import json
import re
import subprocess
from pathlib import Path
from typing import Any

from verify import (
    BASE_SHA,
    FROZEN_CONTRACT_SHA256,
    PINNED_PI_SHA,
    VerificationError,
    current_implementation_source_commit,
    load_contract,
    required_ci_jobs,
    validate_case_results,
    validate_clean_source_tree,
    validate_pi_checkout,
)

ROOT = Path(__file__).resolve().parents[3]
INVENTORY_PATH = ROOT / "docs/parity/fixtures/rpc-protocol-command-parity/inventory.json"
REVIEWED_HEAD = "c019cd0111106b70a83d56e30d86932c987291e1"
REVIEWED_IMPLEMENTATION = "41e9b7e7ebdea70b878e409204cd8b3d177144e2"
PUSH_RUN_ID = "40747257587"
PR_RUN_ID = "40747270521"

SOURCE_CASE_IDS = {
    "P0": (
        "P0.reviewed-head", "P0.exact-head-ci", "P0.published-acceptance",
        "P0.merge-state", "P0.reviewed-code-ancestry", "P0.merge-base-verification",
    ),
    "P1": (
        "P1.branch-name-and-base", "P1.baseline-main-ancestry", "P1.pinned-pi-sha",
        "P1.baseline-tree", "P1.maf-version",
    ),
    "V0": (
        "V0.all-command-schemas", "V0.all-event-families", "V0.extension-ui-schemas",
        "V0.state-and-error-projections", "V0.protocol-operation-coverage", "V0.owner-and-gap-register",
    ),
}


def source_case_ids() -> set[str]:
    return {case_id for case_ids in SOURCE_CASE_IDS.values() for case_id in case_ids}


def _run(argv: list[str], *, cwd: Path = ROOT) -> dict[str, Any]:
    completed = subprocess.run(argv, cwd=cwd, capture_output=True, text=True)
    return {
        "argv": argv,
        "cwd": str(cwd),
        "exitCode": completed.returncode,
        "stdout": completed.stdout,
        "stderr": completed.stderr,
    }


def _git(*args: str) -> str:
    result = _run(["git", *args])
    if result["exitCode"] != 0:
        raise VerificationError(f"git {' '.join(args)} failed: {result['stderr'].strip()}")
    return result["stdout"].strip()


def _capture(evidence_dir: Path, relative: str, value: Any, role: str) -> dict[str, str]:
    path = evidence_dir / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")
    path.write_bytes(payload)
    return {"path": relative, "role": role, "sha256": hashlib.sha256(payload).hexdigest()}


def _check(name: str, passed: bool, detail: Any) -> dict[str, Any]:
    return {"name": name, "passed": bool(passed), "detail": detail}


def _observed(case: dict[str, Any], checks: list[dict[str, Any]], facts: Any) -> dict[str, Any]:
    return {
        "expectedObservation": case["expectedObservation"],
        "accepted": bool(checks) and all(item["passed"] for item in checks),
        "checks": checks,
        "facts": facts,
    }


def _gh_json(argv: list[str]) -> tuple[Any, dict[str, Any]]:
    capture = _run(argv)
    if capture["exitCode"] != 0:
        raise VerificationError(f"{' '.join(argv)} failed: {capture['stderr'].strip()}")
    try:
        return json.loads(capture["stdout"]), capture
    except json.JSONDecodeError as error:
        raise VerificationError(f"{' '.join(argv)} returned invalid JSON: {error}") from error


def _p0_observations() -> tuple[dict[str, Any], list[dict[str, Any]]]:
    pr, pr_capture = _gh_json([
        "gh", "pr", "view", "28", "--json",
        "state,isDraft,mergedAt,mergeCommit,headRefOid,baseRefName,body,url,reviewDecision",
    ])
    reviews, review_capture = _gh_json(["gh", "api", "repos/PhilipGB/pisharp/pulls/28/reviews"])
    run_fields = "databaseId,event,headSha,conclusion,jobs"
    push, push_capture = _gh_json(["gh", "run", "view", PUSH_RUN_ID, "--json", run_fields])
    pull, pull_capture = _gh_json(["gh", "run", "view", PR_RUN_ID, "--json", run_fields])
    origin_main = _git("rev-parse", "origin/main")
    merge_sha = (pr.get("mergeCommit") or {}).get("oid")
    expected_jobs = required_ci_jobs()

    def run_passes(run: dict[str, Any], event: str) -> bool:
        jobs = run.get("jobs")
        return (
            run.get("event") == event
            and run.get("headSha") == REVIEWED_HEAD
            and run.get("conclusion") == "success"
            and isinstance(jobs, list)
            and {job.get("name") for job in jobs} == expected_jobs
            and all(job.get("conclusion") == "success" for job in jobs)
        )

    changed_paths = _git("diff", "--name-only", REVIEWED_IMPLEMENTATION, REVIEWED_HEAD).splitlines()
    forbidden_prefixes = ("src/", "tests/", "tools/parity/", ".github/workflows/")
    docs_only = all(not path.startswith(forbidden_prefixes) for path in changed_paths)
    reviewed_head_ancestor = _run(["git", "merge-base", "--is-ancestor", REVIEWED_HEAD, BASE_SHA])["exitCode"] == 0
    implementation_ancestor = _run(["git", "merge-base", "--is-ancestor", REVIEWED_IMPLEMENTATION, BASE_SHA])["exitCode"] == 0
    body = pr.get("body") or ""
    acceptance = all(token in body for token in (
        "22/22 checkpoints", "211/211 cases", "E0: 9/9", "CI0: 5/5", "PUB0: 5/5",
    ))
    no_changes_requested = not any(review.get("state") == "CHANGES_REQUESTED" for review in reviews)
    captures = [pr_capture, review_capture, push_capture, pull_capture]
    facts = {
        "pr": pr,
        "reviews": reviews,
        "pushRun": push,
        "pullRequestRun": pull,
        "requiredJobs": sorted(expected_jobs),
        "originMain": origin_main,
        "reviewedImplementationChangedPaths": changed_paths,
    }
    checks = [
        _check("reviewed-head", pr.get("headRefOid") == REVIEWED_HEAD, pr.get("headRefOid")),
        _check("exact-head-push-and-pr-ci", run_passes(push, "push") and run_passes(pull, "pull_request"), {
            "push": run_passes(push, "push"), "pull_request": run_passes(pull, "pull_request"),
        }),
        _check("published-acceptance-totals", acceptance, {"matched": acceptance}),
        _check("merged-ready-no-review-blocker", (
            pr.get("state") == "MERGED" and pr.get("isDraft") is False
            and bool(pr.get("mergedAt")) and merge_sha == BASE_SHA and no_changes_requested
        ), {"state": pr.get("state"), "draft": pr.get("isDraft"), "mergeSha": merge_sha,
            "noChangesRequested": no_changes_requested}),
        _check("reviewed-source-ancestry-and-docs-only", (
            reviewed_head_ancestor and implementation_ancestor and docs_only
        ), {"reviewedHeadAncestor": reviewed_head_ancestor, "implementationAncestor": implementation_ancestor,
            "docsOnlyDescendants": docs_only, "changedPaths": changed_paths}),
        _check("actual-merge-on-main", merge_sha == BASE_SHA and _run(
            ["git", "merge-base", "--is-ancestor", BASE_SHA, origin_main]
        )["exitCode"] == 0, {"mergeSha": merge_sha, "originMain": origin_main}),
    ]
    return {"checks": checks, "facts": facts, "captures": captures}, captures


def _p1_observations(pi_checkout: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    branch = _git("branch", "--show-current")
    base = _git("merge-base", "HEAD", "origin/main")
    commits = _git("rev-list", "--reverse", f"{BASE_SHA}..HEAD").splitlines()
    first_parent = _git("rev-parse", f"{commits[0]}^") if commits else ""
    pi_sha = _git("-C", str(pi_checkout), "rev-parse", "HEAD")
    validate_pi_checkout(pi_checkout)
    inventory = json.loads(INVENTORY_PATH.read_text(encoding="utf-8"))
    maf_project = (ROOT / "src/PiSharp.Runtime/PiSharp.Runtime.csproj").read_text(encoding="utf-8")
    maf_match = re.search(r'<PackageReference Include="Microsoft\.Agents\.AI" Version="([^"]+)"', maf_project)
    merge_tree = _git("rev-parse", f"{BASE_SHA}^{{tree}}")
    first_commit_parent = first_parent == BASE_SHA
    checks = [
        _check("branch-and-exact-base", branch == "milestone/rpc-protocol-command-parity" and base == BASE_SHA,
               {"branch": branch, "mergeBase": base}),
        _check("main-contains-prerequisite-merge", _run(
            ["git", "merge-base", "--is-ancestor", BASE_SHA, "origin/main"]
        )["exitCode"] == 0, {"prerequisiteMerge": BASE_SHA, "originMain": _git("rev-parse", "origin/main")}),
        _check("pinned-pi-and-inventory-tracked", pi_sha == PINNED_PI_SHA
               and len(inventory["sourcePins"]["sourceFileHashes"]) > 0,
               {"piSha": pi_sha, "trackedSourceFiles": len(inventory["sourcePins"]["sourceFileHashes"])}),
        _check("baseline-tree-and-first-parent", first_commit_parent and bool(merge_tree),
               {"mergeTree": merge_tree, "firstMilestoneCommitParent": first_parent}),
        _check("maf-version-and-target", bool(maf_match)
               and maf_match.group(1) == "1.21.0"
               and "<TargetFramework>net10.0</TargetFramework>" in maf_project,
               {"mafVersion": maf_match.group(1) if maf_match else None, "targetNet10": "<TargetFramework>net10.0</TargetFramework>" in maf_project}),
    ]
    return {"checks": checks, "facts": {"branch": branch, "mergeBase": base, "piSha": pi_sha,
            "firstMilestoneCommit": commits[0] if commits else None, "firstCommitParent": first_parent,
            "mergeTree": merge_tree, "mafVersion": maf_match.group(1) if maf_match else None}}, []


def _v0_observations() -> tuple[dict[str, Any], list[dict[str, Any]]]:
    inventory = json.loads(INVENTORY_PATH.read_text(encoding="utf-8"))
    contract = load_contract()
    commands = inventory["commandInventory"]
    events = inventory["eventInventory"]
    ui_methods = inventory["extensionUiMethods"]
    records = inventory["protocolRecordFamilies"]
    operations = inventory["protocolOperations"]
    all_ids = set(contract["requiredCaseIds"])
    commands_complete = (
        len(commands) == 33 and all(item.get("requestSchema") and item.get("responseSchema")
        and item.get("upstreamSources") and item.get("pisharpOwners") and item.get("existingTests")
        and item.get("mandatoryVerificationCaseIds") for item in commands)
        and all(item["checkpoint"] in contract["requiredCheckpointIds"] for item in commands)
    )
    events_complete = len(events) == 24 and all(
        item.get("schemaVariants") and item.get("sources") and item.get("documentation")
        and item.get("projectionOwner") and item.get("existingTests") and item.get("mandatoryVerificationCaseIds")
        for item in events
    )
    ui_complete = len(ui_methods) == 9 and all(
        item.get("requestSchemaSource") and item.get("source") and item.get("documentation")
        and item.get("existingTests") and item.get("mandatoryVerificationCaseIds") for item in ui_methods
    )
    projection = inventory.get("stateProjection", {})
    state_complete = (
        len(projection.get("fields", [])) == 12 and projection.get("source") and projection.get("documentation")
        and set(projection.get("mandatoryVerificationCaseIds", [])).issubset(all_ids)
        and len(inventory.get("mismatchRegister", [])) >= 1
    )
    protocol_complete = (
        len(records) == 5 and len(operations) == 7
        and all(item.get("source") and item.get("schema") for item in records)
        and all(item.get("source") and item.get("mandatoryVerificationCaseIds")
                and set(item["mandatoryVerificationCaseIds"]).issubset(all_ids) for item in operations)
    )
    gaps = inventory.get("knownCoverageLimitations", [])
    mismatch_register = inventory.get("mismatchRegister", [])
    owners = inventory.get("pisharpSourceOwners", [])
    owners_complete = all((ROOT / item["path"]).is_file() for item in owners)
    classifications = [item.get("priorEvidenceClassification", "") for item in commands]
    classifications.extend(item.get("assessment", "") for item in events)
    classifications.extend(item.get("assessment", "") for item in ui_methods)
    baseline_markers = ("baseline", "existing", "unverified", "partial-prior", "require new process")
    baseline_only = all(any(marker in value.lower() for marker in baseline_markers) for value in classifications)
    gaps_complete = bool(gaps) and all(
        item.get("id") and item.get("classification") and item.get("evidence")
        and set(item.get("requiredCheckpoint", "").split("/")).issubset(contract["requiredCheckpointIds"])
        and item.get("resolution") for item in mismatch_register
    )
    facts = {
        "counts": inventory["counts"],
        "commands": len(commands), "events": len(events), "uiMethods": len(ui_methods),
        "stateFields": len(projection.get("fields", [])), "records": len(records),
        "operations": len(operations), "owners": len(owners),
        "knownCoverageLimitations": gaps, "mismatchRegister": mismatch_register,
        "priorEvidenceClassifications": sorted(set(classifications)),
    }
    checks = [
        _check("all-command-schemas", commands_complete, {"count": len(commands), "complete": commands_complete}),
        _check("all-event-families", events_complete, {"count": len(events), "complete": events_complete}),
        _check("extension-ui-schemas", ui_complete, {"count": len(ui_methods), "complete": ui_complete}),
        _check("state-and-error-projections", state_complete, {"fieldCount": len(projection.get("fields", [])),
               "mismatchRegisterCount": len(mismatch_register), "complete": state_complete}),
        _check("protocol-operation-coverage", protocol_complete, {"recordFamilies": len(records),
               "operations": len(operations), "complete": protocol_complete}),
        _check("owner-and-gap-register", owners_complete and gaps_complete and baseline_only, {
            "existingOwners": owners_complete, "knownLimitations": len(gaps),
            "mismatchEntries": len(mismatch_register), "baselineOnly": baseline_only,
            "complete": owners_complete and gaps_complete and baseline_only}),
    ]
    return {"checks": checks, "facts": facts}, []


def run_source_checkpoint(checkpoint_id: str, pi_checkout: Path, evidence_dir: Path,
                          *, selected_case_id: str | None = None) -> dict[str, int]:
    """Execute source-bound prerequisite and inventory cases and save hashed receipts."""
    if checkpoint_id not in SOURCE_CASE_IDS:
        raise VerificationError(f"no source-audit runner registered for {checkpoint_id}")
    validate_clean_source_tree()
    if (evidence_dir / "evidence.json").exists():
        raise VerificationError(f"evidence already exists; choose a new run directory: {evidence_dir}")
    pi_checkout = pi_checkout.resolve()
    if checkpoint_id == "P1":
        observation, raw_captures = _p1_observations(pi_checkout)
    elif checkpoint_id == "P0":
        observation, raw_captures = _p0_observations()
    else:
        validate_pi_checkout(pi_checkout)
        observation, raw_captures = _v0_observations()

    evidence_dir.mkdir(parents=True, exist_ok=True)
    raw_metadata = []
    for index, capture in enumerate(raw_captures):
        metadata = _capture(evidence_dir, f"raw/source-audit-{index + 1}.json", capture, f"source-audit-{index + 1}")
        raw_metadata.append(metadata)
    capture_metadata = _capture(evidence_dir, "raw/checkpoint-observation.json", observation, "checkpoint-observation")
    raw_metadata.append(capture_metadata)

    contract = load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == checkpoint_id)
    cases_by_id = {case["id"]: case for case in checkpoint["caseDefinitions"]}
    if [case["id"] for case in checkpoint["caseDefinitions"]] != list(SOURCE_CASE_IDS[checkpoint_id]):
        raise VerificationError(f"{checkpoint_id} source runner case order differs from the frozen contract")
    selected_ids = [selected_case_id] if selected_case_id else list(SOURCE_CASE_IDS[checkpoint_id])
    unknown = set(selected_ids) - set(SOURCE_CASE_IDS[checkpoint_id])
    if unknown:
        raise VerificationError(f"no source-audit case registered for: {', '.join(sorted(unknown))}")
    checks_by_case = dict(zip(SOURCE_CASE_IDS[checkpoint_id], observation["checks"]))
    case_results = []
    for case_id in selected_ids:
        observed = checks_by_case[case_id]
        case = cases_by_id[case_id]
        actual = _observed(case, [observed], observation.get("facts"))
        case_path = case_id.replace(".", "-")
        oracle_meta = _capture(evidence_dir, f"cases/{case_path}/acceptance.json", {
            "accepted": True, "expectedObservation": case["expectedObservation"],
        }, f"{case_id}.acceptance")
        actual_meta = _capture(evidence_dir, f"cases/{case_path}/observation.json", actual, f"{case_id}.observation")
        raw = [*raw_metadata, oracle_meta, actual_meta]
        passed = actual["accepted"]
        case_results.append({
            "caseId": case_id,
            "checkpointId": checkpoint_id,
            "contractExpectedObservation": case["expectedObservation"],
            "executed": True,
            "passed": passed,
            "skipped": False,
            "rawArtifacts": raw,
            "assertions": [{
                "contractObservation": case["expectedObservation"],
                "comparison": case["comparisonRules"]["mode"],
                "piArtifact": oracle_meta["path"],
                "piPointer": "/accepted",
                "piValue": True,
                "pisharpArtifact": actual_meta["path"],
                "pisharpPointer": "/accepted",
                "pisharpValue": passed,
                "passed": passed,
            }],
        })
    source_head = current_implementation_source_commit()
    final_head = _git("rev-parse", "HEAD")
    evidence = {
        "schemaVersion": 1,
        "contractSha256": FROZEN_CONTRACT_SHA256,
        "piCommit": PINNED_PI_SHA,
        "implementationSourceCommit": source_head,
        "finalHeadCommit": final_head,
        "pisharpBaseCommit": BASE_SHA,
        "checkpointId": checkpoint_id,
        "caseResults": case_results,
        "sourceAudit": {"rawArtifacts": raw_metadata, "facts": observation.get("facts")},
    }
    (evidence_dir / "evidence.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    counts = validate_case_results(case_results, checkpoint_id, evidence_dir,
                                   require_complete=selected_case_id is None)
    return counts

