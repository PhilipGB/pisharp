#!/usr/bin/env python3
"""Run the fixed ENV-PARSER-001 coding task against Pi and PiSharp.

Runs are sequential, isolated and bounded by a monotonic ten-minute deadline.
Raw JSONL traces and session documents are written below artifacts/parity/;
credentials are never copied into artifact directories.
"""
from __future__ import annotations

import argparse
import fcntl
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

from common import (
    BASELINE_PATH,
    FIXTURE_ID,
    LOCK_PATH,
    STATE_ROOT,
    WORKSPACE,
    aggregate_manifest,
    clean_build_outputs,
    collect_secrets,
    file_manifest,
    is_rate_limited,
    load_baseline,
    run_agent_process,
    run_command,
    sha256_file,
    redact,
    write_json,
)

TASK_PROMPT = (
    "The project contains a failing unit test. Investigate the failure, identify the underlying defect "
    "and make the smallest appropriate production-code correction. Preserve the documented behaviour, "
    "do not modify tests, and run `dotnet test` to verify the result. Report what you changed and whether "
    "the tests passed."
)
ROOT = Path(__file__).resolve().parents[3]
PI_SOURCE = Path("/home/philip/Projects/pi-pinned")
PISHARP_PROJECT = ROOT / "src/PiSharp.Cli/PiSharp.Cli.csproj"
PISHARP_DLL = ROOT / "src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll"
PI_AGENT = Path("/home/philip/.pi/agent")
PISHARP_AGENT = Path("/home/philip/.pisharp/agent")
AUTH_PATHS = [PI_AGENT / "auth.json", PISHARP_AGENT / "auth.json"]
TOOLS = ["read", "bash", "edit", "write", "grep", "find", "ls"]
RUN_TIMEOUT_SECONDS = 600.0


def _git(path: Path, *arguments: str) -> str:
    result = subprocess.run(["git", "-C", str(path), *arguments], capture_output=True, text=True, check=False)
    return result.stdout.strip() if result.returncode == 0 else ""


def source_revisions() -> dict[str, Any]:
    return {
        "pi": {
            "path": str(PI_SOURCE),
            "revision": _git(PI_SOURCE, "rev-parse", "HEAD"),
            "dirty": bool(_git(PI_SOURCE, "status", "--porcelain")),
        },
        "pisharp": {
            "path": str(ROOT),
            "revision": _git(ROOT, "rev-parse", "HEAD"),
            "dirty": bool(_git(ROOT, "status", "--porcelain")),
        },
    }


def build_pisharp() -> dict[str, Any]:
    result = run_command(["dotnet", "build", str(PISHARP_PROJECT), "-c", "Debug", "--nologo"],
                         cwd=ROOT, timeout=600.0)
    result["dll"] = str(PISHARP_DLL)
    result["dllChecksum"] = sha256_file(PISHARP_DLL) if PISHARP_DLL.exists() else None
    return result


def agent_environment(app: str, agent_directory: Path, sessions: Path) -> dict[str, str]:
    names = ["PATH", "HOME", "DOTNET_ROOT", "LD_LIBRARY_PATH"]
    env = {name: os.environ[name] for name in names if name in os.environ}
    env.update(
        LANG="C.UTF-8", LC_ALL="C.UTF-8", TERM="xterm-256color", COLORTERM="truecolor",
        DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", PI_OFFLINE="1",
    )
    if app == "pi":
        env["PI_CODING_AGENT_DIR"] = str(agent_directory)
    else:
        env["PISHARP_AGENT_DIR"] = str(agent_directory)
        env["PISHARP_SESSION_DIR"] = str(sessions)
    return env


def prepare_agent_directory(app: str, directory: Path) -> None:
    if directory.exists():
        shutil.rmtree(directory)
    directory.mkdir(parents=True, mode=0o700)
    if app == "pi":
        for name in ("auth.json", "models-store.json"):
            source = PI_AGENT / name
            if source.is_file():
                shutil.copy2(source, directory / name)
                os.chmod(directory / name, 0o600)
        (directory / "settings.json").write_text(json.dumps({
            "defaultProvider": "openai", "defaultModel": "gpt-6-luna",
            "theme": "dark", "enableInstallTelemetry": False,
        }))
    else:
        source = PISHARP_AGENT / "auth.json"
        if source.is_file():
            shutil.copy2(source, directory / "auth.json")
            os.chmod(directory / "auth.json", 0o600)
        (directory / "settings.json").write_text(json.dumps({
            "defaultProvider": "openai-codex", "defaultModel": "gpt-6-luna",
            "theme": "system", "defaultThinkingLevel": "xhigh",
        }))


