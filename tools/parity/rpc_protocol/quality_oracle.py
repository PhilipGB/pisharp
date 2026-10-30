#!/usr/bin/env python3
"""Run the frozen RPC milestone quality, regression, and prior-capability gates."""
from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

from verify import (
    BASE_SHA,
    FROZEN_CONTRACT_SHA256,
    PINNED_PI_SHA,
    VerificationError,
    current_implementation_source_commit,
    load_contract,
    validate_case_results,
    validate_clean_source_tree,
    parse_dotnet_test_counts,
    validate_pi_checkout,
    validate_q0_authoritative_counts,
)

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
PROCESS_CHECKPOINT_IDS = tuple(f"R{index}" for index in range(1, 16)) + ("D0", "V1")
QUALITY_CASE_IDS = (
    "Q0.dotnet-format", "Q0.dotnet-build-warnings-as-errors", "Q0.full-dotnet-suite",
    "Q0.all-rpc-process-tests", "Q0.focused-runtime-and-session",
    "Q0.focused-compaction-tools-extensions", "Q0.json-event-regressions", "Q0.linux-process-and-pty",
    "Q0.python-parity-suites", "Q0.node-parity-suites", "Q0.mcp-conformance",
    "Q0.pinned-pi-rpc-suites", "Q0.new-rpc-differential-and-controls", "Q0.docs-link-validation",
    "Q0.prior-milestone-regression-audit",
)
VITEST_SUMMARY = re.compile(r"Tests\s+([\d,]+) passed(?:\s*\|\s*([\d,]+) skipped)?\s*\(([\d,]+)\)")
NODE_SUMMARY = re.compile(r"(?:#|ℹ)\s+(tests|pass|fail|skipped)\s+(\d+)")
ANSI_ESCAPE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")


def quality_case_ids() -> tuple[str, ...]:
    return QUALITY_CASE_IDS


def _run(argv: list[str], *, cwd: Path = ROOT, env: dict[str, str] | None = None,
         timeout: int = 1800) -> dict[str, Any]:
    try:
        result = subprocess.run(argv, cwd=cwd, env=env, capture_output=True, text=True, timeout=timeout)
        return {"argv": argv, "cwd": str(cwd), "exitCode": result.returncode,
                "stdout": result.stdout, "stderr": result.stderr}
    except subprocess.TimeoutExpired as error:
        stdout = error.stdout.decode("utf-8", errors="replace") if isinstance(error.stdout, bytes) else error.stdout or ""
        stderr = error.stderr.decode("utf-8", errors="replace") if isinstance(error.stderr, bytes) else error.stderr or ""
        return {"argv": argv, "cwd": str(cwd), "exitCode": 124,
                "stdout": stdout, "stderr": f"Timed out after {timeout}s\n{stderr}"}


def _summary(step: dict[str, Any]) -> dict[str, Any]:
    output = ANSI_ESCAPE.sub("", step["stdout"] + step["stderr"])
    dotnet = parse_dotnet_test_counts(output)
    if dotnet is not None:
        return dotnet
    vitest = list(VITEST_SUMMARY.finditer(output))
    if vitest:
        match = vitest[-1]
        passed = int(match.group(1).replace(",", ""))
        skipped = int((match.group(2) or "0").replace(",", ""))
        total = int(match.group(3).replace(",", ""))
        return {"failed": total - passed - skipped, "passed": passed, "skipped": skipped, "total": total}
    node: dict[str, int] = {}
    for key, value in NODE_SUMMARY.findall(output):
        node[key] = int(value)
    if "tests" in node and "pass" in node:
        return {"failed": node.get("fail", 0), "passed": node["pass"],
                "skipped": node.get("skipped", 0), "total": node["tests"]}
    python = re.search(r"Ran\s+(\d+)\s+tests?.*\n\nOK(?:\s|$)", output, re.DOTALL)
    if python:
        return {"failed": 0, "passed": int(python.group(1)), "skipped": 0, "total": int(python.group(1))}
    return {}


def _write_artifact(evidence_dir: Path, relative: str, value: Any, role: str) -> dict[str, str]:
    path = evidence_dir / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")
    path.write_bytes(payload)
    return {"path": relative, "role": role, "sha256": hashlib.sha256(payload).hexdigest()}


