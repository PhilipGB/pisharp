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
    report = {
        "checkpointId": checkpoint_id,
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA if pi_path is not None else None,
        "pisharpSourceSha": _git("rev-parse", "HEAD"),
        "commands": results,
        "requiredCaseCount": len(checkpoint["requiredCases"]),
        "caseEvidenceProduced": 0,
        "status": "incomplete",
        "reason": "Command-level baseline ran; per-case provider, state, persistence, oracle-calibration, and assertion adapters are still required. This report cannot pass a checkpoint.",
    }
    _write_json(output / "checkpoint-run.json", report)
    print(f"Recorded command-level baseline for {checkpoint_id}; 0/{report['requiredCaseCount']} case artifacts; checkpoint remains incomplete.")
    return 1


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