def product_command(app: str, sessions: Path) -> list[str]:
    if app == "pi":
        prefix = [
            "node", "--import",
            (PI_SOURCE / "packages/coding-agent/src/experimental/source-resolver.ts").as_uri(),
            str(PI_SOURCE / "packages/coding-agent/src/cli.ts"),
            "--provider", "openai", "--model", "gpt-6-luna",
        ]
    else:
        prefix = [
            "dotnet", str(PISHARP_DLL),
            "--provider", "openai-codex", "--model", "gpt-6-luna",
        ]
    return [
        *prefix,
        "--thinking", "xhigh",
        "--mode", "json",
        "--session-dir", str(sessions),
        "--no-extensions", "--no-skills", "--no-prompt-templates", "--no-context-files",
        *((["--no-mcp"] if app == "pi" else [])),
        "--tools", ",".join(TOOLS),
        "--approve", "--offline",
        TASK_PROMPT,
    ]


def copy_sessions(source: Path, destination: Path) -> list[str]:
    if not source.exists():
        return []
    copied: list[str] = []
    for path in sorted(source.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(source)
        target = destination / "sessions" / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        copied.append(target.relative_to(destination).as_posix())
    return copied


def snapshot_workspace(run_directory: Path) -> dict[str, Any]:
    destination = run_directory / "workspace"
    if destination.exists():
        shutil.rmtree(destination)
    shutil.copytree(WORKSPACE, destination, symlinks=False)
    clean_build_outputs(destination)
    manifest = file_manifest(destination)
    return dict(files=manifest, checksum=aggregate_manifest(manifest), fileCount=len(manifest))


def restore_dependencies() -> dict[str, Any]:
    return run_command(["dotnet", "restore", str(WORKSPACE / "Fixture.slnx"), "--nologo"],
                       cwd=WORKSPACE, timeout=300.0)


def acquire_lock():
    LOCK_PATH.parent.mkdir(parents=True, exist_ok=True)
    handle = open(LOCK_PATH, "w")
    fcntl.flock(handle, fcntl.LOCK_EX)
    return handle


def run_one(app: str, index: int, output: Path, secrets: list[str]) -> dict[str, Any]:
    from common import reset_workspace, verify_baseline

    run_id = f"{app}-{index:02d}"
    run_directory = output / "runs" / run_id
    if run_directory.exists():
        shutil.rmtree(run_directory)
    run_directory.mkdir(parents=True)
    started = time.monotonic()
    record: dict[str, Any] = {
        "runId": run_id, "app": app, "index": index,
        "fixtureId": FIXTURE_ID,
        "model": {"provider": "openai" if app == "pi" else "openai-codex",
                  "id": "gpt-6-luna", "thinking": "xhigh"},
        "timeoutSeconds": RUN_TIMEOUT_SECONDS,
    }
    try:
        baseline = reset_workspace()
        record["startChecksum"] = aggregate_manifest(file_manifest(WORKSPACE))
        if record["startChecksum"] != baseline["fixtureChecksum"]:
            raise RuntimeError("starting workspace checksum does not match the fixture baseline")
        restore = restore_dependencies()
        record["dependencyRestore"] = {key: restore[key] for key in
                                       ("exitCode", "durationSeconds", "timedOut")}
        if restore["exitCode"] != 0:
            record["status"] = "blocked"
            record["blockedReason"] = "NuGet restore failed before the timed run"
            record["stderr"] = redact(restore["stderr"], secrets)
            record["durationSeconds"] = time.monotonic() - started
            return record

        agent_directory = STATE_ROOT / "agents" / run_id
        sessions = STATE_ROOT / "sessions" / run_id
        if sessions.exists():
            shutil.rmtree(sessions)
        sessions.mkdir(parents=True)
        prepare_agent_directory(app, agent_directory)
        command = product_command(app, sessions)
        env = agent_environment(app, agent_directory, sessions)
        result = run_agent_process(command, cwd=WORKSPACE, env=env,
                                   timeout=RUN_TIMEOUT_SECONDS)
        record.update({
            "command": [str(part) for part in command],
            "exitCode": result["exitCode"],
            "durationSeconds": result["durationSeconds"],
            "timedOut": result["timedOut"],
        })
        (run_directory / "events.jsonl").write_text(redact(result["stdout"], secrets))
        (run_directory / "stderr.log").write_text(redact(result["stderr"], secrets))
        record["sessionFiles"] = copy_sessions(sessions, run_directory)
        record["workspace"] = snapshot_workspace(run_directory)
        record["productionSourceChecksum"] = sha256_file(
            run_directory / "workspace" / "src/Fixture.Core/EnvFile.cs")
        record["baselineProductionSourceChecksum"] = sha256_file(
            Path(__file__).with_name("fixture") / FIXTURE_ID / "src/Fixture.Core/EnvFile.cs")
        if result["timedOut"]:
            record["status"] = "timed_out"
        elif is_rate_limited(result["stdout"], result["stderr"]):
            record["status"] = "blocked"
            record["blockedReason"] = "subscription rate limit or capacity response"
        elif result["exitCode"] == 0:
            record["status"] = "completed"
        else:
            record["status"] = "agent_error"
        if result["exitCode"] not in (0, None) and record["status"] == "agent_error":
            record["errorTail"] = redact(result["stderr"][-4000:], secrets)
    except Exception as error:  # keep other runs executing; report the setup failure
        record["status"] = "blocked"
        record["blockedReason"] = f"runner setup failure: {error}"
    finally:
        record["wallDurationSeconds"] = time.monotonic() - started
        write_json(run_directory / "run.json", record)
    return record


def main() -> int:
    global RUN_TIMEOUT_SECONDS
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=int, default=5, help="runs per application")
    parser.add_argument("--apps", nargs="+", choices=["pi", "pisharp"], default=["pi", "pisharp"])
    parser.add_argument("--timeout", type=float, default=RUN_TIMEOUT_SECONDS)
    parser.add_argument("--output", type=Path, default=None)
    parser.add_argument("--skip-build", action="store_true")
    arguments = parser.parse_args()

    RUN_TIMEOUT_SECONDS = arguments.timeout
    stamp = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
    output = arguments.output or (ROOT / "artifacts/parity" / f"objective-agent-parity-{stamp}")
    output.mkdir(parents=True, exist_ok=True)
    secrets = collect_secrets(AUTH_PATHS)
    lock = acquire_lock()
    try:
        verify = subprocess.run(["python3", "-c",
                                 "import sys; sys.path.insert(0, sys.argv[1]); import common; "
                                 "common.verify_baseline(); print(common.load_baseline()['fixtureChecksum'])",
                                 str(Path(__file__).parent)],
                                capture_output=True, text=True, check=False)
        if verify.returncode != 0:
            print(verify.stderr, file=sys.stderr)
            return 2
        build = None if arguments.skip_build else build_pisharp()
        metadata = {
            "fixtureId": FIXTURE_ID,
            "fixtureChecksum": load_baseline()["fixtureChecksum"],
            "prompt": TASK_PROMPT,
            "sourceRevisions": source_revisions(),
            "effectiveConfiguration": {
                "pi": {"provider": "openai", "model": "gpt-6-luna", "thinking": "xhigh",
                       "tools": TOOLS, "subscriptionAuth": True},
                "pisharp": {"provider": "openai-codex", "model": "gpt-6-luna", "thinking": "xhigh",
                            "tools": TOOLS, "subscriptionAuth": True},
            },
            "workspace": str(WORKSPACE),
            "timeoutSeconds": RUN_TIMEOUT_SECONDS,
            "resourceLimits": {
                "timeoutSeconds": RUN_TIMEOUT_SECONDS,
                "cpuSeconds": int(RUN_TIMEOUT_SECONDS) + 5,
                "addressSpaceBytes": 8 * 1024 * 1024 * 1024,
                "openFiles": 4096,
            },
            "build": None if build is None else {
                "exitCode": build["exitCode"], "durationSeconds": build["durationSeconds"],
                "dll": build["dll"], "dllChecksum": build["dllChecksum"],
            },
            "startedAt": stamp,
        }
        write_json(output / "run-metadata.json", metadata)
        if build is not None and (build["exitCode"] != 0 or not PISHARP_DLL.is_file()):
            print("PiSharp build failed; refusing to start real-model runs.", file=sys.stderr)
            return 2
        runs = []
        for app in arguments.apps:
            for index in range(1, arguments.runs + 1):
                record = run_one(app, index, output, secrets)
                runs.append(record)
                print(f"{record['runId']}: {record['status']} "
                      f"{record.get('durationSeconds', 0):.1f}s", flush=True)
        write_json(output / "runs.json", runs)
        print(output)
        return 0
    finally:
        fcntl.flock(lock, fcntl.LOCK_UN)
        lock.close()


if __name__ == "__main__":
    raise SystemExit(main())
