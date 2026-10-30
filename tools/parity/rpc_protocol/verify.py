#!/usr/bin/env python3
"""Fail-closed verifier for the frozen RPC protocol parity contract."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
CONTRACT_PATH = HERE / "contract.json"
INDEX_PATH = ROOT / "docs/parity/fixtures/rpc-protocol-command-parity/required-case-index.json"
INVENTORY_PATH = ROOT / "docs/parity/fixtures/rpc-protocol-command-parity/inventory.json"
FROZEN_CONTRACT_SHA256 = "036c369054fbb50887629666f6b25ef76d8a9fbe856d00d4a79a2cf9609f0761"
FROZEN_INVENTORY_SHA256 = "b7c4983036690f3b929a205256aa33e44e143a8ef1ec3aab7f8bcfb6cf68051a"
PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"
BASE_SHA = "646db89bd396b8089b1b9a058fa96b1489f4d0cd"
REQUIRED_CHECKPOINT_IDS = (
    "P0", "P1", "V0", "V1", "R1", "R2", "R3", "R4", "R5", "R6", "R7",
    "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15", "D0", "Q0",
    "E0", "CI0", "PUB0",
)
SHA1 = re.compile(r"^[0-9a-f]{40}$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
DOTNET_TEST_SUMMARY = re.compile(
    r"(?:Passed!\s*-\s*)?Failed:\s*([\d,]+),\s*Passed:\s*([\d,]+),\s*Skipped:\s*([\d,]+),\s*Total:\s*([\d,]+)",
)
PROCESS_CHECKPOINTS = {f"R{i}" for i in range(1, 16)} | {"D0"}
CI_WORKFLOW_PATH = ROOT / ".github/workflows/ci.yml"
IMPLEMENTATION_SOURCE_PATHS = ("src", "tests", "tools/parity", ".github/workflows/ci.yml")


class VerificationError(ValueError):
    """The contract or evidence violates a frozen acceptance requirement."""


def parse_dotnet_test_counts(output: str) -> dict[str, int] | None:
    """Extract the final .NET test summary from a captured command output."""
    matches = list(DOTNET_TEST_SUMMARY.finditer(output))
    if not matches:
        return None
    match = matches[-1]
    return {
        "passed": int(match.group(2).replace(",", "")),
        "failed": int(match.group(1).replace(",", "")),
        "skipped": int(match.group(3).replace(",", "")),
        "total": int(match.group(4).replace(",", "")),
    }


def _read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise VerificationError(f"cannot read JSON from {path}: {error}") from error


def _relative_artifact(evidence_dir: Path, relative: str) -> Path:
    path = Path(relative)
    if path.is_absolute() or ".." in path.parts or not path.parts:
        raise VerificationError(f"artifact path escapes evidence directory: {relative}")
    return evidence_dir / path


def load_contract(path: Path = CONTRACT_PATH, *, verify_lock: bool = True) -> dict[str, Any]:
    try:
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
    except OSError as error:
        raise VerificationError(f"cannot read frozen contract: {error}") from error
    if verify_lock and digest != FROZEN_CONTRACT_SHA256:
        raise VerificationError(f"frozen contract digest changed: {digest}")
    contract = _read_json(path)
    checkpoints = contract.get("checkpoints")
    if not isinstance(checkpoints, list):
        raise VerificationError("contract checkpoints must be a list")
    checkpoint_ids = [item.get("id") for item in checkpoints if isinstance(item, dict)]
    if tuple(checkpoint_ids) != REQUIRED_CHECKPOINT_IDS:
        raise VerificationError("contract checkpoint IDs or order differ from the frozen set")
    if contract.get("checkpointCount") != len(checkpoints):
        raise VerificationError("checkpoint count is not derived from the checkpoint list")
    if contract.get("requiredCheckpointIds") != list(REQUIRED_CHECKPOINT_IDS):
        raise VerificationError("required checkpoint IDs differ from the frozen list")
    if contract.get("sourcePins", {}).get("piCommit") != PINNED_PI_SHA:
        raise VerificationError("pinned Pi SHA changed")
    if contract.get("sourcePins", {}).get("pisharpBaseCommit") != BASE_SHA:
        raise VerificationError("PR #28 actual merge base changed")
    if contract.get("sourcePins", {}).get("prerequisiteActualMergeCommit") != BASE_SHA:
        raise VerificationError("actual PR #28 merge SHA changed")
    if contract.get("sourcePins", {}).get("branch") != "milestone/rpc-protocol-command-parity":
        raise VerificationError("milestone branch identity changed")
    index_ids: list[str] = []
    definition_ids: list[str] = []
    seen: set[str] = set()
    for checkpoint in checkpoints:
        required_ids = checkpoint.get("requiredCaseIds")
        definitions = checkpoint.get("caseDefinitions")
        if not isinstance(required_ids, list) or not required_ids:
            raise VerificationError(f"{checkpoint.get('id')} has no immutable case IDs")
        if not isinstance(definitions, list) or [item.get("id") for item in definitions] != required_ids:
            raise VerificationError(f"{checkpoint.get('id')} case definitions do not match its case IDs")
        for required in ("runner", "expectedObservations", "passFailCriteria", "evidencePath", "sourceReferences"):
            if not checkpoint.get(required):
                raise VerificationError(f"{checkpoint['id']} lacks checkpoint field {required}")
        for case in definitions:
            case_id = case.get("id")
            if not isinstance(case_id, str) or case_id in seen:
                raise VerificationError(f"missing or duplicate case ID: {case_id}")
            seen.add(case_id)
            for required in ("inputs", "expectedObservation", "sourceReferences", "runner", "evidencePath", "passFailCriteria", "comparisonRules"):
                if not case.get(required):
                    raise VerificationError(f"{case_id} lacks case field {required}")
            rules = case["comparisonRules"]
            if rules.get("mode") not in {"strict", "equivalent", "intentional-safety-difference", "unresolved"}:
                raise VerificationError(f"{case_id} has an unknown comparison mode")
            if rules["mode"] == "strict" and rules.get("normalizations"):
                raise VerificationError(f"{case_id} applies normalization under strict comparison")
            index_ids.append(case_id)
            definition_ids.append(case_id)
    required_ids = contract.get("requiredCaseIds")
    if required_ids != definition_ids or len(required_ids) != len(set(required_ids)):
        raise VerificationError("required case ID list differs from checkpoint definitions")
    cases = contract.get("cases")
    flattened_cases = [case for checkpoint in checkpoints for case in checkpoint["caseDefinitions"]]
    if not isinstance(cases, list) or cases != flattened_cases or [case.get("id") for case in cases] != required_ids:
        raise VerificationError("global case list differs from checkpoint case definitions")
    if contract.get("requiredCaseCount") != len(required_ids):
        raise VerificationError("required case count is not derived from immutable IDs")
    inventory_digest = hashlib.sha256(INVENTORY_PATH.read_bytes()).hexdigest()
    if verify_lock and inventory_digest != FROZEN_INVENTORY_SHA256:
        raise VerificationError(f"source inventory digest changed: {inventory_digest}")
    inventory = _read_json(INVENTORY_PATH)
    if contract.get("inventory", {}).get("sha256") != inventory_digest:
        raise VerificationError("contract inventory digest does not match the inventory file")
    counts = inventory.get("counts", {})
    for name in ("commands", "eventFamilies", "extensionUiMethods", "credentialGatedUpstreamCases"):
        if contract.get("inventory", {}).get(name) != counts.get(name):
            raise VerificationError(f"contract and inventory differ on {name}")
    if len(inventory.get("commandInventory", [])) != counts.get("commands"):
        raise VerificationError("command inventory count does not match its list")
    if len(inventory.get("eventInventory", [])) != counts.get("eventFamilies"):
        raise VerificationError("event inventory count does not match its list")
    if len(inventory.get("extensionUiMethods", [])) != counts.get("extensionUiMethods"):
        raise VerificationError("extension UI method count does not match its list")
    credential_cases = inventory.get("credentialGatedUpstreamCases", [])
    if len(credential_cases) != counts.get("credentialGatedUpstreamCases"):
        raise VerificationError("credential-gated test count does not match its exact identity list")
    if len({(item.get("source"), item.get("testName")) for item in credential_cases}) != len(credential_cases):
        raise VerificationError("credential-gated test identity list contains duplicates")
    if any("never counted as a pass" not in item.get("classification", "") for item in credential_cases):
        raise VerificationError("credential-gated tests must remain classified as unverified, not passing")
    for collection, key in (("commandInventory", "name"), ("eventInventory", "name"), ("extensionUiMethods", "name")):
        names = [item.get(key) for item in inventory[collection]]
        if len(names) != len(set(names)):
            raise VerificationError(f"{collection} contains duplicate names")
    index = _read_json(INDEX_PATH)
    if index.get("contractSha256") != digest or index.get("caseIds") != required_ids:
        raise VerificationError("required-case index is stale or differs from the frozen contract")
    if index.get("caseCount") != len(required_ids) or index.get("checkpointIds") != list(REQUIRED_CHECKPOINT_IDS):
        raise VerificationError("required-case index counts or checkpoints are incorrect")
    return contract


def source_bound_case_count(source_bound_evidence: Any) -> int:
    """Count the frozen cases represented by E0's source-bound checkpoint manifest."""
    checkpoints = source_bound_evidence.get("checkpoints") if isinstance(source_bound_evidence, dict) else None
    if not isinstance(checkpoints, list):
        raise VerificationError("E0 source-bound checkpoint manifest is missing")
    checkpoint_ids = [item.get("checkpointId") for item in checkpoints if isinstance(item, dict)]
    expected_ids = ["P0", "P1", "V0", "V1", *[f"R{i}" for i in range(1, 16)], "D0", "Q0"]
    if checkpoint_ids != expected_ids:
        raise VerificationError("E0 source-bound checkpoint manifest is incomplete or out of contract order")
    definitions = {item["id"]: item["caseDefinitions"] for item in load_contract()["checkpoints"]}
    return sum(len(definitions[checkpoint_id]) for checkpoint_id in checkpoint_ids)