def _pi_test_files(pi_checkout: Path) -> list[str]:
    tracked = subprocess.run(
        ["git", "ls-files", "packages/coding-agent/test"], cwd=pi_checkout,
        capture_output=True, text=True, check=True,
    ).stdout.splitlines()
    excluded = {
        "packages/coding-agent/test/rpc.test.ts",
        "packages/coding-agent/test/agent-session-branching.test.ts",
        "packages/coding-agent/test/agent-session-compaction.test.ts",
    }
    selected: list[str] = []
    for path in tracked:
        if not path.endswith(".test.ts") or path in excluded:
            continue
        relative = path.removeprefix("packages/coding-agent/")
        relevant = (
            relative.startswith("test/rpc-")
            or relative.startswith("test/agent-session-")
            or relative.startswith("test/session-manager/")
            or relative.startswith("test/compaction")
            or relative.startswith("test/suite/agent-session")
            or relative in {"test/sdk-session-manager.test.ts", "test/settings-manager-compaction.test.ts",
                            "test/branch-summary-extensions.test.ts"}
            or (relative.startswith("test/suite/regressions/")
                and any(token in relative for token in ("rpc", "session", "compaction")))
        )
        if relevant:
            selected.append(relative)
    return sorted(selected)


def _pinned_pi_skips(output: str) -> set[tuple[str, str]]:
    normalized = ANSI_ESCAPE.sub("", output)
    actual_skips: set[tuple[str, str]] = set()
    for line in normalized.splitlines():
        match = re.match(r"\s*↓\s+(test/[^ >]+)\s+>\s+(.+)$", line)
        if match:
            actual_skips.add((f"packages/coding-agent/{match.group(1)}", match.group(2).replace(" > ", " ")))
    return actual_skips


def _pinned_pi_step(pi_checkout: Path, evidence_dir: Path, head: str) -> tuple[dict[str, Any], dict[str, Any]]:
    package_dir = pi_checkout / "packages/coding-agent"
    tests = _pi_test_files(pi_checkout)
    if not tests:
        return {"exitCode": 1, "stdout": "", "stderr": "no tracked pinned-Pi test files selected"}, {}
    environment = dict(os.environ)
    for name in list(environment):
        upper = name.upper()
        if any(token in upper for token in (
            "API_KEY", "OAUTH", "ACCESS_TOKEN", "OPENAI", "ANTHROPIC", "GOOGLE", "GEMINI",
            "OPENROUTER", "AWS_ACCESS", "AZURE_OPENAI", "GROQ", "XAI_API", "MISTRAL",
        )):
            environment.pop(name, None)
    environment.pop("GITHUB_ACTIONS", None)
    environment["PI_CODING_AGENT_DIR"] = str(Path("/tmp") / f"pisharp-pi-rpc-tests-{head[:12]}")
    command = ["node", "../../node_modules/vitest/vitest.mjs", "run", "--config", "vitest.config.ts",
               "--reporter=verbose", *tests]
    step = _run(command, cwd=package_dir, env=environment, timeout=1800)
    output = step["stdout"] + step["stderr"]
    inventory = json.loads((ROOT / "docs/parity/fixtures/rpc-protocol-command-parity/inventory.json").read_text())
    expected_skips = {
        (item["source"], item["testName"]) for item in inventory["credentialGatedUpstreamCases"]
    }
    actual_skips = _pinned_pi_skips(output)
    counts = _summary(step)
    summary = {
        "selectedTrackedTests": tests,
        "excludedTrackedTests": sorted({
            "packages/coding-agent/test/rpc.test.ts",
            "packages/coding-agent/test/agent-session-branching.test.ts",
            "packages/coding-agent/test/agent-session-compaction.test.ts",
        }),
        "testCounts": counts,
        "expectedCredentialSkips": sorted([{"source": source, "testName": name} for source, name in expected_skips],
                                             key=lambda item: (item["source"], item["testName"])),
        "actualSkippedTests": sorted([{"source": source, "testName": name} for source, name in actual_skips],
                                      key=lambda item: (item["source"], item["testName"])),
        "skipSetMatchesInventory": actual_skips == expected_skips,
        "credentialVariablesRemoved": True,
        "offlineMode": "PI_OFFLINE=1 from the pinned Pi Vitest config",
    }
    step["summary"] = summary
    return step, summary


