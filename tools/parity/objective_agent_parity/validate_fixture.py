#!/usr/bin/env python3
"""Validate the ENV-PARSER-002 sequential-defect baseline and record its checksum."""
from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path

from common import (
    STATE_ROOT,
    aggregate_manifest,
    clean_build_outputs,
    file_manifest,
    parse_trx,
    run_command,
    sha256_file,
    write_json,
)
from fixture_transforms import correct_defect_a, correct_defect_b

ROOT = Path(__file__).resolve().parents[3]
FIXTURE = ROOT / "tools/parity/objective_agent_parity/fixture/ENV-PARSER-002"
OUTPUT = ROOT / "tools/parity/objective_agent_parity/baseline-002.json"
VISIBLE_TEST = "Fixture.Tests.EnvIntegrationTests.ParsesEmbeddedEqualsAndPreservesUnknownPlaceholders"


def dotnet_env() -> dict[str, str]:
    import os
    env = {name: os.environ[name] for name in
           ("PATH", "HOME", "DOTNET_ROOT", "LD_LIBRARY_PATH", "NUGET_PACKAGES",
            "NUGET_HTTP_CACHE_PATH", "DOTNET_CLI_HOME") if name in os.environ}
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    return env


def run_tests(workspace: Path) -> dict:
    trx = workspace / "baseline.trx"
    result = run_command(
        ["dotnet", "test", "Fixture.slnx", "--nologo", "--no-restore",
         "--logger", "trx;LogFileName=baseline.trx"],
        cwd=workspace, env=dotnet_env(), timeout=300.0)
    report = next(iter(sorted(workspace.rglob("baseline.trx"))), None)
    tests = parse_trx(report) if report else {}
    excerpt = ""
    combined = result["stdout"] + result["stderr"]
    for marker in ("Assert.Equal() Failure", "Failed Fixture.Tests", "Error Message"):
        index = combined.find(marker)
        if index >= 0:
            excerpt = combined[max(0, index - 200): index + 1600]
            break
    if not excerpt:
        excerpt = combined[-2000:]
    return dict(exitCode=result["exitCode"], timedOut=result["timedOut"],
                tests=tests, failureExcerpt=excerpt,
                passed=sum(1 for value in tests.values() if value == "passed"),
                failed=sum(1 for value in tests.values() if value == "failed"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    arguments = parser.parse_args()
    STATE_ROOT.mkdir(parents=True, exist_ok=True)
    workspace = STATE_ROOT / "baseline-validation-ENV-PARSER-002"
    if workspace.exists():
        shutil.rmtree(workspace)
    shutil.copytree(FIXTURE, workspace, symlinks=False)
    clean_build_outputs(workspace)

    restore = run_command(["dotnet", "restore", "Fixture.slnx", "--nologo"],
                          cwd=workspace, env=dotnet_env(), timeout=300.0)
    build = run_command(["dotnet", "build", "Fixture.slnx", "--nologo", "--no-restore"],
                        cwd=workspace, env=dotnet_env(), timeout=300.0)
    initial = run_tests(workspace)

    env_file = workspace / "src/Fixture.Core/EnvFile.cs"
    interpolator = workspace / "src/Fixture.Core/EnvInterpolator.cs"
    env_file.write_text(correct_defect_a(env_file.read_text()))
    after_a = run_tests(workspace)

    interpolator.write_text(correct_defect_b(interpolator.read_text()))
    after_both = run_tests(workspace)

    files = file_manifest(FIXTURE)
    result = {
        "fixtureId": "ENV-PARSER-002",
        "fixtureChecksum": aggregate_manifest(files),
        "files": files,
        "restore": dict(exitCode=restore["exitCode"], timedOut=restore["timedOut"]),
        "build": dict(exitCode=build["exitCode"], timedOut=build["timedOut"],
                      stdoutTail=(build["stdout"] + build["stderr"])[-2000:]),
        "initial": initial,
        "afterDefectA": after_a,
        "afterBothDefects": after_both,
        "transformChecksums": {
            "defectA": sha256_file(env_file),
            "defectB": sha256_file(interpolator),
            "baselineDefectA": files["src/Fixture.Core/EnvFile.cs"],
            "baselineDefectB": files["src/Fixture.Core/EnvInterpolator.cs"],
        },
        "intentionalDefects": [
            "EnvFile.Parse splits at every equals sign and discards assignments without exactly two parts.",
            "EnvInterpolator.Expand replaces unresolved placeholders with an empty string.",
        ],
        "expectedInitialFailure": VISIBLE_TEST,
        "sequentialExpectation": {
            "initial": "fails in the parsing assertion",
            "afterDefectA": "fails in the interpolation assertion",
            "afterBothDefects": "passes",
        },
    }
    write_json(arguments.output, result)
    print(json.dumps(dict(checksum=result["fixtureChecksum"],
                          initial=initial["tests"],
                          afterDefectA=after_a["tests"],
                          afterBothDefects=after_both["tests"]), indent=2))
    ok = (restore["exitCode"] == 0 and build["exitCode"] == 0
          and initial["failed"] == 1 and initial["tests"].get(VISIBLE_TEST) == "failed"
          and after_a["failed"] == 1 and after_a["tests"].get(VISIBLE_TEST) == "failed"
          and after_both["exitCode"] == 0
          and after_both["tests"].get(VISIBLE_TEST) == "passed")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