def _recorded_checkout_directory_matches(cwd: str) -> bool:
    """Match captured absolute working directories across independent checkout roots."""
    normalized = cwd.replace("\\", "/").rstrip("/")
    recorded_name = normalized.rsplit("/", 1)[-1].casefold()
    expected_names = {ROOT.name.casefold()}
    remote = subprocess.run(
        ["git", "remote", "get-url", "origin"], cwd=ROOT, capture_output=True, text=True,
    )
    if remote.returncode == 0 and remote.stdout.strip():
        remote_name = re.split(r"[/\\:]", remote.stdout.strip().rstrip("/"))[-1]
        expected_names.add(remote_name.removesuffix(".git").casefold())
    return bool(recorded_name) and recorded_name in expected_names


def validate_pi_checkout(pi_checkout: Path) -> None:
    result = subprocess.run(["git", "-C", str(pi_checkout), "rev-parse", "HEAD"], capture_output=True, text=True)
    if result.returncode != 0 or result.stdout.strip() != PINNED_PI_SHA:
        raise VerificationError(f"Pi checkout is not pinned to {PINNED_PI_SHA}")
    inventory = _read_json(INVENTORY_PATH)
    for relative, expected in inventory["sourcePins"]["sourceFileHashes"].items():
        path = pi_checkout / relative
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise VerificationError(f"pinned Pi source file changed: {relative}")


def _pointer(value: Any, pointer: str) -> Any:
    if pointer == "":
        return value
    if not pointer.startswith("/"):
        raise VerificationError(f"JSON pointer must be empty or start with '/': {pointer}")
    current = value
    for token in pointer[1:].split("/"):
        token = token.replace("~1", "/").replace("~0", "~")
        try:
            current = current[int(token)] if isinstance(current, list) else current[token]
        except (KeyError, IndexError, TypeError, ValueError) as error:
            raise VerificationError(f"JSON pointer does not resolve: {pointer}") from error
    return current


