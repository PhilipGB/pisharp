#!/usr/bin/env python3
"""Fail-closed entry point for the frozen RPC process acceptance cases.

Scenario adapters are added only after the contract and inventory are committed.
Until an adapter exists, `run` returns failure rather than manufacturing a pass.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

from verify import VerificationError, load_contract, validate_pi_checkout


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