def _quality_plan(evidence_root: Path) -> dict[str, list[list[str]]]:
    test_project = "tests/PiSharp.Tests/PiSharp.Tests.csproj"
    common = ["dotnet", "test", test_project, "--no-build", "--no-restore"]
    return {
        "Q0.dotnet-format": [["dotnet", "format", "PiSharp.slnx", "--verify-no-changes", "--no-restore"]],
        "Q0.dotnet-build-warnings-as-errors": [[
            "dotnet", "build", "PiSharp.slnx", "--no-restore", "--warnaserror",
            "-m:1", "/nodeReuse:false", "-p:NuGetAudit=false",
        ]],
        "Q0.full-dotnet-suite": [["dotnet", "test", "PiSharp.slnx"]],
        "Q0.all-rpc-process-tests": [common + [
            "--filter",
            "FullyQualifiedName~Rpc|FullyQualifiedName~BashShutdownProcessTests|FullyQualifiedName~BuiltinRpcResultParityTests",
        ]],
        "Q0.focused-runtime-and-session": [common + [
            "--filter",
            "FullyQualifiedName~Session|FullyQualifiedName~Model|FullyQualifiedName~ProviderModelRuntimeTests",
        ]],
        "Q0.focused-compaction-tools-extensions": [common + ["--filter", "FullyQualifiedName~Compaction|FullyQualifiedName~Bash|FullyQualifiedName~Tool|FullyQualifiedName~Extension"]],
        "Q0.json-event-regressions": [common + ["--filter", "FullyQualifiedName~RpcEventProjectorTests|FullyQualifiedName~RpcAssistantMessageProjectorTests|FullyQualifiedName~RpcToolCallDeltaProcessTests"]],
        "Q0.linux-process-and-pty": [common + ["--filter", "FullyQualifiedName~Pty|FullyQualifiedName~Process|FullyQualifiedName~Session"]],
        "Q0.python-parity-suites": [
            ["python3", "-m", "unittest", "discover", "-s", "tools/parity/terminal", "-p", "test_*.py"],
            ["python3", "-m", "unittest", "discover", "-s", "tools/parity/rpc_protocol", "-p", "test_*.py"],
            ["python3", "-m", "unittest", "discover", "-s", "tools/parity/compaction_context", "-p", "test_*.py"],
            ["python3", "-m", "unittest", "discover", "-s", "tools/parity/managed_packages", "-p", "test_*.py"],
            ["python3", "-m", "unittest", "discover", "-s", "tools/parity", "-p", "test_builtin_coding_tool_differential.py"],
        ],
        "Q0.node-parity-suites": [["npm", "test", "--prefix", "tools/parity/terminal"]],
        "Q0.mcp-conformance": [
            ["dotnet", "build", "tools/McpConformance/PiSharp.McpConformance.csproj", "--no-restore", "--warnaserror"],
            ["node", "tools/parity/mcp-conformance/run.mjs"],
        ],
        "Q0.docs-link-validation": [[sys.executable, "tools/validate_doc_paths.py"]],
    }


