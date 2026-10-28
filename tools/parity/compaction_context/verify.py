#!/usr/bin/env python3
"""Validate the frozen compaction/context parity contract and its evidence."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
CONTRACT_PATH = HERE / "contract.json"
FROZEN_CONTRACT_SHA256 = "c774819bdb78f537ba75c80243891187128f7fae7d1e392c31a1bd24868a2cf6"
PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"
REQUIRED_CHECKPOINT_IDS = (
    "P0", "P1", "V0", "V1", "C1", "C2", "C3", "C4", "C5", "C6", "C7",
    "C8", "C9", "C10", "C11", "C12", "C13", "D0", "Q0", "E0", "CI0", "PUB0",
)
SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")


class VerificationError(ValueError):
    """Evidence failed a mandatory contract assertion."""


def _json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise VerificationError(f"cannot read JSON from {path}: {error}") from error


def load_contract(path: Path = CONTRACT_PATH) -> dict[str, Any]:
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != FROZEN_CONTRACT_SHA256:
        raise VerificationError(f"frozen contract digest changed: {digest}")
    contract = _json(path)
    checkpoints = contract.get("checkpoints")
    if not isinstance(checkpoints, list):
        raise VerificationError("contract checkpoints must be a list")
    checkpoint_ids = [item.get("id") for item in checkpoints if isinstance(item, dict)]
    if tuple(checkpoint_ids) != REQUIRED_CHECKPOINT_IDS:
        raise VerificationError("contract checkpoint IDs or order differ from the frozen set")
    required_ids: list[str] = []
    for item in checkpoints:
        if not item.get("runner", {}).get("command") or not item.get("runner", {}).get("verifier"):
            raise VerificationError(f"{item['id']} has no executable runner and verifier")
        if not item.get("expectedObservations") or not item.get("passFailCriteria"):
            raise VerificationError(f"{item['id']} lacks expectations or pass/fail criteria")
        if not item.get("evidencePath") or not item.get("sourceReferences"):
            raise VerificationError(f"{item['id']} lacks evidence paths or source references")
        cases = item.get("requiredCases")
        if not isinstance(cases, list) or not cases:
            raise VerificationError(f"{item['id']} has no required cases")
        for case in cases:
            if not case.get("id") or not case.get("expectedObservation") or not case.get("evidencePath"):
                raise VerificationError(f"{item['id']} has an incomplete required case")
            required_ids.append(case["id"])
    if len(required_ids) != len(set(required_ids)):
        raise VerificationError("contract contains duplicate required case IDs")
    if contract.get("checkpointCount") != len(checkpoints):
        raise VerificationError("contract checkpoint count does not match the frozen list")
    if contract.get("requiredCaseCount") != len(required_ids):
        raise VerificationError("contract case count does not match required case IDs")
    if contract.get("requiredCheckpointIds") != list(REQUIRED_CHECKPOINT_IDS):
        raise VerificationError("contract required checkpoint IDs differ from the frozen set")
    if contract.get("sourcePins", {}).get("piCommit") != PINNED_PI_SHA:
        raise VerificationError("contract Pi source pin changed")
    return contract


def required_cases(contract: dict[str, Any]) -> dict[str, tuple[dict[str, Any], dict[str, Any]]]:
    result: dict[str, tuple[dict[str, Any], dict[str, Any]]] = {}
    for checkpoint in contract["checkpoints"]:
        for case in checkpoint["requiredCases"]:
            result[case["id"]] = (checkpoint, case)
    return result


def _assertion_passes(assertion: dict[str, Any], evidence_dir: Path, verify_artifacts: bool) -> bool:
    mode = assertion.get("comparison", "strict")
    expected = assertion.get("expected")
    actual = assertion.get("actual")
    if mode == "strict":
        return assertion.get("passed") is True and expected == actual
    if mode == "equivalent":
        return (assertion.get("passed") is True and assertion.get("normalizedExpected") is not None
                and assertion.get("normalizedExpected") == assertion.get("normalizedActual"))
    if mode == "intentional-safety-difference":
        path = assertion.get("safetyEvidence")
        regression = assertion.get("safetyRegression")
        if not assertion.get("passed") or not path or not regression:
            return False
        if verify_artifacts:
            return (evidence_dir / path).is_file()
        return True
    return False


def validate_source_identity(source_sha: str, head_sha: str, *, cwd: Path = ROOT) -> None:
    if not SHA_PATTERN.fullmatch(source_sha) or not SHA_PATTERN.fullmatch(head_sha):
        raise VerificationError("source/head identity must use full lowercase Git SHAs")
    ancestor = subprocess.run(["git", "merge-base", "--is-ancestor", source_sha, head_sha],
                              cwd=cwd, capture_output=True, text=True)
    if ancestor.returncode != 0:
        raise VerificationError(f"implementation source {source_sha} is not an ancestor of head {head_sha}")
    changed = subprocess.run(["git", "diff", "--quiet", source_sha, head_sha, "--", "src", "tests", "tools/parity"],
                             cwd=cwd, capture_output=True, text=True)
    if changed.returncode != 0:
        raise VerificationError("implementation evidence is stale: production, test, or parity sources changed")


def validate_evidence(evidence: dict[str, Any], evidence_dir: Path, *,
                      verify_artifacts: bool = True, verify_source: bool = False,
                      current_head: str | None = None) -> dict[str, int]:
    contract = load_contract()
    provenance = evidence.get("provenance")
    if not isinstance(provenance, dict):
        raise VerificationError("evidence has no provenance")
    if provenance.get("contractSha256") != FROZEN_CONTRACT_SHA256:
        raise VerificationError("evidence was produced for a different contract")
    if provenance.get("piSha") != PINNED_PI_SHA:
        raise VerificationError("evidence Pi source pin does not match the required commit")
    source_sha = provenance.get("pisharpSourceSha")
    if not isinstance(source_sha, str) or not SHA_PATTERN.fullmatch(source_sha):
        raise VerificationError("evidence has no full PiSharp source SHA")
    if verify_source:
        head_sha = current_head or subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT,
                                                    capture_output=True, text=True, check=True).stdout.strip()
        validate_source_identity(source_sha, head_sha)
        dirty = subprocess.run(["git", "diff", "--quiet", "HEAD", "--", "src", "tests", "tools/parity"],
                               cwd=ROOT, capture_output=True)
        untracked = subprocess.run(["git", "ls-files", "--others", "--exclude-standard", "--",
                                    "src", "tests", "tools/parity"], cwd=ROOT, capture_output=True, text=True)
        if dirty.returncode != 0 or untracked.stdout.strip():
            raise VerificationError("current source tree differs from the evidence tree")

    required = required_cases(contract)
    cases = evidence.get("cases")
    if not isinstance(cases, list):
        raise VerificationError("evidence cases must be a list")
    actual: dict[str, dict[str, Any]] = {}
    for item in cases:
        if not isinstance(item, dict) or not isinstance(item.get("caseId"), str):
            raise VerificationError("case evidence has no caseId")
        case_id = item["caseId"]
        if case_id in actual:
            raise VerificationError(f"duplicate case result: {case_id}")
        if case_id not in required:
            raise VerificationError(f"unknown case result: {case_id}")
        actual[case_id] = item
    missing = sorted(set(required) - set(actual))
    if missing:
        raise VerificationError(f"missing required cases: {', '.join(missing[:8])}")

    counts = {"required": len(required), "executed": 0, "failed": 0, "skipped": 0}
    for case_id, (checkpoint, contract_case) in required.items():
        result = actual[case_id]
        if result.get("checkpointId") != checkpoint["id"]:
            raise VerificationError(f"case {case_id} is assigned to the wrong checkpoint")
        if result.get("contractExpectedObservation") != contract_case["expectedObservation"]:
            raise VerificationError(f"case {case_id} expected observation differs from the frozen contract")
        if result.get("executed") is not True:
            raise VerificationError(f"required case did not execute: {case_id}")
        counts["executed"] += 1
        if result.get("skipped") is True:
            counts["skipped"] += 1
            raise VerificationError(f"required case was skipped: {case_id}")
        if result.get("passed") is not True:
            counts["failed"] += 1
            raise VerificationError(f"required case failed: {case_id}")
        assertions = result.get("assertions")
        if not isinstance(assertions, list) or not assertions:
            raise VerificationError(f"case has no observable assertions: {case_id}")
        if not all(isinstance(assertion, dict) and _assertion_passes(assertion, evidence_dir, verify_artifacts)
                   for assertion in assertions):
            counts["failed"] += 1
            raise VerificationError(f"case contains a failed or mismatched observation: {case_id}")
        artifacts = result.get("rawArtifacts")
        if not isinstance(artifacts, list) or not artifacts:
            raise VerificationError(f"case has no raw evidence references: {case_id}")
        if verify_artifacts:
            for artifact in artifacts:
                relative = artifact.get("path") if isinstance(artifact, dict) else None
                if not relative or not (evidence_dir / relative).is_file():
                    raise VerificationError(f"case {case_id} references missing raw evidence: {relative}")
                if not SHA_PATTERN.fullmatch(str(artifact.get("sha256", ""))):
                    raise VerificationError(f"case {case_id} raw evidence has no SHA-256")

    checkpoint_results = evidence.get("checkpointResults")
    if not isinstance(checkpoint_results, list):
        raise VerificationError("checkpointResults must be a list")
    expected_checkpoints = {item["id"]: item for item in contract["checkpoints"]}
    observed_checkpoints: dict[str, dict[str, Any]] = {}
    for item in checkpoint_results:
        if not isinstance(item, dict) or item.get("checkpointId") not in expected_checkpoints:
            raise VerificationError("checkpoint result is missing or unknown")
        checkpoint_id = item["checkpointId"]
        if checkpoint_id in observed_checkpoints:
            raise VerificationError(f"duplicate checkpoint result: {checkpoint_id}")
        observed_checkpoints[checkpoint_id] = item
    if set(observed_checkpoints) != set(expected_checkpoints):
        missing_checkpoints = sorted(set(expected_checkpoints) - set(observed_checkpoints))
        raise VerificationError(f"missing checkpoint results: {', '.join(missing_checkpoints)}")
    for checkpoint_id, checkpoint in expected_checkpoints.items():
        expected_count = len(checkpoint["requiredCases"])
        result = observed_checkpoints[checkpoint_id]
        cp_cases = [actual[item["id"]] for item in checkpoint["requiredCases"]]
        executed_count = sum(item.get("executed") is True for item in cp_cases)
        failed_count = sum(item.get("passed") is not True for item in cp_cases)
        skipped_count = sum(item.get("skipped") is True for item in cp_cases)
        if (result.get("requiredCaseCount") != expected_count or
                result.get("executedCaseCount") != executed_count or
                result.get("failedCaseCount") != failed_count or
                result.get("skippedCaseCount") != skipped_count):
            raise VerificationError(f"checkpoint {checkpoint_id} counts do not match the frozen contract")
        if result.get("status") != "passed" or failed_count or skipped_count or executed_count != expected_count:
            raise VerificationError(f"checkpoint did not pass every frozen case: {checkpoint_id}")
    counts["checkpoints"] = len(expected_checkpoints)
    return counts


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    contract_parser = sub.add_parser("contract", help="validate the frozen denominator and source pin")
    contract_parser.add_argument("--check", action="store_true")
    check_parser = sub.add_parser("check", help="validate an explicit evidence directory")
    check_parser.add_argument("--evidence-dir", type=Path, required=True)
    check_parser.add_argument("--skip-artifact-files", action="store_true", help=argparse.SUPPRESS)
    check_parser.add_argument("--skip-source-check", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    try:
        contract = load_contract()
        if args.command == "contract":
            print(f"Frozen contract valid: {contract['checkpointCount']} checkpoints, "
                  f"{contract['requiredCaseCount']} required cases; SHA-256 {FROZEN_CONTRACT_SHA256}")
            return 0
        evidence = _json(args.evidence_dir / "evidence.json")
        counts = validate_evidence(evidence, args.evidence_dir,
                                   verify_artifacts=not args.skip_artifact_files,
                                   verify_source=not args.skip_source_check)
        print(f"Evidence passed: {counts['checkpoints']}/{contract['checkpointCount']} checkpoints, "
              f"{counts['executed']}/{counts['required']} required cases, 0 failed, 0 skipped")
        return 0
    except (VerificationError, OSError, subprocess.CalledProcessError) as error:
        print(f"verification failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
