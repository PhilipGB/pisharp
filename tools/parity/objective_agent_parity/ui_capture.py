#!/usr/bin/env python3
"""Capture the deterministic terminal fixture and score UI parity."""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

from common import write_json
from ui_score import score_ui

ROOT = Path(__file__).resolve().parents[3]
FIXTURE = ROOT / "tools/parity/terminal/fixtures/objective-agent-progress.json"
RUNNER = ROOT / "tools/parity/terminal/run.py"


def run_terminal(*, pi: Path, pisharp: Path, output: Path, calibrate: bool) -> dict:
    command = [sys.executable, str(RUNNER), "--pi", str(pi), "--pisharp", str(pisharp),
               "--fixture", str(FIXTURE), "--output", str(output)]
    if calibrate:
        command.insert(-2, "--calibrate")
    if output.exists():
        import shutil
        shutil.rmtree(output)
    completed = subprocess.run(command, capture_output=True, text=True, check=False)
    (output.parent / f"{output.name}.stdout.log").write_text(completed.stdout)
    (output.parent / f"{output.name}.stderr.log").write_text(completed.stderr)
    report = json.loads((output / "report.json").read_text()) if (output / "report.json").is_file() else None
    return dict(command=command, exitCode=completed.returncode, report=report)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, default=Path("/home/philip/Projects/pi-pinned"))
    parser.add_argument("--pisharp", type=Path,
                        default=ROOT / "src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll")
    parser.add_argument("--output", type=Path,
                        default=ROOT / "artifacts/parity/objective-agent-ui")
    parser.add_argument("--calibration-directory", type=Path, default=None)
    parser.add_argument("--comparison-directory", type=Path, default=None)
    arguments = parser.parse_args()

    if arguments.calibration_directory and arguments.comparison_directory:
        calibration = arguments.calibration_directory
        comparison = arguments.comparison_directory
        result = score_ui(calibration, comparison)
    else:
        arguments.output.mkdir(parents=True, exist_ok=True)
        calibration = arguments.output / "calibration"
        comparison = arguments.output / "comparison"
        calibration_run = run_terminal(pi=arguments.pi, pisharp=arguments.pisharp,
                                       output=calibration, calibrate=True)
        result = {
            "fixture": "objective-agent-progress",
            "calibration": {
                "directory": str(calibration),
                "exitCode": calibration_run["exitCode"],
                "match": bool(calibration_run["report"] and calibration_run["report"].get("match")),
            },
            "status": "inconclusive" if calibration_run["exitCode"] != 0 else "capturing",
        }
        if calibration_run["exitCode"] == 0:
            comparison_run = run_terminal(pi=arguments.pi, pisharp=arguments.pisharp,
                                          output=comparison, calibrate=False)
            result = score_ui(calibration, comparison)
            result["calibration"]["exitCode"] = calibration_run["exitCode"]
            result["comparison"]["exitCode"] = comparison_run["exitCode"]
        else:
            result["reason"] = "Same-Pi calibration failed; cross-product comparison is not valid."
    write_json(arguments.output / "ui-report.json", result)
    print(json.dumps({key: result.get(key) for key in
                      ("status", "calibration", "progressPoints", "visualPoints", "total", "max")},
                     indent=2))
    return 0 if result.get("status") == "scored" else 1


if __name__ == "__main__":
    raise SystemExit(main())