def _step_passed(case_id: str, step: dict[str, Any], summary: dict[str, Any]) -> bool:
    if step.get("exitCode") != 0:
        return False
    if case_id == "Q0.dotnet-build-warnings-as-errors":
        output = step["stdout"] + step["stderr"]
        return bool(re.search(r"\b0 Warning\(s\).*\b0 Error\(s\)", output, re.DOTALL))
    if case_id in {
        "Q0.full-dotnet-suite", "Q0.all-rpc-process-tests", "Q0.focused-runtime-and-session",
        "Q0.focused-compaction-tools-extensions", "Q0.json-event-regressions", "Q0.linux-process-and-pty",
    }:
        return bool(summary) and summary.get("failed") == 0 and summary.get("skipped") == 0 and summary.get("passed", 0) > 0
    if case_id == "Q0.pinned-pi-rpc-suites":
        counts = summary.get("testCounts", {})
        return (step.get("exitCode") == 0 and counts.get("failed") == 0 and counts.get("passed", 0) >= 200
                and counts.get("skipped") == len(summary.get("expectedCredentialSkips", [])) == 20
                and summary.get("skipSetMatchesInventory") is True)
    if case_id.startswith("Q0.python-parity-suites"):
        test_summary = _summary(step)
        return test_summary.get("passed", 0) > 0 and test_summary.get("skipped", 0) == 0
    if case_id == "Q0.node-parity-suites":
        return summary.get("failed") == 0 and summary.get("passed", 0) > 0 and summary.get("skipped", 0) == 0
    if case_id == "Q0.docs-link-validation":
        return "Documentation paths verified:" in (step["stdout"] + step["stderr"])
    return step.get("exitCode") == 0


def _with_gate_result(case_id: str, summary: dict[str, Any], passed: bool) -> dict[str, Any]:
    """Keep full-suite numbers separate from the Boolean acceptance result."""
    if case_id == "Q0.full-dotnet-suite":
        summary["testCounts"] = {
            field: summary[field] for field in ("passed", "failed", "skipped", "total")
            if field in summary
        }
    summary["passed"] = passed
    return summary