def _validate_assertion(assertion: dict[str, Any], case: dict[str, Any], evidence_dir: Path,
                        available_artifacts: set[str]) -> None:
    if assertion.get("contractObservation") != case["expectedObservation"]:
        raise VerificationError(f"assertion for {case['id']} is not bound to the frozen observation")
    comparison = assertion.get("comparison")
    if comparison != case["comparisonRules"]["mode"]:
        raise VerificationError(f"assertion for {case['id']} changes the frozen comparison mode")
    pi_path = assertion.get("piArtifact")
    sharp_path = assertion.get("pisharpArtifact")
    if not isinstance(pi_path, str) or not isinstance(sharp_path, str) or pi_path == sharp_path:
        raise VerificationError(f"assertion for {case['id']} lacks distinct Pi and PiSharp artifacts")
    if pi_path not in available_artifacts or sharp_path not in available_artifacts:
        raise VerificationError(f"assertion for {case['id']} refers to undeclared raw artifacts")
    pi_value = _pointer(_read_json(_relative_artifact(evidence_dir, pi_path)), assertion.get("piPointer", ""))
    sharp_value = _pointer(_read_json(_relative_artifact(evidence_dir, sharp_path)), assertion.get("pisharpPointer", ""))
    if assertion.get("piValue") != pi_value or assertion.get("pisharpValue") != sharp_value:
        raise VerificationError(f"assertion for {case['id']} values do not match their artifacts")
    if assertion.get("passed") is not True:
        raise VerificationError(f"assertion for {case['id']} did not pass")
    if comparison == "strict" and pi_value != sharp_value:
        raise VerificationError(f"strict observation differs for {case['id']}")
    if comparison == "equivalent":
        normalized_pi = assertion.get("normalizedPi")
        normalized_sharp = assertion.get("normalizedPiSharp")
        if normalized_pi != normalized_sharp or assertion.get("normalizationEvidence") is None:
            raise VerificationError(f"equivalent observation lacks matching normalized values for {case['id']}")
    if comparison == "intentional-safety-difference":
        if not assertion.get("safetyDifferenceId") or not assertion.get("safetyRegression"):
            raise VerificationError(f"safety difference lacks a named regression for {case['id']}")
        evidence = assertion.get("safetyEvidence")
        if evidence not in available_artifacts:
            raise VerificationError(f"safety difference lacks raw evidence for {case['id']}")
    if comparison == "unresolved":
        raise VerificationError(f"unresolved differences fail acceptance for {case['id']}")


def validate_case_results(results: list[dict[str, Any]], checkpoint_id: str, evidence_dir: Path,
                          *, verify_artifacts: bool = True, require_complete: bool = True) -> dict[str, int]:
    contract = load_contract()
    checkpoint = next((item for item in contract["checkpoints"] if item["id"] == checkpoint_id), None)
    if checkpoint is None:
        raise VerificationError(f"unknown checkpoint: {checkpoint_id}")
    case_by_id = {case["id"]: case for case in checkpoint["caseDefinitions"]}
    seen: set[str] = set()
    counts = {"required": len(case_by_id), "executed": 0, "passed": 0, "failed": 0, "skipped": 0}
    for result in results:
        case_id = result.get("caseId")
        if case_id not in case_by_id or case_id in seen:
            raise VerificationError(f"unknown or duplicate case result: {case_id}")
        seen.add(case_id)
        case = case_by_id[case_id]
        if result.get("checkpointId") != checkpoint_id or result.get("contractExpectedObservation") != case["expectedObservation"]:
            raise VerificationError(f"case {case_id} differs from its frozen checkpoint assignment")
        if result.get("executed") is not True:
            raise VerificationError(f"required case did not execute: {case_id}")
        counts["executed"] += 1
        if result.get("skipped") is True:
            counts["skipped"] += 1
            raise VerificationError(f"required case was skipped: {case_id}")
        if result.get("passed") is not True:
            counts["failed"] += 1
            raise VerificationError(f"required case failed: {case_id}")
        raw = result.get("rawArtifacts")
        if not isinstance(raw, list) or not raw:
            raise VerificationError(f"case has no raw evidence artifacts: {case_id}")
        available: set[str] = set()
        roles: set[str] = set()
        for item in raw:
            relative = item.get("path") if isinstance(item, dict) else None
            role = item.get("role") if isinstance(item, dict) else None
            digest = item.get("sha256") if isinstance(item, dict) else None
            if not relative or not role or not SHA256.fullmatch(str(digest or "")):
                raise VerificationError(f"case has an incomplete raw artifact reference: {case_id}")
            path = _relative_artifact(evidence_dir, relative)
            if verify_artifacts:
                if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
                    raise VerificationError(f"raw artifact missing or hash mismatch: {case_id}/{relative}")
            available.add(relative)
            roles.add(role)
        if checkpoint_id in PROCESS_CHECKPOINTS:
            required_roles = {f"{run}.{stream}" for run in ("pi1", "pi2", "pisharp")
                              for stream in ("stdin", "stdout", "stderr", "exit", "provider.requests")}
            if not required_roles.issubset(roles):
                raise VerificationError(f"case {case_id} lacks repeated Pi/PiSharp process captures")
        assertions = result.get("assertions")
        if not isinstance(assertions, list) or not assertions:
            raise VerificationError(f"case has no observable assertions: {case_id}")
        for assertion in assertions:
            _validate_assertion(assertion, case, evidence_dir, available)
        if checkpoint_id in PROCESS_CHECKPOINTS:
            calibration = result.get("independentPiCalibration")
            if not isinstance(calibration, dict) or calibration.get("matched") is not True:
                raise VerificationError(f"case {case_id} lacks a passing independent Pi calibration")
            pi1_path = calibration.get("pi1Artifact")
            pi2_path = calibration.get("pi2Artifact")
            if pi1_path not in available or pi2_path not in available:
                raise VerificationError(f"case {case_id} lacks independent Pi observation artifacts")
            pi1_value = _read_json(_relative_artifact(evidence_dir, pi1_path))
            pi2_value = _read_json(_relative_artifact(evidence_dir, pi2_path))
            if pi1_value != pi2_value:
                raise VerificationError(f"independent Pi calibration differs for {case_id}")
        counts["passed"] += 1
    if require_complete and seen != set(case_by_id):
        missing = sorted(set(case_by_id) - seen)
        raise VerificationError(f"checkpoint {checkpoint_id} is incomplete; missing {len(missing)} required cases")
    return counts


