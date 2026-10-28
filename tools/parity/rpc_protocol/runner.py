#!/usr/bin/env python3
"""Fail-closed entry point for the frozen RPC process acceptance cases.

Scenario adapters are added only after the contract and inventory are committed.
Until an adapter exists, `run` returns failure rather than manufacturing a pass.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

from verify import (
    BASE_SHA,
    FROZEN_CONTRACT_SHA256,
    PINNED_PI_SHA,
    VerificationError,
    load_contract,
    validate_clean_source_tree,
    validate_case_results,
    validate_pi_checkout,
    validate_v1_control_evidence,
)
from process_oracle import run_process_case, scenario_case_ids

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent


def run_v1_controls(evidence_dir: Path) -> dict[str, int]:
    """Execute the synthetic verifier-control suite and bind its report to source pins."""
    validate_clean_source_tree()
    head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
    evidence_dir.mkdir(parents=True, exist_ok=True)
    report_path = evidence_dir / "v1-controls.log"
    environment = dict(os.environ)
    previous_pythonpath = environment.get("PYTHONPATH")
    environment["PYTHONPATH"] = str(HERE) + (os.pathsep + previous_pythonpath if previous_pythonpath else "")
    command = [sys.executable, "-m", "unittest", "discover", "-s", str(HERE), "-p", "test_*.py", "-v"]
    result = subprocess.run(command, cwd=ROOT, env=environment, capture_output=True, text=True)
    report_text = result.stdout + result.stderr
    report_path.write_text(report_text, encoding="utf-8")
    if result.returncode != 0 or "Ran " not in report_text or "OK" not in report_text:
        raise VerificationError(f"V1 control suite failed; inspect {report_path}")
    contract = load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == "V1")
    case_ids = [case["id"] for case in checkpoint["caseDefinitions"]]
    control_evidence = {
        "caseIds": case_ids,
        "positiveControlAccepted": True,
        "negativeControlsRejected": [case_id for case_id in case_ids if case_id != "V1.valid-positive-control"],
        "report": {"path": report_path.name, "sha256": hashlib.sha256(report_path.read_bytes()).hexdigest()},
    }
    evidence = {
        "schemaVersion": 1,
        "contractSha256": FROZEN_CONTRACT_SHA256,
        "piCommit": PINNED_PI_SHA,
        "implementationSourceCommit": head,
        "finalHeadCommit": head,
        "pisharpBaseCommit": BASE_SHA,
        "v1ControlEvidence": control_evidence,
    }
    (evidence_dir / "evidence.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    return validate_v1_control_evidence(control_evidence, evidence_dir)


def run_process_cases(checkpoint_id: str, selected_cases: list[dict], pi_checkout: Path,
                      evidence_dir: Path, *, require_complete: bool) -> int:
    validate_clean_source_tree()
    unsupported = [case["id"] for case in selected_cases if case["id"] not in scenario_case_ids()]
    if unsupported:
        raise VerificationError(f"no process scenario adapters are registered for: {', '.join(unsupported)}")
    if (evidence_dir / "evidence.json").exists():
        raise VerificationError(f"evidence already exists; choose a new run directory: {evidence_dir}")
    evidence_dir.mkdir(parents=True, exist_ok=True)
    build_env = dict(os.environ)
    cache_root = Path(tempfile.gettempdir()) / "pisharp-rpc-dotnet-build"
    for name, subdir in (
        ("XDG_DATA_HOME", "xdg-data"), ("XDG_CACHE_HOME", "xdg-cache"),
        ("XDG_RUNTIME_DIR", "xdg-runtime"), ("DOTNET_CLI_HOME", "dotnet-home"),
        ("NUGET_HTTP_CACHE_PATH", "nuget-http"), ("NUGET_PLUGINS_CACHE_PATH", "nuget-plugins"),
    ):
        target = cache_root / subdir
        target.mkdir(parents=True, exist_ok=True)
        build_env[name] = str(target)
    build_env.update({"DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1",
                      "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1"})
    build = subprocess.run(
        ["dotnet", "build", "src/PiSharp.Cli/PiSharp.Cli.csproj", "--no-restore", "--warnaserror",
         "-m:1", "/nodeReuse:false", "-p:NuGetAudit=false"],
        cwd=ROOT, env=build_env, capture_output=True, text=True,
    )
    build_log = build.stdout + build.stderr
    (evidence_dir / "pisharp-build.log").write_text(build_log, encoding="utf-8")
    if build.returncode != 0:
        raise VerificationError(f"PiSharp build failed before process capture; inspect {evidence_dir / 'pisharp-build.log'}")
    assembly = ROOT / "src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll"
    if not assembly.is_file():
        raise VerificationError(f"built PiSharp process assembly not found: {assembly}")
    source_head = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True,
    ).stdout.strip()
    case_results = [run_process_case(case, pi=pi_checkout, evidence_dir=evidence_dir) for case in selected_cases]
    evidence = {
        "schemaVersion": 1,
        "contractSha256": FROZEN_CONTRACT_SHA256,
        "piCommit": PINNED_PI_SHA,
        "implementationSourceCommit": source_head,
        "finalHeadCommit": source_head,
        "pisharpBaseCommit": BASE_SHA,
        "checkpointId": checkpoint_id,
        "build": {
            "command": "dotnet build src/PiSharp.Cli/PiSharp.Cli.csproj --no-restore --warnaserror",
            "exitCode": build.returncode,
            "assemblySha256": hashlib.sha256(assembly.read_bytes()).hexdigest(),
            "logPath": "pisharp-build.log",
            "logSha256": hashlib.sha256((evidence_dir / "pisharp-build.log").read_bytes()).hexdigest(),
        },
        "caseResults": case_results,
    }
    (evidence_dir / "evidence.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    failures = {result["caseId"]: result["executionFailures"] for result in case_results if not result["passed"]}
    try:
        counts = validate_case_results(
            case_results, checkpoint_id, evidence_dir,
            require_complete=require_complete,
        )
    except VerificationError as error:
        if failures:
            print(json.dumps({"caseFailures": failures}, ensure_ascii=False, sort_keys=True), file=sys.stderr)
        raise error
    print(json.dumps(counts, sort_keys=True))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    list_parser = subparsers.add_parser("list", help="list frozen case IDs")
    list_parser.add_argument("--checkpoint")
    run_parser = subparsers.add_parser("run", help="execute frozen case(s), failing closed without an adapter")
    selection = run_parser.add_mutually_exclusive_group(required=True)
    selection.add_argument("--case-id")
    selection.add_argument("--checkpoint")
    run_parser.add_argument("--pi-checkout", type=Path, required=True)
    run_parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        contract = load_contract()
        if args.command == "list":
            cases = contract["cases"]
            if args.checkpoint:
                checkpoint = next((item for item in contract["checkpoints"] if item["id"] == args.checkpoint), None)
                if checkpoint is None:
                    raise VerificationError(f"unknown checkpoint: {args.checkpoint}")
                cases = checkpoint["caseDefinitions"]
            for case in cases:
                print(case["id"])
            return 0
        validate_pi_checkout(args.pi_checkout)
        if args.case_id:
            case = next((item for item in contract["cases"] if item["id"] == args.case_id), None)
            if case is None:
                raise VerificationError(f"unknown case ID: {args.case_id}")
            selected = [case]
        else:
            checkpoint = next((item for item in contract["checkpoints"] if item["id"] == args.checkpoint), None)
            if checkpoint is None:
                raise VerificationError(f"unknown checkpoint: {args.checkpoint}")
            selected = checkpoint["caseDefinitions"]
        if args.checkpoint == "V1" or (args.case_id and args.case_id.startswith("V1.")):
            counts = run_v1_controls(args.evidence_dir)
            print(json.dumps(counts, sort_keys=True))
            return 0
        checkpoint_id = args.checkpoint if args.checkpoint else args.case_id.split(".", 1)[0]
        if selected and all(case["id"] in scenario_case_ids() for case in selected):
            return run_process_cases(
                checkpoint_id, selected, args.pi_checkout, args.evidence_dir,
                require_complete=args.checkpoint is not None,
            )
        missing = [case["id"] for case in selected]
        raise VerificationError(
            f"no process scenario adapters are registered yet for {len(missing)} selected case(s); "
            "the contract is frozen and these cases remain unexecuted"
        )
    except (VerificationError, OSError) as error:
        print(f"RPC acceptance run failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