def run_quality_checkpoint(pi_checkout: Path, evidence_dir: Path) -> dict[str, int]:
    """Execute every frozen Q0 gate and write its captured command reports."""
    validate_clean_source_tree()
    validate_pi_checkout(pi_checkout)
    if (evidence_dir / "evidence.json").exists():
        raise VerificationError(f"evidence already exists; choose a new run directory: {evidence_dir}")
    evidence_dir.mkdir(parents=True, exist_ok=True)
    contract = load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == "Q0")
    expected_ids = [case["id"] for case in checkpoint["caseDefinitions"]]
    if expected_ids != list(QUALITY_CASE_IDS):
        raise VerificationError("Q0 quality runner case order differs from the frozen contract")
    case_defs = {case["id"]: case for case in checkpoint["caseDefinitions"]}
    source_head = current_implementation_source_commit()
    final_head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True,
                                text=True, check=True).stdout.strip()
    plan = _quality_plan(evidence_dir.parent)
    run_summaries: dict[str, dict[str, Any]] = {}
    case_results: list[dict[str, Any]] = []

    for case_id in expected_ids:
        case = case_defs[case_id]
        steps: list[dict[str, Any]] = []
        if case_id == "Q0.pinned-pi-rpc-suites":
            pi_step, pi_summary = _pinned_pi_step(pi_checkout.resolve(), evidence_dir, source_head)
            steps.append(pi_step)
            summary = pi_summary
        elif case_id == "Q0.new-rpc-differential-and-controls":
            summary = {"checkpoints": {}, "passed": True}
            root = evidence_dir.parent
            for checkpoint_id in PROCESS_CHECKPOINT_IDS:
                result = _run([
                    sys.executable, str(HERE / "verify.py"), "check", "--checkpoint", checkpoint_id,
                    "--evidence-dir", str(root / checkpoint_id),
                ])
                steps.append(result)
                summary["checkpoints"][checkpoint_id] = {
                    "exitCode": result["exitCode"], "output": result["stdout"] + result["stderr"],
                }
                summary["passed"] = summary["passed"] and result["exitCode"] == 0
        elif case_id == "Q0.prior-milestone-regression-audit":
            workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
            managed_branch_scope = (
                "milestone/managed-package-lifecycle-parity" in workflow
                and "Run managed-package parity corpus" in workflow
                and "Record managed-package CI integration" in workflow
                and "Independently check managed-package evidence" in workflow
            )
            dependencies = {
                key: value.get("passed", False)
                for key, value in run_summaries.items()
                if key in {
                    "Q0.full-dotnet-suite", "Q0.python-parity-suites", "Q0.node-parity-suites",
                    "Q0.mcp-conformance", "Q0.pinned-pi-rpc-suites",
                }
            }
            summary = {"managedPackageCorpusBranchScoped": managed_branch_scope,
                       "precedingRegressionGates": dependencies,
                       "allPrecedingGatesPassed": bool(dependencies) and all(dependencies.values())}
            workflow_artifact = _write_artifact(evidence_dir, "raw/managed-package-workflow.json",
                                                {"path": ".github/workflows/ci.yml", "content": workflow},
                                                "managed-package-workflow-source")
            steps.append({"argv": ["read", ".github/workflows/ci.yml"], "cwd": str(ROOT), "exitCode": 0,
                          "stdout": workflow, "stderr": "", "artifact": workflow_artifact})
        else:
            commands = plan.get(case_id)
            if not commands:
                raise VerificationError(f"no quality command plan registered for {case_id}")
            summaries = []
            for command in commands:
                if case_id == "Q0.full-dotnet-suite":
                    # NuGet's audit cache is read-only in some managed workspaces. Keep the
                    # frozen command line intact and record the environment override used.
                    result = _run(command, env={**os.environ, "NuGetAudit": "false"})
                    result["environmentOverrides"] = {"NuGetAudit": "false"}
                else:
                    result = _run(command)
                summaries.append(_summary(result))
                steps.append(result)
            summary = {"commandsPassed": bool(steps) and all(step["exitCode"] == 0 for step in steps),
                       "commandSummaries": summaries}
            if len(summaries) == 1:
                summary.update(summaries[0])

        passed = (
            summary.get("allPrecedingGatesPassed") is True and summary.get("managedPackageCorpusBranchScoped") is True
            if case_id == "Q0.prior-milestone-regression-audit"
            else summary.get("passed") is True
            if case_id == "Q0.new-rpc-differential-and-controls"
            else all(_step_passed(case_id, step, summary if case_id != "Q0.pinned-pi-rpc-suites" else summary)
                     for step in steps)
        )
        summary = _with_gate_result(case_id, summary, passed)
        run_summaries[case_id] = summary

        step_artifacts = []
        for index, step in enumerate(steps, start=1):
            artifact = step.get("artifact")
            if artifact is None:
                artifact = _write_artifact(evidence_dir, f"raw/{case_id}/command-{index}.json", step,
                                           f"{case_id}.command-{index}")
            step_artifacts.append(artifact)
        expected_artifact = _write_artifact(evidence_dir, f"cases/{case_id}/acceptance.json", {
            "accepted": True, "expectedObservation": case["expectedObservation"],
        }, f"{case_id}.acceptance")
        observation = {
            "expectedObservation": case["expectedObservation"], "accepted": passed,
            "summary": summary, "commandArtifactPaths": [artifact["path"] for artifact in step_artifacts],
        }
        observation_artifact = _write_artifact(evidence_dir, f"cases/{case_id}/observation.json", observation,
                                               f"{case_id}.observation")
        raw_artifacts = [*step_artifacts, expected_artifact, observation_artifact]
        case_results.append({
            "caseId": case_id, "checkpointId": "Q0",
            "contractExpectedObservation": case["expectedObservation"],
            "executed": True, "passed": passed, "skipped": False, "rawArtifacts": raw_artifacts,
            "assertions": [{
                "contractObservation": case["expectedObservation"],
                "comparison": case["comparisonRules"]["mode"],
                "piArtifact": expected_artifact["path"], "piPointer": "/accepted", "piValue": True,
                "pisharpArtifact": observation_artifact["path"], "pisharpPointer": "/accepted",
                "pisharpValue": passed, "passed": passed,
            }],
        })

    evidence = {
        "schemaVersion": 1, "contractSha256": FROZEN_CONTRACT_SHA256, "piCommit": PINNED_PI_SHA,
        "implementationSourceCommit": source_head, "finalHeadCommit": final_head,
        "pisharpBaseCommit": BASE_SHA, "checkpointId": "Q0", "caseResults": case_results,
        "qualitySourceCommit": source_head, "pinnedPiTestRoot": str(pi_checkout.resolve()),
    }
    (evidence_dir / "evidence.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    counts = validate_case_results(case_results, "Q0", evidence_dir)
    validate_q0_authoritative_counts(evidence, evidence_dir, expected_source_commit=source_head)
    return counts