def validate_q0_authoritative_counts(evidence: dict[str, Any], evidence_dir: Path,
                                    *, expected_source_commit: str | None = None) -> dict[str, int]:
    """Validate Q0's full-suite count schema against the hashed raw dotnet execution."""
    if evidence.get("checkpointId") != "Q0":
        raise VerificationError("full .NET test counts must come from Q0 evidence")
    source_commit = evidence.get("implementationSourceCommit")
    quality_source_commit = evidence.get("qualitySourceCommit")
    if not isinstance(source_commit, str) or not SHA1.fullmatch(source_commit):
        raise VerificationError("Q0 implementation source SHA is missing or malformed")
    if quality_source_commit != source_commit:
        raise VerificationError("Q0 quality source SHA is stale or differs from its implementation SHA")
    if expected_source_commit is not None and source_commit != expected_source_commit:
        raise VerificationError("Q0 implementation source SHA differs from the publication source")

    case_id = "Q0.full-dotnet-suite"
    matches = [item for item in evidence.get("caseResults", [])
               if isinstance(item, dict) and item.get("caseId") == case_id]
    if len(matches) != 1:
        raise VerificationError("Q0 must contain exactly one full .NET suite result")
    case_result = matches[0]
    validate_case_results([case_result], "Q0", evidence_dir, require_complete=False)

    artifacts = case_result.get("rawArtifacts", [])
    observation_refs = [item for item in artifacts if item.get("role") == f"{case_id}.observation"]
    command_refs = [item for item in artifacts if item.get("role") == f"{case_id}.command-1"]
    if len(observation_refs) != 1 or len(command_refs) != 1:
        raise VerificationError("Q0 full .NET suite must reference one observation and one raw command")
    observation_path = _relative_artifact(evidence_dir, observation_refs[0]["path"])
    command_path = _relative_artifact(evidence_dir, command_refs[0]["path"])
    observation = _read_json(observation_path)
    raw_command = _read_json(command_path)
    if not isinstance(observation, dict) or not isinstance(raw_command, dict):
        raise VerificationError("Q0 full .NET suite observation or raw command is not a JSON object")

    if observation.get("accepted") is not True:
        raise VerificationError("Q0 full .NET suite observation is not accepted")
    summary = observation.get("summary")
    if not isinstance(summary, dict) or summary.get("passed") is not True:
        raise VerificationError("Q0 full .NET suite summary.passed must be Boolean true")
    if summary.get("commandsPassed") is not True:
        raise VerificationError("Q0 full .NET suite command gate is not successful")
    test_counts = summary.get("testCounts")
    required_fields = {"passed", "failed", "skipped", "total"}
    if not isinstance(test_counts, dict) or set(test_counts) != required_fields:
        raise VerificationError("Q0 full .NET suite testCounts must contain passed, failed, skipped and total")
    if any(type(test_counts[field]) is not int or test_counts[field] < 0 for field in required_fields):
        raise VerificationError("Q0 testCounts fields must be non-negative JSON integers, excluding Booleans")
    counts = {field: test_counts[field] for field in ("passed", "failed", "skipped", "total")}
    if counts["passed"] + counts["failed"] + counts["skipped"] != counts["total"]:
        raise VerificationError("Q0 testCounts are arithmetically inconsistent")
    if counts["total"] <= 0 or counts["failed"] != 0 or counts["skipped"] != 0:
        raise VerificationError("Q0 successful full-suite acceptance requires total > 0 and zero failures/skips")

    if raw_command.get("argv") != ["dotnet", "test", "PiSharp.slnx"]:
        raise VerificationError("Q0 authoritative counts are not from the full dotnet test PiSharp.slnx suite")
    cwd = raw_command.get("cwd")
    if not isinstance(cwd, str) or not _recorded_checkout_directory_matches(cwd):
        raise VerificationError("Q0 full .NET suite ran from the wrong implementation directory")
    if type(raw_command.get("exitCode")) is not int or raw_command["exitCode"] != 0:
        raise VerificationError("Q0 raw full .NET suite command did not exit successfully")
    stdout, stderr = raw_command.get("stdout"), raw_command.get("stderr")
    if not isinstance(stdout, str) or not isinstance(stderr, str):
        raise VerificationError("Q0 raw full .NET suite output is missing")
    raw_counts = parse_dotnet_test_counts(stdout + stderr)
    if raw_counts is None:
        raise VerificationError("Q0 raw full .NET suite output has no numerical test summary")
    if counts != raw_counts:
        raise VerificationError("Q0 testCounts differ from the hashed raw .NET test output")
    command_paths = observation.get("commandArtifactPaths")
    if command_paths != [command_refs[0]["path"]]:
        raise VerificationError("Q0 observation does not reference its authoritative raw test command")
    return counts


def validate_publication_test_count(publication: Any, quality_counts: dict[str, int]) -> int:
    """Require CI0/PUB0 to publish only Q0's authoritative numeric full-suite total."""
    if not isinstance(publication, dict):
        raise VerificationError("publication evidence is missing its Q0 test count")
    count = publication.get("fullDotnetTestCount")
    if type(count) is not int or count != quality_counts.get("total"):
        raise VerificationError("publication test count differs from authoritative Q0 testCounts.total")
    return count


def validate_evidence_header(evidence: dict[str, Any], *, current_head: str | None = None,
                             require_clean_worktree: bool = True) -> None:
    """Validate source identities before interpreting individual case results."""
    contract = load_contract()
    if evidence.get("contractSha256") != FROZEN_CONTRACT_SHA256:
        raise VerificationError("evidence uses a changed or stale frozen contract")
    if evidence.get("piCommit") != PINNED_PI_SHA:
        raise VerificationError("evidence uses the wrong pinned Pi SHA")
    source_sha = evidence.get("implementationSourceCommit")
    head_sha = current_head or evidence.get("finalHeadCommit")
    if not isinstance(source_sha, str) or not isinstance(head_sha, str):
        raise VerificationError("evidence is missing implementation source or tested head SHA")
    evidence_head = evidence.get("finalHeadCommit")
    if current_head is not None:
        if not isinstance(evidence_head, str) or not SHA1.fullmatch(evidence_head):
            raise VerificationError("evidence final head must be a full lowercase Git SHA")
        final_head_is_ancestor = subprocess.run(
            ["git", "merge-base", "--is-ancestor", evidence_head, current_head],
            cwd=ROOT, capture_output=True, text=True,
        )
        if final_head_is_ancestor.returncode != 0:
            raise VerificationError("evidence final head is not an ancestor of the current repository head")
    validate_source_identity(source_sha, head_sha)
    if require_clean_worktree:
        validate_clean_source_tree()
    if evidence.get("pisharpBaseCommit") != contract["sourcePins"]["pisharpBaseCommit"]:
        raise VerificationError("evidence is based on the wrong PiSharp merge commit")


