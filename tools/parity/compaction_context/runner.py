#!/usr/bin/env python3
"""Run a frozen compaction checkpoint and retain command-level baseline evidence."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any

import verify

ROOT = Path(__file__).resolve().parents[3]

FILTERS = {
    "C1": "FullyQualifiedName~CompactionTests|FullyQualifiedName~RpcCompactionProcessTests",
    "C2": "FullyQualifiedName~CompactionTests|FullyQualifiedName~AutoCompactionPolicy",
    "C3": "FullyQualifiedName~CompactionTests|FullyQualifiedName~CompactionNestedToolCallsTests",
    "C4": "FullyQualifiedName~PiContextUsageEstimatorTests|FullyQualifiedName~CompactionTests",
    "C5": "FullyQualifiedName~ProviderOverflowLoopbackTests|FullyQualifiedName~CompactionTests",
    "C6": "FullyQualifiedName~InFlightContextBudgetTests|FullyQualifiedName~CompactionTests|FullyQualifiedName~CompactionNestedToolCallsTests",
    "C7": "FullyQualifiedName~CompactionTests|FullyQualifiedName~RpcCompactionProcessTests",
    "C8": "FullyQualifiedName~CompactionTests|FullyQualifiedName~PiJsonlSessionInterchangeTests|FullyQualifiedName~CrashRecoveryProcessTests",
    "C9": "FullyQualifiedName~ConversationTreeTests|FullyQualifiedName~TerminalTreePickerTests|FullyQualifiedName~NativeExtensionTreeEventTests|FullyQualifiedName~RpcSessionProcessTests",
    "C10": "FullyQualifiedName~CompactionTests|FullyQualifiedName~RpcSessionStatsProjectorTests",
    "C11": "FullyQualifiedName~RpcCompactionProcessTests|FullyQualifiedName~RpcSessionStatsProjectorTests",
    "C12": "FullyQualifiedName~PiSharpExtensionCompactionTests|FullyQualifiedName~NativeExtensionCompactionEventTests|FullyQualifiedName~NativeExtensionTreeEventTests",
    "C13": "FullyQualifiedName~CompactionTests|FullyQualifiedName~RpcCompactionProcessTests|FullyQualifiedName~CrashRecoveryProcessTests",
}


def _git(*args: str) -> str:
    result = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=True)
    return result.stdout.strip()


def _write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def _source_tree_is_clean() -> bool:
    changed = subprocess.run(["git", "diff", "--quiet", "HEAD", "--", "src", "tests", "tools/parity"],
                             cwd=ROOT, capture_output=True)
    untracked = subprocess.run(["git", "ls-files", "--others", "--exclude-standard", "--",
                                "src", "tests", "tools/parity"], cwd=ROOT, capture_output=True, text=True)
    return changed.returncode == 0 and not untracked.stdout.strip()


def _commands(checkpoint: str, pi: Path | None) -> list[list[str]]:
    if checkpoint == "V1":
        return [[sys.executable, "-m", "unittest", "discover", "-s",
                 "tools/parity/compaction_context", "-p", "test_*.py"]]
    if checkpoint == "V0":
        return [[sys.executable, "tools/parity/compaction_context/verify.py", "contract", "--check"]]
    if checkpoint in FILTERS:
        return [["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--no-restore",
                 "--filter", FILTERS[checkpoint]]]
    if checkpoint == "Q0":
        return [["dotnet", "format", "PiSharp.slnx", "--verify-no-changes"],
                ["dotnet", "build", "PiSharp.slnx", "--warnaserror"],
                ["dotnet", "test", "PiSharp.slnx"]]
    if checkpoint in {"C1", "C2", "C3", "C4", "C5", "C6", "C7", "C8", "C9", "C10", "C11", "C12", "C13", "D0"} and pi is None:
        raise verify.VerificationError(f"checkpoint {checkpoint} requires --pi")
    return []


def run_checkpoint(checkpoint_id: str, evidence_dir: Path, pi_path: Path | None) -> int:
    contract = verify.load_contract()
    checkpoint = next((item for item in contract["checkpoints"] if item["id"] == checkpoint_id), None)
    if checkpoint is None:
        raise verify.VerificationError(f"unknown checkpoint: {checkpoint_id}")
    if pi_path is not None:
        head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=pi_path,
                              capture_output=True, text=True, check=True).stdout.strip()
        if head != verify.PINNED_PI_SHA:
            raise verify.VerificationError(f"Pi checkout is {head}; expected {verify.PINNED_PI_SHA}")
    output = evidence_dir / "runs" / checkpoint_id
    output.mkdir(parents=True, exist_ok=True)
    if checkpoint_id in {"P0", "P1", "V0", "V1"}:
        if pi_path is None:
            raise verify.VerificationError(f"checkpoint {checkpoint_id} requires --pi")
        command = [sys.executable, "tools/parity/compaction_context/baseline_checkpoints.py",
                   "--checkpoint", checkpoint_id, "--pi", str(pi_path), "--evidence-dir", str(output)]
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, check=False)
        (output / "runner.stdout.log").write_text(completed.stdout, encoding="utf-8")
        (output / "runner.stderr.log").write_text(completed.stderr, encoding="utf-8")
        if completed.stdout:
            print(completed.stdout, end="")
        if completed.stderr:
            print(completed.stderr, file=sys.stderr, end="")
        return completed.returncode
    commands = _commands(checkpoint_id, pi_path)
    results = []
    for index, command in enumerate(commands, start=1):
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                   env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4"}, check=False)
        stdout_path = output / f"command-{index}.stdout.log"
        stderr_path = output / f"command-{index}.stderr.log"
        stdout_path.write_text(completed.stdout, encoding="utf-8")
        stderr_path.write_text(completed.stderr, encoding="utf-8")
        results.append({
            "argv": command,
            "exitCode": completed.returncode,
            "stdout": {"path": str(stdout_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stdout.encode()).hexdigest()},
            "stderr": {"path": str(stderr_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stderr.encode()).hexdigest()},
        })
        if completed.returncode != 0:
            break
    differential_report = None
    if checkpoint_id == "C5" and results and results[-1]["exitCode"] == 0:
        differential_dir = output / "differential"
        command = [sys.executable, "tools/parity/compaction_context/c5_oracle.py",
                   "--pi", str(pi_path), "--evidence-dir", str(differential_dir)]
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                   env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4"}, check=False)
        stdout_path = output / f"command-{len(results) + 1}.stdout.log"
        stderr_path = output / f"command-{len(results) + 1}.stderr.log"
        stdout_path.write_text(completed.stdout, encoding="utf-8")
        stderr_path.write_text(completed.stderr, encoding="utf-8")
        results.append({
            "argv": command,
            "exitCode": completed.returncode,
            "stdout": {"path": str(stdout_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stdout.encode()).hexdigest()},
            "stderr": {"path": str(stderr_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stderr.encode()).hexdigest()},
        })
        if completed.returncode == 0:
            differential_report = verify._json(differential_dir / "c5-case-results.json")
            differential_report["verifierCounts"] = verify.validate_case_results(
                differential_report["cases"], checkpoint_id, differential_dir, verify_artifacts=True)

    if checkpoint_id == "C1" and results and results[-1]["exitCode"] == 0:
        differential_dir = output / "differential"
        command = [sys.executable, "tools/parity/compaction_context/c1_oracle.py",
                   "--pi", str(pi_path), "--evidence-dir", str(differential_dir)]
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                   env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4"}, check=False)
        stdout_path = output / f"command-{len(results) + 1}.stdout.log"
        stderr_path = output / f"command-{len(results) + 1}.stderr.log"
        stdout_path.write_text(completed.stdout, encoding="utf-8")
        stderr_path.write_text(completed.stderr, encoding="utf-8")
        results.append({
            "argv": command,
            "exitCode": completed.returncode,
            "stdout": {"path": str(stdout_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stdout.encode()).hexdigest()},
            "stderr": {"path": str(stderr_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stderr.encode()).hexdigest()},
        })
        if completed.returncode == 0:
            differential_report = verify._json(differential_dir / "c1-case-results.json")
            differential_report["verifierCounts"] = verify.validate_case_results(
                differential_report["cases"], checkpoint_id, differential_dir, verify_artifacts=True)

    if checkpoint_id == "C2" and results and results[-1]["exitCode"] == 0:
        differential_dir = output / "differential"
        command = [sys.executable, "tools/parity/compaction_context/c2_oracle.py",
                   "--pi", str(pi_path), "--evidence-dir", str(differential_dir)]
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                   env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4"}, check=False)
        stdout_path = output / f"command-{len(results) + 1}.stdout.log"
        stderr_path = output / f"command-{len(results) + 1}.stderr.log"
        stdout_path.write_text(completed.stdout, encoding="utf-8")
        stderr_path.write_text(completed.stderr, encoding="utf-8")
        results.append({
            "argv": command,
            "exitCode": completed.returncode,
            "stdout": {"path": str(stdout_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stdout.encode()).hexdigest()},
            "stderr": {"path": str(stderr_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stderr.encode()).hexdigest()},
        })
        if completed.returncode == 0:
            differential_report = verify._json(differential_dir / "c2-case-results.json")
            differential_report["verifierCounts"] = verify.validate_case_results(
                differential_report["cases"], checkpoint_id, differential_dir, verify_artifacts=True)

    if checkpoint_id == "C3" and results and results[-1]["exitCode"] == 0:
        differential_dir = output / "differential"
        command = [sys.executable, "tools/parity/compaction_context/c3_oracle.py",
                   "--pi", str(pi_path), "--evidence-dir", str(differential_dir)]
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                   env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4"}, check=False)
        stdout_path = output / f"command-{len(results) + 1}.stdout.log"
        stderr_path = output / f"command-{len(results) + 1}.stderr.log"
        stdout_path.write_text(completed.stdout, encoding="utf-8")
        stderr_path.write_text(completed.stderr, encoding="utf-8")
        results.append({
            "argv": command,
            "exitCode": completed.returncode,
            "stdout": {"path": str(stdout_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stdout.encode()).hexdigest()},
            "stderr": {"path": str(stderr_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stderr.encode()).hexdigest()},
        })
        if completed.returncode == 0:
            differential_report = verify._json(differential_dir / "c3-case-results.json")
            differential_report["verifierCounts"] = verify.validate_case_results(
                differential_report["cases"], checkpoint_id, differential_dir, verify_artifacts=True)

    if checkpoint_id == "C4" and results and results[-1]["exitCode"] == 0:
        differential_dir = output / "differential"
        command = [sys.executable, "tools/parity/compaction_context/c4_oracle.py",
                   "--pi", str(pi_path), "--evidence-dir", str(differential_dir)]
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                   env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4"}, check=False)
        stdout_path = output / f"command-{len(results) + 1}.stdout.log"
        stderr_path = output / f"command-{len(results) + 1}.stderr.log"
        stdout_path.write_text(completed.stdout, encoding="utf-8")
        stderr_path.write_text(completed.stderr, encoding="utf-8")
        results.append({
            "argv": command,
            "exitCode": completed.returncode,
            "stdout": {"path": str(stdout_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stdout.encode()).hexdigest()},
            "stderr": {"path": str(stderr_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stderr.encode()).hexdigest()},
        })
        if completed.returncode == 0:
            differential_report = verify._json(differential_dir / "c4-case-results.json")
            differential_report["verifierCounts"] = verify.validate_case_results(
                differential_report["cases"], checkpoint_id, differential_dir, verify_artifacts=True)

    if checkpoint_id == "C6" and results and results[-1]["exitCode"] == 0:
        differential_dir = output / "differential"
        command = [sys.executable, "tools/parity/compaction_context/c6_oracle.py",
                   "--pi", str(pi_path), "--evidence-dir", str(differential_dir)]
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                   env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4"}, check=False)
        stdout_path = output / f"command-{len(results) + 1}.stdout.log"
        stderr_path = output / f"command-{len(results) + 1}.stderr.log"
        stdout_path.write_text(completed.stdout, encoding="utf-8")
        stderr_path.write_text(completed.stderr, encoding="utf-8")
        results.append({
            "argv": command,
            "exitCode": completed.returncode,
            "stdout": {"path": str(stdout_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stdout.encode()).hexdigest()},
            "stderr": {"path": str(stderr_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stderr.encode()).hexdigest()},
        })
        if completed.returncode == 0:
            differential_report = verify._json(differential_dir / "c6-case-results.json")
            differential_report["verifierCounts"] = verify.validate_case_results(
                differential_report["cases"], checkpoint_id, differential_dir, verify_artifacts=True)

    if checkpoint_id == "C7" and results and results[-1]["exitCode"] == 0:
        differential_dir = output / "differential"
        command = [sys.executable, "tools/parity/compaction_context/c7_oracle.py",
                   "--pi", str(pi_path), "--evidence-dir", str(differential_dir)]
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                   env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4"}, check=False)
        stdout_path = output / f"command-{len(results) + 1}.stdout.log"
        stderr_path = output / f"command-{len(results) + 1}.stderr.log"
        stdout_path.write_text(completed.stdout, encoding="utf-8")
        stderr_path.write_text(completed.stderr, encoding="utf-8")
        results.append({
            "argv": command,
            "exitCode": completed.returncode,
            "stdout": {"path": str(stdout_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stdout.encode()).hexdigest()},
            "stderr": {"path": str(stderr_path.relative_to(evidence_dir)),
                       "sha256": hashlib.sha256(completed.stderr.encode()).hexdigest()},
        })
        if completed.returncode == 0:
            differential_report = verify._json(differential_dir / "c7-case-results.json")
            differential_report["verifierCounts"] = verify.validate_case_results(
                differential_report["cases"], checkpoint_id, differential_dir, verify_artifacts=True)

    produced_cases = differential_report["cases"] if differential_report is not None else []
    source_clean = _source_tree_is_clean()
    source_sha = _git("rev-parse", "HEAD") if source_clean else None
    required_count = len(checkpoint["requiredCases"])
    validated_counts = differential_report["verifierCounts"] if differential_report is not None else {}
    passed_count = validated_counts.get("passed", 0)
    failed_count = validated_counts.get("failed", 0)
    skipped_count = validated_counts.get("skipped", 0)
    all_cases_produced = len(produced_cases) == required_count
    report = {
        "checkpointId": checkpoint_id,
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA if pi_path is not None else None,
        "pisharpSourceSha": source_sha,
        "sourceTreeClean": source_clean,
        "commands": results,
        "requiredCaseCount": required_count,
        "caseEvidenceProduced": len(produced_cases),
        "executedCaseCount": sum(item.get("executed") is True for item in produced_cases),
        "failedCaseCount": failed_count,
        "skippedCaseCount": skipped_count,
        "caseResultsPath": (str((output / "differential" / f"{checkpoint_id.lower()}-case-results.json").relative_to(evidence_dir))
                            if differential_report is not None else None),
        "status": "passed" if all_cases_produced and passed_count == required_count
            and failed_count == 0 and skipped_count == 0 and source_clean else "incomplete",
        "reason": (None if all_cases_produced and passed_count == required_count and source_clean else
                   f"Produced {len(produced_cases)}/{required_count} case artifacts; remaining required adapters or a clean committed source tree are required."),
    }
    _write_json(output / "checkpoint-run.json", report)
    print(f"Recorded {report['caseEvidenceProduced']}/{report['requiredCaseCount']} case artifacts for {checkpoint_id}; "
          f"status {report['status']}.")
    return 0 if report["status"] == "passed" else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", choices=["run"])
    parser.add_argument("--checkpoint", required=True)
    parser.add_argument("--pi", type=Path)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        return run_checkpoint(args.checkpoint, args.evidence_dir.resolve(),
                              None if args.pi is None else args.pi.resolve())
    except (verify.VerificationError, OSError, subprocess.CalledProcessError) as error:
        print(f"checkpoint runner failed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