def required_ci_jobs(workflow_path: Path = CI_WORKFLOW_PATH) -> set[str]:
    """Read top-level job IDs from the exact workflow source used by the repo."""
    try:
        lines = workflow_path.read_text(encoding="utf-8").splitlines()
    except OSError as error:
        raise VerificationError(f"cannot read CI workflow source: {error}") from error
    try:
        jobs_index = next(index for index, line in enumerate(lines) if line == "jobs:")
    except StopIteration as error:
        raise VerificationError("CI workflow has no jobs section") from error
    jobs: set[str] = set()
    for line in lines[jobs_index + 1:]:
        match = re.fullmatch(r"  ([A-Za-z0-9_-]+):", line)
        if match:
            jobs.add(match.group(1))
        elif line and not line.startswith(" "):
            break
    if not jobs:
        raise VerificationError("CI workflow has no top-level jobs")
    return jobs


def validate_ci_evidence(ci_evidence: Any, *, final_head_sha: str, milestone_base_sha: str,
                         cwd: Path = ROOT) -> None:
    """Require exact-head automatic CI plus a published full-corpus evidence artifact."""
    if not isinstance(ci_evidence, dict):
        raise VerificationError("exact-head CI evidence is missing")
    runs = ci_evidence.get("runs")
    if not isinstance(runs, list) or len(runs) != 2:
        raise VerificationError("CI evidence must contain exactly one push and one pull_request run")
    by_event: dict[str, dict[str, Any]] = {}
    run_ids: set[str] = set()
    for run in runs:
        if not isinstance(run, dict):
            raise VerificationError("CI run record must be an object")
        event = run.get("event")
        run_id = run.get("runId")
        if event not in {"push", "pull_request"} or event in by_event:
            raise VerificationError("CI evidence includes an unexpected, manual or duplicate workflow event")
        if not isinstance(run_id, str) or not run_id or run_id in run_ids:
            raise VerificationError("CI evidence has a missing or duplicate workflow run ID")
        by_event[event] = run
        run_ids.add(run_id)
    if set(by_event) != {"push", "pull_request"}:
        raise VerificationError("automatic push and pull_request CI runs are both required")
    expected_jobs = required_ci_jobs()
    for event, run in by_event.items():
        if run.get("headSha") != final_head_sha:
            raise VerificationError(f"{event} CI run is not for the final PR head")
        if run.get("conclusion") != "success":
            raise VerificationError(f"{event} CI run did not succeed")
        jobs = run.get("jobs")
        if not isinstance(jobs, dict) or set(jobs) != expected_jobs:
            raise VerificationError(f"{event} CI job set is incomplete or differs from the workflow")
        if any(conclusion != "success" for conclusion in jobs.values()):
            raise VerificationError(f"{event} CI has a failed, skipped or cancelled mandatory job")
    pull_request_base = by_event["pull_request"].get("baseSha")
    if not isinstance(pull_request_base, str) or not SHA1.fullmatch(pull_request_base):
        raise VerificationError("pull_request CI evidence lacks a full base SHA")
    if not SHA1.fullmatch(milestone_base_sha):
        raise VerificationError("frozen milestone base SHA is invalid")
    ancestor = subprocess.run(
        ["git", "merge-base", "--is-ancestor", milestone_base_sha, pull_request_base],
        cwd=cwd, capture_output=True, text=True,
    )
    if ancestor.returncode != 0:
        raise VerificationError("pull_request CI base does not contain the prerequisite merge")
    validate_rpc_parity_artifact(ci_evidence.get("rpcParityEvidence"), final_head_sha=final_head_sha)


def validate_rpc_parity_artifact(record: Any, *, final_head_sha: str) -> None:
    """Check the exact-head evidence workflow and its immutable GitHub artifact metadata."""
    if not isinstance(record, dict):
        raise VerificationError("published RPC parity evidence workflow is missing")
    run_id = record.get("runId")
    if (record.get("workflowName") != "RPC parity evidence" or record.get("event") != "push"
            or record.get("headSha") != final_head_sha or record.get("conclusion") != "success"
            or not isinstance(run_id, str) or not run_id.isdigit()):
        raise VerificationError("RPC parity evidence workflow is not a successful exact-head push run")
    if record.get("jobs") != {"generate-and-publish": "success"}:
        raise VerificationError("RPC parity evidence workflow job did not succeed")
    artifact = record.get("artifact")
    if not isinstance(artifact, dict):
        raise VerificationError("RPC parity evidence workflow did not publish its artifact")
    if (artifact.get("name") != f"rpc-parity-evidence-{final_head_sha}"
            or artifact.get("headSha") != final_head_sha or artifact.get("expired") is not False
            or type(artifact.get("artifactId")) is not int or artifact["artifactId"] <= 0
            or type(artifact.get("sizeInBytes")) is not int or artifact["sizeInBytes"] <= 0):
        raise VerificationError("RPC parity evidence artifact is stale, expired, or empty")
    digest = artifact.get("digest")
    if not isinstance(digest, str) or not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        raise VerificationError("RPC parity evidence artifact lacks a valid immutable digest")


def _github_json(arguments: list[str], *, cwd: Path = ROOT) -> Any:
    result = subprocess.run(["gh", *arguments], cwd=cwd, capture_output=True, text=True)
    if result.returncode != 0:
        raise VerificationError(f"GitHub query failed: {' '.join(arguments)}: {result.stderr.strip()}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise VerificationError(f"GitHub query returned invalid JSON: {' '.join(arguments)}") from error


def validate_live_ci_evidence(ci_evidence: Any, *, final_head_sha: str, cwd: Path = ROOT) -> None:
    """Re-read each accepted workflow run and its job conclusions from GitHub."""
    if not isinstance(ci_evidence, dict) or not isinstance(ci_evidence.get("runs"), list):
        raise VerificationError("live CI verification has no run records")
    for recorded in ci_evidence["runs"]:
        run_id = recorded.get("runId")
        if not isinstance(run_id, str) or not run_id.isdigit():
            raise VerificationError("CI run ID is not a numeric GitHub Actions ID")
        live = _github_json(["run", "view", run_id, "--json", "databaseId,event,headSha,conclusion,jobs"], cwd=cwd)
        live_jobs = {job.get("name"): job.get("conclusion") for job in live.get("jobs", [])}
        recorded_jobs = recorded.get("jobs")
        if (str(live.get("databaseId")) != run_id or live.get("event") != recorded.get("event")
                or live.get("headSha") != final_head_sha or live.get("headSha") != recorded.get("headSha")
                or live.get("conclusion") != recorded.get("conclusion")
                or live_jobs != recorded_jobs):
            raise VerificationError(f"GitHub Actions run {run_id} differs from the exact-head CI receipt")
    record = ci_evidence.get("rpcParityEvidence")
    validate_rpc_parity_artifact(record, final_head_sha=final_head_sha)
    run_id = record["runId"]
    live_run = _github_json([
        "run", "view", run_id,
        "--json", "databaseId,event,headSha,conclusion,workflowName,jobs",
    ], cwd=cwd)
    live_jobs = {job.get("name"): job.get("conclusion") for job in live_run.get("jobs", [])}
    if (str(live_run.get("databaseId")) != run_id or live_run.get("event") != "push"
            or live_run.get("headSha") != final_head_sha or live_run.get("conclusion") != "success"
            or live_run.get("workflowName") != "RPC parity evidence"
            or live_jobs != record.get("jobs")):
        raise VerificationError("RPC parity evidence workflow changed or is not on the exact final head")
    artifact_record = record["artifact"]
    artifact_list = _github_json([
        "api", f"repos/PhilipGB/pisharp/actions/runs/{run_id}/artifacts",
    ], cwd=cwd)
    matching = [item for item in artifact_list.get("artifacts", [])
                if item.get("id") == artifact_record.get("artifactId")]
    if len(matching) != 1:
        raise VerificationError("published RPC parity evidence artifact is missing or ambiguous")
    live_artifact = matching[0]
    artifact_head = live_artifact.get("workflow_run", {}).get("head_sha")
    if (live_artifact.get("name") != artifact_record.get("name")
            or live_artifact.get("digest") != artifact_record.get("digest")
            or live_artifact.get("size_in_bytes") != artifact_record.get("sizeInBytes")
            or live_artifact.get("expired") != artifact_record.get("expired")
            or artifact_head != final_head_sha):
        raise VerificationError("published RPC parity evidence artifact differs from its recorded digest or source head")


def validate_live_publication_evidence(publication: Any, *, final_head_sha: str,
                                      cwd: Path = ROOT) -> None:
    """Verify the recorded PR remains the current open draft on the latest main head."""
    if not isinstance(publication, dict):
        raise VerificationError("publication receipt is missing")
    number = publication.get("number")
    if not isinstance(number, int) or number <= 0:
        raise VerificationError("publication receipt has no valid PR number")
    pull = _github_json(["api", f"repos/PhilipGB/pisharp/pulls/{number}"], cwd=cwd)
    main = _github_json(["api", "repos/PhilipGB/pisharp/branches/main"], cwd=cwd)
    head = pull.get("head", {})
    base = pull.get("base", {})
    author = pull.get("user", {}).get("login")
    if (pull.get("state") != "open" or pull.get("merged") is True or pull.get("draft") is not True
            or head.get("sha") != final_head_sha or head.get("ref") != "milestone/rpc-protocol-command-parity"
            or base.get("ref") != "main" or base.get("sha") != main.get("commit", {}).get("sha")
            or base.get("sha") != publication.get("baseSha") or author != publication.get("author")):
        raise VerificationError("published PR is closed, merged, not a draft, or stale against main/head")
    if publication.get("url") != pull.get("html_url") or publication.get("headSha") != head.get("sha"):
        raise VerificationError("publication URL or head SHA differs from the live pull request")
    body = pull.get("body") or ""
    body_digest = hashlib.sha256(body.encode("utf-8")).hexdigest()
    if body_digest != publication.get("bodySha256"):
        raise VerificationError("PR description changed after the publication receipt was captured")
    contract = load_contract()
    source_sha = publication.get("implementationSourceCommit")
    mode_counts = {
        mode: sum(case["comparisonRules"]["mode"] == mode for case in contract["cases"])
        for mode in ("strict", "equivalent", "intentional-safety-difference")
    }
    dotnet_count = publication.get("fullDotnetTestCount")
    if type(dotnet_count) is not int or dotnet_count <= 0:
        raise VerificationError("publication fullDotnetTestCount must be a positive JSON integer")
    mandatory_tokens = [
        "24/24", "252/252", PINNED_PI_SHA, BASE_SHA, str(source_sha), final_head_sha,
        str(publication.get("pushRunId")), str(publication.get("pullRequestRunId")),
        f"strict: {mode_counts['strict']}", f"equivalent: {mode_counts['equivalent']}",
        f"intentional safety differences: {mode_counts['intentional-safety-difference']}",
        f"{dotnet_count:,}/{dotnet_count:,}" if isinstance(dotnet_count, int) else "",
        "20 credential-gated", "unverified", "managed-package parity corpus",
        str(publication.get("rpcParityEvidenceRunId", "")),
        str(publication.get("rpcParityEvidenceArtifactName", "")),
        str(publication.get("rpcParityEvidenceArtifactDigest", "")),
    ]
    if not source_sha or any(token.lower() not in body.lower() for token in mandatory_tokens):
        raise VerificationError("PR description omits required exact results, pins, CI IDs or limitations")
    if not all(case["comparisonRules"]["mode"] in {"strict", "equivalent", "intentional-safety-difference"}
               for case in contract["cases"]):
        raise VerificationError("publication includes unresolved comparison cases")
    reviews = _github_json(["api", f"repos/PhilipGB/pisharp/pulls/{number}/reviews"], cwd=cwd)
    if any(review.get("user", {}).get("login") == author and review.get("state") == "APPROVED"
           for review in reviews):
        raise VerificationError("single contributor PR has a self-approval recorded")


def validate_v1_control_evidence(control: Any, evidence_dir: Path) -> dict[str, int]:
    """Re-run the synthetic positive/mutation corpus and validate its captured report."""
    if not isinstance(control, dict):
        raise VerificationError("V1 verifier-control evidence is missing")
    checkpoint = next(item for item in load_contract()["checkpoints"] if item["id"] == "V1")
    case_ids = [case["id"] for case in checkpoint["caseDefinitions"]]
    positive_id = "V1.valid-positive-control"
    if control.get("caseIds") != case_ids:
        raise VerificationError("V1 control report does not enumerate the frozen case IDs in order")
    if control.get("positiveControlAccepted") is not True:
        raise VerificationError("V1 positive control was not accepted")
    negative_ids = [case_id for case_id in case_ids if case_id != positive_id]
    if control.get("negativeControlsRejected") != negative_ids:
        raise VerificationError("V1 report does not record every frozen negative control as rejected")
    report = control.get("report")
    if not isinstance(report, dict):
        raise VerificationError("V1 report artifact metadata is missing")
    relative = report.get("path")
    digest = report.get("sha256")
    if not isinstance(relative, str) or not SHA256.fullmatch(str(digest or "")):
        raise VerificationError("V1 report artifact reference is invalid")
    path = _relative_artifact(evidence_dir, relative)
    try:
        report_text = path.read_text(encoding="utf-8")
    except OSError as error:
        raise VerificationError(f"V1 report artifact is missing: {relative}") from error
    if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
        raise VerificationError("V1 report artifact hash mismatch")
    if "Ran " not in report_text or "OK" not in report_text:
        raise VerificationError("V1 report does not contain a successful test run")
    command = [sys.executable, "-m", "unittest", "discover", "-s", str(HERE), "-p", "test_*.py"]
    environment = dict(os.environ)
    previous_pythonpath = environment.get("PYTHONPATH")
    environment["PYTHONPATH"] = str(HERE) + (os.pathsep + previous_pythonpath if previous_pythonpath else "")
    rerun = subprocess.run(command, cwd=ROOT, env=environment, capture_output=True, text=True)
    rerun_text = rerun.stdout + rerun.stderr
    if rerun.returncode != 0 or "Ran " not in rerun_text or "OK" not in rerun_text:
        raise VerificationError("V1 positive/mutation control suite did not pass when independently rerun")
    return {"required": len(case_ids), "executed": len(case_ids), "passed": len(case_ids), "failed": 0, "skipped": 0}


def validate_e0_evidence(evidence: dict[str, Any], evidence_dir: Path, *, current_head: str) -> None:
    """Independently validate every source-bound checkpoint and its complete raw-artifact manifest."""
    root = evidence_dir.parent
    source_commit = evidence.get("implementationSourceCommit")
    source_bound = evidence.get("sourceBoundEvidence")
    if not isinstance(source_commit, str) or not isinstance(source_bound, dict):
        raise VerificationError("E0 evidence lacks its source-bound checkpoint manifest")
    if source_bound.get("implementationSourceCommit") != source_commit:
        raise VerificationError("E0 source-bound manifest has a different implementation SHA")
    required_checkpoints = ["P0", "P1", "V0", "V1", *[f"R{i}" for i in range(1, 16)], "D0", "Q0"]
    checkpoint_records = source_bound.get("checkpoints")
    if not isinstance(checkpoint_records, list) or [item.get("checkpointId") for item in checkpoint_records] != required_checkpoints:
        raise VerificationError("E0 checkpoint manifest is incomplete or out of contract order")
    manifest_ref = source_bound.get("artifactManifest")
    if not isinstance(manifest_ref, dict):
        raise VerificationError("E0 raw-artifact manifest reference is missing")
    manifest_rel = manifest_ref.get("path")
    manifest_sha = manifest_ref.get("sha256")
    if not isinstance(manifest_rel, str) or not SHA256.fullmatch(str(manifest_sha or "")):
        raise VerificationError("E0 raw-artifact manifest reference is malformed")
    manifest_path = _relative_artifact(root, manifest_rel)
    if not manifest_path.is_file() or hashlib.sha256(manifest_path.read_bytes()).hexdigest() != manifest_sha:
        raise VerificationError("E0 raw-artifact manifest is missing or has a hash mismatch")
    manifest = _read_json(manifest_path)
    if manifest.get("implementationSourceCommit") != source_commit or manifest.get("piCommit") != PINNED_PI_SHA:
        raise VerificationError("E0 artifact manifest has a different source pin")
    if manifest.get("checkpoints") != checkpoint_records:
        raise VerificationError("E0 source evidence and raw-artifact manifest list different checkpoints")

    expected_artifacts: list[dict[str, str]] = []
    for record in checkpoint_records:
        checkpoint_id = record.get("checkpointId")
        relative = record.get("evidencePath")
        digest = record.get("evidenceSha256")
        if not isinstance(relative, str) or not SHA256.fullmatch(str(digest or "")):
            raise VerificationError(f"E0 {checkpoint_id} evidence reference is malformed")
        evidence_path = _relative_artifact(root, relative)
        if not evidence_path.is_file() or hashlib.sha256(evidence_path.read_bytes()).hexdigest() != digest:
            raise VerificationError(f"E0 {checkpoint_id} evidence is missing or has a hash mismatch")
        child_dir = evidence_path.parent
        child = _read_json(evidence_path)
        if record.get("implementationSourceCommit") != child.get("implementationSourceCommit"):
            raise VerificationError(f"E0 {checkpoint_id} manifest source SHA differs from its evidence")
        validate_evidence_header(child, current_head=current_head)
        if child.get("implementationSourceCommit") != source_commit:
            raise VerificationError(f"E0 {checkpoint_id} was not tested at the shared implementation source")
        if checkpoint_id == "Q0":
            validate_q0_authoritative_counts(child, child_dir, expected_source_commit=source_commit)
        if checkpoint_id == "V1":
            validate_v1_control_evidence(child.get("v1ControlEvidence"), child_dir)
        else:
            if checkpoint_id in PROCESS_CHECKPOINTS:
                validate_process_build_evidence(child.get("build"), child_dir)
            validate_case_results(child.get("caseResults", []), checkpoint_id, child_dir)
        for result in child.get("caseResults", []):
            for artifact in result.get("rawArtifacts", []):
                expected_artifacts.append({
                    "checkpointId": checkpoint_id,
                    "caseId": result["caseId"],
                    "path": f"{checkpoint_id}/{artifact['path']}",
                    "role": artifact["role"],
                    "sha256": artifact["sha256"],
                })
        if checkpoint_id == "V1":
            control = child["v1ControlEvidence"]["report"]
            expected_artifacts.append({
                "checkpointId": "V1", "caseId": "V1", "path": f"V1/{control['path']}",
                "role": "verifier-control-report", "sha256": control["sha256"],
            })

    actual_artifacts = manifest.get("artifacts")
    artifact_key = lambda item: (
        item.get("checkpointId"), item.get("caseId"), item.get("path"), item.get("role"), item.get("sha256"),
    )
    if not isinstance(actual_artifacts, list) or sorted(map(artifact_key, actual_artifacts)) != sorted(map(artifact_key, expected_artifacts)):
        raise VerificationError("E0 artifact manifest differs from the checkpoint raw-artifact references")
    for artifact in actual_artifacts:
        relative = artifact.get("path")
        digest = artifact.get("sha256")
        if not isinstance(relative, str) or not SHA256.fullmatch(str(digest or "")):
            raise VerificationError("E0 manifest contains a malformed artifact record")
        path = _relative_artifact(root, relative)
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise VerificationError(f"E0 manifest artifact is missing or has a hash mismatch: {relative}")


def validate_process_build_evidence(build: Any, evidence_dir: Path) -> None:
    if not isinstance(build, dict) or build.get("exitCode") != 0:
        raise VerificationError("PiSharp process evidence lacks a successful source build")
    if not SHA256.fullmatch(str(build.get("assemblySha256", ""))):
        raise VerificationError("PiSharp build evidence lacks an assembly digest")
    log_path = build.get("logPath")
    log_sha = build.get("logSha256")
    if not isinstance(log_path, str) or not SHA256.fullmatch(str(log_sha or "")):
        raise VerificationError("PiSharp build evidence lacks a build log and digest")
    path = _relative_artifact(evidence_dir, log_path)
    if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != log_sha:
        raise VerificationError("PiSharp build log is missing or its digest changed")


def validate_source_identity(source_sha: str, head_sha: str, *, cwd: Path = ROOT) -> None:
    if not SHA1.fullmatch(source_sha) or not SHA1.fullmatch(head_sha):
        raise VerificationError("implementation source and head must be full lowercase Git SHAs")
    ancestor = subprocess.run(["git", "merge-base", "--is-ancestor", source_sha, head_sha], cwd=cwd)
    if ancestor.returncode != 0:
        raise VerificationError("implementation source is not an ancestor of the tested head")
    changed = subprocess.run(
        ["git", "diff", "--quiet", source_sha, head_sha, "--", *IMPLEMENTATION_SOURCE_PATHS],
        cwd=cwd,
    )
    if changed.returncode != 0:
        raise VerificationError("evidence is stale: production, test or parity sources changed after implementation source")


def current_implementation_source_commit(*, cwd: Path = ROOT) -> str:
    """Return the latest commit that changed implementation, tests, parity tools or Linux CI."""
    result = subprocess.run(
        ["git", "log", "-1", "--format=%H", "HEAD", "--", *IMPLEMENTATION_SOURCE_PATHS],
        cwd=cwd, capture_output=True, text=True,
    )
    source_sha = result.stdout.strip()
    if result.returncode != 0 or not SHA1.fullmatch(source_sha):
        raise VerificationError("cannot determine the implementation source commit")
    return source_sha


def validate_clean_source_tree(*, cwd: Path = ROOT) -> None:
    changed = subprocess.run(
        ["git", "status", "--porcelain", "--", "src", "tests", "tools/parity", ".github/workflows"],
        cwd=cwd, capture_output=True, text=True,
    )
    if changed.returncode != 0:
        raise VerificationError("cannot inspect source worktree status")
    if changed.stdout.strip():
        raise VerificationError("evidence cannot be accepted with uncommitted production, test, parity or workflow changes")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    contract_parser = subparsers.add_parser("contract", help="validate the frozen contract and source inventory")
    contract_parser.add_argument("--pi-checkout", type=Path, default=ROOT / "artifacts/parity/pi-ownership-oracle")
    check_parser = subparsers.add_parser("check", help="validate checkpoint results in an evidence directory")
    check_parser.add_argument("--checkpoint", required=True)
    check_parser.add_argument("--case-id")
    check_parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        contract = load_contract()
        if args.command == "contract":
            validate_pi_checkout(args.pi_checkout)
            print(f"contract valid: {len(contract['checkpoints'])} checkpoints, {len(contract['requiredCaseIds'])} cases; Pi {PINNED_PI_SHA}")
            return 0
        evidence_path = args.evidence_dir / "evidence.json"
        evidence = _read_json(evidence_path)
        current_head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
        validate_evidence_header(evidence, current_head=current_head)
        if args.checkpoint == "Q0":
            validate_q0_authoritative_counts(evidence, args.evidence_dir)
        if args.checkpoint == "V1":
            if args.case_id:
                raise VerificationError("V1 evidence is validated as a complete control checkpoint")
            counts = validate_v1_control_evidence(evidence.get("v1ControlEvidence"), args.evidence_dir)
            print(json.dumps(counts, sort_keys=True))
            return 0
        if args.checkpoint == "E0":
            validate_e0_evidence(evidence, args.evidence_dir, current_head=current_head)
        if args.checkpoint in {"CI0", "PUB0"}:
            q0_dir = args.evidence_dir.parent / "Q0"
            q0_evidence = _read_json(q0_dir / "evidence.json")
            q0_counts = validate_q0_authoritative_counts(
                q0_evidence, q0_dir,
                expected_source_commit=evidence.get("implementationSourceCommit"),
            )
            validate_publication_test_count(evidence.get("publicationEvidence"), q0_counts)
            validate_ci_evidence(
                evidence.get("ciEvidence"),
                final_head_sha=evidence["finalHeadCommit"],
                milestone_base_sha=contract["sourcePins"]["pisharpBaseCommit"],
            )
            validate_live_ci_evidence(evidence.get("ciEvidence"), final_head_sha=evidence["finalHeadCommit"])
            validate_live_publication_evidence(
                evidence.get("publicationEvidence"), final_head_sha=evidence["finalHeadCommit"],
            )
        if args.checkpoint in PROCESS_CHECKPOINTS:
            validate_process_build_evidence(evidence.get("build"), args.evidence_dir)
        results = evidence.get("caseResults", [])
        if args.case_id:
            if not args.case_id.startswith(f"{args.checkpoint}."):
                raise VerificationError("case ID does not belong to the requested checkpoint")
            results = [result for result in results if result.get("caseId") == args.case_id]
            if len(results) != 1:
                raise VerificationError("evidence does not contain exactly the requested case ID")
        counts = validate_case_results(
            results, args.checkpoint, args.evidence_dir,
            require_complete=args.case_id is None,
        )
        print(json.dumps(counts, sort_keys=True))
        return 0
    except (VerificationError, OSError, KeyError, TypeError) as error:
        print(f"verification failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
