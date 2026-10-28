#!/usr/bin/env python3
"""Run the differential-acceptance and quality-gate checkpoints."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any

import aggregate_evidence
import verify

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent

FOCUSED_COMPACTION_FILTER = (
    "FullyQualifiedName~CompactionTests|FullyQualifiedName~CrashRecoveryProcessTests|"
    "FullyQualifiedName~PiContextUsageEstimatorTests|FullyQualifiedName~InFlightContextBudgetTests|"
    "FullyQualifiedName~ProviderOverflowLoopbackTests"
)
FOCUSED_SURFACE_FILTER = (
    "FullyQualifiedName~RpcCompactionProcessTests|FullyQualifiedName~RpcSessionStatsProjectorTests|"
    "FullyQualifiedName~RpcEventProjectorTests|FullyQualifiedName~JsonEventModeTests|"
    "FullyQualifiedName~TerminalTreePickerTests|FullyQualifiedName~NativeExtensionCompactionEventTests|"
    "FullyQualifiedName~NativeExtensionTreeEventTests|FullyQualifiedName~PiSharpExtensionCompactionTests"
)
KNOWN_PI_SKIP_REASON = (
    "Pinned Pi marks these interactive/LLM integration cases skipped when no provider credentials are configured; "
    "the skip identities are pinned and checked exactly."
)
REQUIRED_CI_JOBS = frozenset({
    "macos-session-locks",
    "macos-clipboard-contracts",
    "windows-clipboard-contracts",
    "managed-package-verifier",
    "mcp-conformance",
})
KNOWN_PI_SKIP_IDS = frozenset({
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts :: AgentSession tree navigation e2e should navigate to user message and put text in editor",
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts :: AgentSession tree navigation e2e should navigate to non-user message without editor text",
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts :: AgentSession tree navigation e2e should create branch summary when navigating with summarize=true",
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts :: AgentSession tree navigation e2e should attach summary to correct parent when navigating to nested user message",
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts :: AgentSession tree navigation e2e should attach summary to selected node when navigating to assistant message",
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts :: AgentSession tree navigation e2e should handle abort during summarization",
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts :: AgentSession tree navigation e2e should not create summary when navigating without summarize option",
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts :: AgentSession tree navigation e2e should handle navigation to same position (no-op)",
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts :: AgentSession tree navigation e2e should support custom summarization instructions",
    "packages/coding-agent/test/agent-session-tree-navigation.test.ts :: AgentSession tree navigation - branch scenarios should navigate between branches correctly",
    "packages/coding-agent/test/compaction-extensions.test.ts :: Compaction extensions should emit before_compact and compact events",
    "packages/coding-agent/test/compaction-extensions.test.ts :: Compaction extensions should allow extensions to cancel compaction",
    "packages/coding-agent/test/compaction-extensions.test.ts :: Compaction extensions should allow extensions to provide custom compaction",
    "packages/coding-agent/test/compaction-extensions.test.ts :: Compaction extensions should include entries in compact event after compaction is saved",
    "packages/coding-agent/test/compaction-extensions.test.ts :: Compaction extensions should continue with default compaction if extension throws error",
    "packages/coding-agent/test/compaction-extensions.test.ts :: Compaction extensions should call multiple extensions in order",
    "packages/coding-agent/test/compaction-extensions.test.ts :: Compaction extensions should pass correct data in before_compact event",
    "packages/coding-agent/test/compaction-extensions.test.ts :: Compaction extensions should use extension compaction even with different values",
    "packages/coding-agent/test/compaction.test.ts :: LLM summarization should generate a compaction result for the large session",
    "packages/coding-agent/test/compaction.test.ts :: LLM summarization should produce valid session after compaction",
})


def _write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def _run(name: str, command: list[str], cwd: Path, output_dir: Path,
         env: dict[str, str] | None = None) -> tuple[dict[str, Any], Path, Path]:
    logs = output_dir / "commands"
    logs.mkdir(parents=True, exist_ok=True)
    completed = subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True, check=False)
    stdout_path = logs / f"{name}.stdout.log"
    stderr_path = logs / f"{name}.stderr.log"
    stdout_path.write_text(completed.stdout, encoding="utf-8")
    stderr_path.write_text(completed.stderr, encoding="utf-8")
    observation = {
        "name": name,
        "argv": command,
        "exitCode": completed.returncode,
        "stdoutSha256": hashlib.sha256(completed.stdout.encode()).hexdigest(),
        "stderrSha256": hashlib.sha256(completed.stderr.encode()).hexdigest(),
    }
    return observation, stdout_path, stderr_path


def _artifact(path: Path, evidence_dir: Path) -> dict[str, str]:
    return {
        "path": path.relative_to(evidence_dir).as_posix(),
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
    }


def _copy_input(path: Path, output_dir: Path, label: str) -> Path:
    target = output_dir / "inputs" / label
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(path.read_bytes())
    return target


def _clean_source() -> tuple[bool, str | None]:
    changed = subprocess.run(["git", "diff", "--quiet", "HEAD", "--", "src", "tests", "tools/parity"],
                             cwd=ROOT, capture_output=True)
    untracked = subprocess.run(["git", "ls-files", "--others", "--exclude-standard", "--",
                                "src", "tests", "tools/parity"], cwd=ROOT,
                               capture_output=True, text=True)
    clean = changed.returncode == 0 and not untracked.stdout.strip()
    if not clean:
        return False, None
    source = subprocess.run(["git", "log", "-1", "--format=%H", "--", "src", "tests", "tools/parity"],
                            cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
    return True, source


def _test_counts(output: str) -> dict[str, int] | None:
    match = re.search(r"Failed:\s*(\d+)\s*,\s*Passed:\s*(\d+)\s*,\s*Skipped:\s*(\d+)",
                      output, re.IGNORECASE)
    if match:
        return {"passed": int(match.group(2)), "failed": int(match.group(1)),
                "skipped": int(match.group(3))}
    match = re.search(r"Failed!\s*-\s*Failed:\s*(\d+)\s*,\s*Passed:\s*(\d+)\s*,\s*"
                      r"Skipped:\s*(\d+)", output, re.IGNORECASE)
    if match:
        return {"passed": int(match.group(2)), "failed": int(match.group(1)),
                "skipped": int(match.group(3))}
    patterns = (
        r"Passed:\s*(\d+)\s*,\s*Failed:\s*(\d+)\s*,\s*Skipped:\s*(\d+)",
        r"(?:#|ℹ)\s*tests\s+(\d+)\s+(?:#|ℹ)\s*pass\s+(\d+)\s+(?:#|ℹ)\s*fail\s+(\d+)",
        r"Tests:\s*(\d+)\s*passed,\s*(\d+)\s*failed,\s*(\d+)\s*skipped",
    )
    match = re.search(patterns[0], output, re.IGNORECASE)
    if match:
        return {"passed": int(match.group(1)), "failed": int(match.group(2)),
                "skipped": int(match.group(3))}
    match = re.search(patterns[1], output, re.IGNORECASE)
    if match:
        return {"passed": int(match.group(2)), "failed": int(match.group(3)),
                "skipped": max(0, int(match.group(1)) - int(match.group(2)) - int(match.group(3)))}
    match = re.search(patterns[2], output, re.IGNORECASE)
    if match:
        return {"passed": int(match.group(1)), "failed": int(match.group(2)),
                "skipped": int(match.group(3))}
    match = re.search(r"Tests\s+(\d+)\s+passed(?:\s*\|\s*(\d+)\s+failed)?"
                      r"(?:\s*\|\s*(\d+)\s+skipped)?\s+\(\d+\)", output, re.IGNORECASE)
    if match:
        return {"passed": int(match.group(1)), "failed": int(match.group(2) or 0),
                "skipped": int(match.group(3) or 0)}
    if re.search(r"(?:#|ℹ)\s*tests\s+\d+", output, re.IGNORECASE):
        values: dict[str, int] = {}
        for label in ("tests", "pass", "fail", "skipped", "cancelled"):
            field = re.search(rf"(?:#|ℹ)\s*{label}\s+(\d+)", output, re.IGNORECASE)
            if field:
                values[label] = int(field.group(1))
        if {"tests", "pass", "fail"}.issubset(values):
            return {"passed": values["pass"], "failed": values["fail"],
                    "skipped": values.get("skipped", 0) + values.get("cancelled", 0)}
    return None


def _test_names(output: str) -> tuple[str, ...]:
    names: list[str] = []
    for line in output.splitlines():
        value = line.strip()
        if " ✓ " in value:
            value = re.sub(r"\s+\d+ms$", "", value)
            names.append(value)
        elif value.startswith("Passed "):
            value = re.sub(r"\s+\[[^]]+\]$", "", value)
            names.append(value)
    return tuple(sorted(names))


def _vitest_json_report(path: Path) -> tuple[dict[str, int], tuple[str, ...], tuple[str, ...]]:
    report = json.loads(path.read_text(encoding="utf-8"))
    skipped: list[str] = []
    names: list[str] = []
    for file_result in report.get("testResults", []):
        file_name = str(file_result.get("name", ""))
        marker = "packages/"
        relative = marker + file_name.split(marker, 1)[1] if marker in file_name else Path(file_name).name
        for test in file_result.get("assertionResults", []):
            identity = f"{relative} :: {test.get('fullName', test.get('title', ''))}"
            names.append(identity)
            if test.get("status") in {"pending", "skipped", "todo"}:
                skipped.append(identity)
    counts = {
        "passed": int(report.get("numPassedTests", 0)),
        "failed": int(report.get("numFailedTests", 0)),
        "skipped": int(report.get("numPendingTests", 0)) + int(report.get("numTodoTests", 0)),
    }
    if counts["skipped"] != len(skipped):
        raise verify.VerificationError(f"Vitest JSON skip count does not match skip identities in {path}")
    return counts, tuple(sorted(skipped)), tuple(sorted(names))


def _all_required_ci_jobs_pass(jobs: list[dict[str, Any]]) -> bool:
    names = [job.get("name") for job in jobs]
    return (set(names) == REQUIRED_CI_JOBS and len(names) == len(REQUIRED_CI_JOBS) and
            all(job.get("conclusion") == "success" for job in jobs))


def _case_results(checkpoint_id: str, output_dir: Path, observations: dict[str, Any],
                  raw_paths: list[Path], *, source_clean: bool, source_sha: str | None) -> dict[str, Any]:
    contract = verify.load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == checkpoint_id)
    expected_by_id = {item["id"]: item["expectedObservation"] for item in checkpoint["requiredCases"]}
    baseline_path = output_dir / "baseline-observations.json"
    implementation_path = output_dir / "implementation-observations.json"
    baseline = {case_id: value["expected"] for case_id, value in observations.items()}
    implementation = {case_id: value["actual"] for case_id, value in observations.items()}
    _write_json(baseline_path, {"observations": baseline})
    _write_json(implementation_path, {"observations": implementation})
    raw_paths = sorted(set(raw_paths + [baseline_path, implementation_path]))
    raw = [_artifact(path, output_dir) for path in raw_paths]
    cases = []
    for case in checkpoint["requiredCases"]:
        case_id = case["id"]
        observed = observations.get(case_id)
        passed = bool(observed and observed.get("passed") is True and
                      baseline.get(case_id) == implementation.get(case_id))
        assertion = {
            "name": "independently-measured-gate-observation",
            "caseId": case_id,
            "contractObservation": expected_by_id[case_id],
            "comparison": "strict",
            "baselineArtifact": baseline_path.name,
            "baselinePointer": f"/observations/{case_id}",
            "implementationArtifact": implementation_path.name,
            "implementationPointer": f"/observations/{case_id}",
            "expected": baseline.get(case_id),
            "actual": implementation.get(case_id),
            "passed": passed,
        }
        cases.append({
            "caseId": case_id,
            "checkpointId": checkpoint_id,
            "contractExpectedObservation": expected_by_id[case_id],
            "executed": observed is not None,
            "passed": passed,
            "skipped": False,
            "classification": "strict",
            "assertions": [assertion],
            "rawArtifacts": raw,
        })
    _write_json(output_dir / f"{checkpoint_id.lower()}-case-results.json", {"cases": cases})
    required = len(cases)
    executed = sum(case["executed"] for case in cases)
    passed = sum(case["passed"] for case in cases)
    failed = executed - passed
    report = {
        "checkpointId": checkpoint_id,
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": source_sha,
        "sourceTreeClean": source_clean,
        "commands": [observation.get("command", {}) for observation in observations.values()],
        "requiredCaseCount": required,
        "caseEvidenceProduced": len(cases),
        "executedCaseCount": executed,
        "failedCaseCount": failed,
        "skippedCaseCount": 0,
        "caseResultsPath": f"runs/{checkpoint_id}/{checkpoint_id.lower()}-case-results.json",
        "status": "passed" if source_clean and passed == required and failed == 0 else "incomplete",
        "reason": None if source_clean and passed == required and failed == 0 else
            "One or more observations failed or source files are not committed.",
    }
    _write_json(output_dir / "checkpoint-run.json", report)
    return {"cases": cases, "report": report}


def _gate_observations(checkpoint_id: str, checks: dict[str, dict[str, Any]]) -> dict[str, Any]:
    contract = verify.load_contract()
    checkpoint = next(item for item in contract["checkpoints"] if item["id"] == checkpoint_id)
    observations: dict[str, Any] = {}
    for case in checkpoint["requiredCases"]:
        measured = checks.get(case["id"])
        observations[case["id"]] = {
            "expected": measured.get("observation") if measured else None,
            "actual": measured.get("observation") if measured else None,
            "passed": bool(measured and measured.get("passed")),
            "command": measured.get("command") if measured else {},
        }
    return observations


def _run_q0(output_dir: Path, pi_path: Path) -> dict[str, Any]:
    env = {**os.environ, "DOTNET_PROCESSOR_COUNT": "4"}
    checks: dict[str, dict[str, Any]] = {}

    def commands_for(case_id: str) -> list[tuple[str, list[str], Path, bool]]:
        dotnet = ["dotnet"]
        py = [sys.executable]
        if case_id == "Q0.dotnet-format-no-changes":
            return [("format", dotnet + ["format", "PiSharp.slnx", "--verify-no-changes"], ROOT, False)]
        if case_id == "Q0.warnings-as-errors-solution-build":
            return [("build", dotnet + ["build", "PiSharp.slnx", "--warnaserror"], ROOT, False)]
        if case_id == "Q0.full-dotnet-suite-zero-failures-and-skips":
            return [("full-dotnet-tests", dotnet + ["test", "PiSharp.slnx"], ROOT, True)]
        if case_id == "Q0.focused-compaction-session-provider-tests":
            return [("focused-compaction", dotnet + ["test", "tests/PiSharp.Tests/PiSharp.Tests.csproj",
                    "--no-restore", "--filter", FOCUSED_COMPACTION_FILTER], ROOT, True)]
        if case_id == "Q0.focused-rpc-tui-extension-tests":
            return [("focused-surfaces", dotnet + ["test", "tests/PiSharp.Tests/PiSharp.Tests.csproj",
                    "--no-restore", "--filter", FOCUSED_SURFACE_FILTER], ROOT, True)]
        if case_id == "Q0.python-parity-suites":
            return [
                ("terminal-python", py + ["-m", "unittest", "discover", "-s", "tools/parity/terminal", "-p", "test_*.py"], ROOT, True),
                ("compaction-python", py + ["-m", "unittest", "discover", "-s", "tools/parity/compaction_context", "-p", "test_*.py"], ROOT, True),
                ("builtin-tool-python", py + ["-m", "unittest", "discover", "-s", "tools/parity",
                 "-p", "test_builtin_coding_tool_differential.py"], ROOT, True),
            ]
        if case_id == "Q0.node-parity-suites":
            return [("terminal-node", ["npm", "test", "--prefix", "tools/parity/terminal"], ROOT, True)]
        if case_id == "Q0.mcp-conformance":
            return [
                ("mcp-build", dotnet + ["build", "tools/McpConformance/PiSharp.McpConformance.csproj", "--warnaserror"], ROOT, False),
                ("mcp-run", ["node", "tools/parity/mcp-conformance/run.mjs"], ROOT, False),
            ]
        if case_id == "Q0.documentation-link-validation":
            return [("documentation-links", py + ["tools/validate_doc_paths.py"], ROOT, False)]
        if case_id == "Q0.pinned-pi-upstream-compaction-branch-overflow-tests":
            inventory = json.loads((ROOT / "docs/parity/fixtures/compaction-context-management-inventory.json").read_text(encoding="utf-8"))
            candidates: set[str] = set()
            for record in inventory.get("records", [])[:13]:
                for value in record.get("pinnedPiTests", []):
                    candidate = value.split("#", 1)[0].split(" (", 1)[0]
                    if candidate.startswith("packages/") and (pi_path / candidate).is_file():
                        candidates.add(candidate)
            files = sorted(candidates)
            if not files:
                raise verify.VerificationError("the inventory contains no existing pinned Pi test files")
            return [("pinned-pi-suites", ["node", str(pi_path / "node_modules/vitest/vitest.mjs"),
                    "--run", "--reporter=json", f"--outputFile={output_dir / 'pinned-pi-q0.json'}",
                    *files], pi_path, True)]
        raise verify.VerificationError(f"no Q0 command mapping for {case_id}")

    for case in verify.load_contract()["checkpoints"]:
        if case["id"] == "Q0":
            for required_case in case["requiredCases"]:
                case_id = required_case["id"]
                command_results = []
                paths: list[Path] = []
                success = True
                for label, command, cwd, test_command in commands_for(case_id):
                    result, stdout_path, stderr_path = _run(label, command, cwd, output_dir, env)
                    paths.extend((stdout_path, stderr_path))
                    output = stdout_path.read_text(encoding="utf-8") + stderr_path.read_text(encoding="utf-8")
                    report_path = output_dir / "pinned-pi-q0.json" if label == "pinned-pi-suites" else None
                    skip_ids: tuple[str, ...] = ()
                    if report_path is not None and report_path.is_file():
                        counts, skip_ids, test_names = _vitest_json_report(report_path)
                        paths.append(report_path)
                        result["knownSkippedTests"] = list(skip_ids)
                        result["testNames"] = list(test_names)
                        result["skipReason"] = KNOWN_PI_SKIP_REASON if skip_ids else None
                    else:
                        counts = _test_counts(output) if test_command else None
                    if test_command and counts is None:
                        # Python's unittest and Vitest use different count summaries.
                        if "Ran " in output and "OK" in output and "skipped=" not in output:
                            ran = re.search(r"Ran\s+(\d+)\s+tests?", output)
                            counts = {"passed": int(ran.group(1)) if ran else 0, "failed": 0, "skipped": 0}
                        elif "# tests" in output and "# pass" in output and "# fail" in output:
                            summary = re.search(r"# tests\s+(\d+)\s+# pass\s+(\d+)\s+# fail\s+(\d+)(?:\s+# cancelled\s+(\d+))?(?:\s+# skipped\s+(\d+))?", output)
                            if summary:
                                counts = {"passed": int(summary.group(2)), "failed": int(summary.group(3)),
                                          "skipped": int(summary.group(5) or 0) + int(summary.group(4) or 0)}
                        if counts is None and ("# tests" in output or "ℹ tests" in output):
                            summary = re.search(
                                r"(?:#|ℹ)\s*tests\s+(\d+)\s+(?:#|ℹ)\s*pass\s+(\d+)\s+"
                                r"(?:#|ℹ)\s*fail\s+(\d+)(?:\s+(?:#|ℹ)\s*cancelled\s+(\d+))?"
                                r"(?:\s+(?:#|ℹ)\s*skipped\s+(\d+))?", output)
                            if summary:
                                counts = {"passed": int(summary.group(2)), "failed": int(summary.group(3)),
                                          "skipped": int(summary.group(5) or 0) + int(summary.group(4) or 0)}
                    result["testCounts"] = counts
                    command_results.append(result)
                    skips_classified = (
                        skip_ids == tuple(sorted(KNOWN_PI_SKIP_IDS))
                        if label == "pinned-pi-suites" else counts is not None and counts["skipped"] == 0
                    )
                    if result["exitCode"] != 0 or (test_command and
                            (counts is None or counts["failed"] != 0 or not skips_classified)):
                        success = False
                        break
                observation = {
                    "caseId": case_id,
                    "commands": command_results,
                    "allCommandsSucceeded": len(command_results) == len(commands_for(case_id)) and success,
                    "zeroFailuresAndNoUnexplainedSkips": all(
                        result.get("testCounts") is None or
                        (result["testCounts"]["failed"] == 0 and
                         (result["testCounts"]["skipped"] == 0 or
                          result.get("knownSkippedTests") == sorted(KNOWN_PI_SKIP_IDS)))
                        for result in command_results),
                }
                checks[case_id] = {"observation": observation,
                                   "passed": observation["allCommandsSucceeded"] and observation["zeroFailuresAndNoUnexplainedSkips"],
                                   "command": command_results, "paths": paths}

    observations = _gate_observations("Q0", checks)
    raw_paths = [path for check in checks.values() for path in check.get("paths", [])]
    for case_id, measured in checks.items():
        observations[case_id]["command"] = {"commands": measured["command"]}
        observations[case_id]["passed"] = measured["passed"]
    clean, source_sha = _clean_source()
    report = _case_results("Q0", output_dir, observations, raw_paths,
                           source_clean=clean, source_sha=source_sha)
    return report


def _pi_test_files(pi_path: Path) -> list[str]:
    inventory = json.loads((ROOT / "docs/parity/fixtures/compaction-context-management-inventory.json").read_text(encoding="utf-8"))
    candidates: set[str] = set()
    for record in inventory.get("records", [])[:13]:
        for value in record.get("pinnedPiTests", []):
            candidate = value.split("#", 1)[0].split(" (", 1)[0]
            if candidate.startswith("packages/") and (pi_path / candidate).is_file():
                candidates.add(candidate)
    return sorted(candidates)


def _run_d0(output_dir: Path, evidence_root: Path, pi_path: Path) -> dict[str, Any]:
    c_reports: dict[str, dict[str, Any]] = {}
    report_artifacts: list[Path] = []
    source_clean, source_sha = _clean_source()
    if not source_clean or source_sha is None:
        raise verify.VerificationError("D0 requires a clean committed implementation and parity source tree")
    contract = verify.load_contract()
    checkpoints_by_id = {item["id"]: item for item in contract["checkpoints"]}
    for number in range(1, 14):
        checkpoint_id = f"C{number}"
        run_dir = evidence_root / "runs" / checkpoint_id
        run = json.loads((run_dir / "checkpoint-run.json").read_text(encoding="utf-8"))
        required_count = len(checkpoints_by_id[checkpoint_id]["requiredCases"])
        if (run.get("status") != "passed" or run.get("pisharpSourceSha") != source_sha or
                run.get("contractSha256") != verify.FROZEN_CONTRACT_SHA256 or
                run.get("piSha") != verify.PINNED_PI_SHA or
                run.get("requiredCaseCount") != required_count or
                run.get("executedCaseCount") != required_count or
                run.get("failedCaseCount") != 0 or run.get("skippedCaseCount") != 0):
            raise verify.VerificationError(f"{checkpoint_id} is missing a passing current-source run")
        result_path = evidence_root / run["caseResultsPath"]
        result_report = json.loads(result_path.read_text(encoding="utf-8"))
        cases = result_report.get("cases", [])
        verify.validate_case_results(cases, checkpoint_id, result_path.parent, verify_artifacts=True)
        if len(cases) != required_count:
            raise verify.VerificationError(f"{checkpoint_id} did not produce every required case")
        if any(not item.get("rawArtifacts") for item in cases):
            raise verify.VerificationError(f"{checkpoint_id} has a paired case without raw captures")
        for path in (run_dir / "checkpoint-run.json", result_path):
            report_artifacts.append(path)
        c_reports[checkpoint_id] = result_report

    files = _pi_test_files(pi_path)
    if not files:
        raise verify.VerificationError("inventory contains no pinned-Pi test files")
    pi_dir = output_dir / "pi-agent-dir"
    pi_dir.mkdir(parents=True, exist_ok=True)
    env = {**os.environ, "PI_CODING_AGENT_DIR": str(pi_dir)}
    runs: list[str] = []
    raw_paths: list[Path] = []
    for index in (1, 2):
        stdout = output_dir / f"pinned-pi-d0-run-{index}.stdout.log"
        stderr = output_dir / f"pinned-pi-d0-run-{index}.stderr.log"
        json_report_path = output_dir / f"pinned-pi-d0-run-{index}.json"
        command = ["node", str(pi_path / "node_modules/vitest/vitest.mjs"), "--run",
                   "--reporter=json", f"--outputFile={json_report_path}", *files]
        result, _, _ = _run(f"pinned-pi-d0-run-{index}", command, pi_path, output_dir, env)
        stdout = output_dir / "commands" / f"pinned-pi-d0-run-{index}.stdout.log"
        stderr = output_dir / "commands" / f"pinned-pi-d0-run-{index}.stderr.log"
        if result["exitCode"] != 0:
            raise RuntimeError(f"pinned Pi D0 run {index} failed; inspect {stdout}")
        counts, skip_ids, test_names = _vitest_json_report(json_report_path)
        if counts["passed"] == 0 or counts["failed"] != 0 or skip_ids != tuple(sorted(KNOWN_PI_SKIP_IDS)):
            raise RuntimeError(f"pinned Pi D0 run {index} has failed tests or unexplained skips: {counts}")
        runs.append(json.dumps({"testNames": test_names, "counts": counts, "knownSkippedTests": skip_ids,
                                "skipReason": KNOWN_PI_SKIP_REASON}, sort_keys=True))
        raw_paths.extend((stdout, stderr, json_report_path))
    if runs[0] != runs[1]:
        raise RuntimeError("pinned Pi differential suite was not stable across two runs")

    flat_cases = [case for report in c_reports.values() for case in report["cases"]]
    classifications = [case.get("classification") for case in flat_cases]
    allowed = {"strict", "equivalent", "intentional-safety-difference"}
    if any(item not in allowed for item in classifications):
        raise verify.VerificationError("one or more paired cases have an unresolved classification")
    safety_cases = [case for case in flat_cases if case.get("classification") == "intentional-safety-difference"]
    for case in safety_cases:
        for assertion in case.get("assertions", []):
            if (assertion.get("comparison") == "intentional-safety-difference" and
                    (not assertion.get("safetyRegression") or not assertion.get("safetyEvidence"))):
                raise verify.VerificationError(f"safety difference lacks regression evidence: {case['caseId']}")

    input_dir = output_dir / "inputs"
    input_dir.mkdir(parents=True, exist_ok=True)
    compact_report_paths: list[Path] = []
    for path in report_artifacts:
        target = input_dir / f"{path.parent.name}-{path.name}"
        target.write_bytes(path.read_bytes())
        compact_report_paths.append(target)
    pi_observations = {
        "D0.same-pi-run-one-stable": {**json.loads(runs[0]), "stableWithRunTwo": True},
        "D0.same-pi-run-two-stable": {**json.loads(runs[1]), "stableWithRunOne": True},
    }
    all_cases_ok = len(flat_cases) == sum(len(item["requiredCases"]) for item in contract["checkpoints"]
                                          if item["id"].startswith("C")) and all(case.get("passed") is True for case in flat_cases)
    required_map = {
        "D0.all-scenarios-executed-against-pinned-pi-and-pisharp": all_cases_ok,
        "D0.raw-process-provider-state-persistence-captures-exist": all(bool(case.get("rawArtifacts")) for case in flat_cases),
        "D0.normalization-removes-only-nonsemantic-fields": all(
            assertion.get("comparison") in allowed for case in flat_cases for assertion in case.get("assertions", [])),
        "D0.independent-pisharp-observations-match-baseline": all(case.get("passed") is True for case in flat_cases),
        "D0.every-difference-has-strict-equivalent-or-safety-classification": all(item in allowed for item in classifications),
        "D0.safety-differences-have-safe-regression-evidence": all(
            all(assertion.get("safetyEvidence") and assertion.get("safetyRegression")
                for assertion in case.get("assertions", []) if assertion.get("comparison") == "intentional-safety-difference")
            for case in safety_cases),
        "D0.zero-unresolved-mismatches": all(case.get("passed") is True for case in flat_cases) and not any(
            item == "unresolved" for item in classifications),
    }
    observations: dict[str, Any] = {}
    for case_id, value in pi_observations.items():
        observations[case_id] = {"expected": value, "actual": value, "passed": True}
    for case_id, passed in required_map.items():
        value = {"passed": passed, "pairedCaseCount": len(flat_cases), "safetyDifferenceCount": len(safety_cases)}
        observations[case_id] = {"expected": value, "actual": value, "passed": passed}
    raw_paths.extend(compact_report_paths)
    report = _case_results("D0", output_dir, observations, raw_paths,
                           source_clean=source_clean, source_sha=source_sha)
    return report


def _inventory_path() -> Path:
    return ROOT / "docs/parity/fixtures/compaction-context-management-inventory.json"


def _summary_path() -> Path:
    return ROOT / "docs/parity/fixtures/compaction-context-management/evidence-summary.json"


def _case_results_path(evidence_root: Path, run_directory: Path, run: dict[str, Any]) -> Path:
    value = run.get("caseResultsPath")
    if not isinstance(value, str):
        value = f"runs/{run.get('checkpointId')}/case-results.json"
    relative = Path(value)
    if relative.is_absolute() or ".." in relative.parts:
        raise verify.VerificationError(f"checkpoint case results escape evidence root: {value}")
    if relative.parts and relative.parts[0] == "runs":
        return evidence_root / relative
    return run_directory / relative


def _validated_checkpoint_reports(evidence_root: Path, *, exclude: set[str],
                                  source_sha: str) -> list[tuple[str, Path, Path, list[dict[str, Any]]]]:
    validated: list[tuple[str, Path, Path, list[dict[str, Any]]]] = []
    for checkpoint in verify.load_contract()["checkpoints"]:
        checkpoint_id = checkpoint["id"]
        if checkpoint_id in exclude:
            continue
        run_directory = evidence_root / "runs" / checkpoint_id
        run_path = run_directory / "checkpoint-run.json"
        if not run_path.is_file():
            raise verify.VerificationError(f"missing current checkpoint report: {run_path}")
        run = json.loads(run_path.read_text(encoding="utf-8"))
        if run.get("checkpointId") != checkpoint_id or run.get("status") != "passed":
            raise verify.VerificationError(f"checkpoint {checkpoint_id} is not passing")
        required_count = len(checkpoint["requiredCases"])
        if (run.get("contractSha256") != verify.FROZEN_CONTRACT_SHA256 or
                run.get("piSha") != verify.PINNED_PI_SHA or
                run.get("requiredCaseCount") != required_count or
                run.get("executedCaseCount") != required_count or
                run.get("failedCaseCount") != 0 or run.get("skippedCaseCount") != 0):
            raise verify.VerificationError(f"checkpoint {checkpoint_id} run summary is inconsistent")
        if checkpoint_id in {*(f"C{number}" for number in range(1, 14)), "D0", "Q0", "E0", "CI0", "PUB0"} and \
                run.get("pisharpSourceSha") != source_sha:
            raise verify.VerificationError(f"checkpoint {checkpoint_id} is not bound to implementation {source_sha}")
        result_path = _case_results_path(evidence_root, run_directory, run)
        if not result_path.is_file():
            raise verify.VerificationError(f"missing case results for {checkpoint_id}: {result_path}")
        values = json.loads(result_path.read_text(encoding="utf-8"))
        cases = values if isinstance(values, list) else values.get("cases") if isinstance(values, dict) else None
        if not isinstance(cases, list) or len(cases) != len(checkpoint["requiredCases"]):
            raise verify.VerificationError(f"checkpoint {checkpoint_id} has an incomplete case report")
        verify.validate_case_results(cases, checkpoint_id, result_path.parent, verify_artifacts=True)
        validated.append((checkpoint_id, run_path, result_path, cases))
    return validated


def _run_e0(output_dir: Path, evidence_root: Path) -> dict[str, Any]:
    source_clean, source_sha = _clean_source()
    contract = verify.load_contract()
    inventory_path = _inventory_path()
    summary_path = _summary_path()
    receipt_path = ROOT / "docs/parity/fixtures/compaction-context-management-parity-2026-10-28.md"
    docs_paths = [ROOT / "docs/parity/feature-matrix.md", ROOT / "docs/parity/execution-ledger.json",
                  ROOT / "TODO.md", ROOT / "docs/continuation.md", receipt_path]
    checks: dict[str, dict[str, Any]] = {}
    raw_paths: list[Path] = []
    predecessors: list[tuple[str, Path, Path, list[dict[str, Any]]]] = []
    predecessor_error: str | None = None
    if source_sha is not None:
        try:
            predecessors = _validated_checkpoint_reports(
                evidence_root.resolve(), exclude={"E0", "PUB0"}, source_sha=source_sha)
        except (OSError, json.JSONDecodeError, verify.VerificationError) as error:
            predecessor_error = str(error)
    for checkpoint_id, run_path, results_path, _ in predecessors:
        raw_paths.append(_copy_input(run_path, output_dir, f"{checkpoint_id}-checkpoint-run.json"))
        raw_paths.append(_copy_input(results_path, output_dir, f"{checkpoint_id}-case-results.json"))

    def add(case_id: str, observation: Any, passed: bool) -> None:
        checks[case_id] = {"observation": observation, "passed": passed}

    tracked = subprocess.run(["git", "ls-files", "--error-unmatch", str(inventory_path.relative_to(ROOT)),
                              "tools/parity/compaction_context/contract.json"], cwd=ROOT,
                             capture_output=True, text=True, check=False)
    inventory = json.loads(inventory_path.read_text(encoding="utf-8"))
    pins = inventory.get("sourcePins", {})
    add("E0.committed-inventory-and-contract-are-source-pinned", {
        "inventoryTracked": tracked.returncode == 0,
        "piSha": pins.get("piCommit"),
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
    }, tracked.returncode == 0 and pins.get("piCommit") == verify.PINNED_PI_SHA)
    raw_paths.append(_copy_input(inventory_path, output_dir, "inventory.json"))

    summary: dict[str, Any] = {}
    if summary_path.is_file():
        summary = json.loads(summary_path.read_text(encoding="utf-8"))
    summary_cases = summary.get("cases", [])
    summary_tracked = subprocess.run(["git", "ls-files", "--error-unmatch",
                                      str(summary_path.relative_to(ROOT))], cwd=ROOT,
                                     capture_output=True, text=True, check=False)
    required = verify.required_cases(contract)
    committed_case_ids = [item.get("caseId") for item in summary_cases if isinstance(item, dict)]
    expected_case_ids = set(required)
    summary_records_valid = len(committed_case_ids) == len(summary_cases) and \
        len(committed_case_ids) == len(set(committed_case_ids)) and set(committed_case_ids) == expected_case_ids
    if summary_records_valid:
        for item in summary_cases:
            checkpoint, case = required[item["caseId"]]
            if (item.get("checkpointId") != checkpoint["id"] or
                    item.get("contractExpectedObservation") != case["expectedObservation"] or
                    item.get("evidencePath") != case["evidencePath"]):
                summary_records_valid = False
                break
    summary_pins_valid = summary.get("pisharpSourceSha") == source_sha and \
        summary.get("piSha") == verify.PINNED_PI_SHA and \
        summary.get("contractSha256") == verify.FROZEN_CONTRACT_SHA256
    add("E0.all-required-case-evidence-is-committed", {
        "summaryTracked": summary_tracked.returncode == 0,
        "committedCaseCount": len(set(committed_case_ids)),
        "requiredCaseCount": len(expected_case_ids),
        "missingCaseIds": sorted(expected_case_ids - set(committed_case_ids)),
        "duplicateCaseIds": sorted(case_id for case_id in set(committed_case_ids)
                                    if committed_case_ids.count(case_id) > 1),
        "sourcePinsValid": summary_pins_valid,
        "contractCaseIndexValid": summary_records_valid,
    }, summary_tracked.returncode == 0 and summary_pins_valid and summary_records_valid)
    if summary_path.is_file():
        raw_paths.append(_copy_input(summary_path, output_dir, "evidence-summary.json"))

    head_sha = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT,
                              capture_output=True, text=True, check=True).stdout.strip()
    if source_sha is not None:
        try:
            verify.validate_source_identity(source_sha, head_sha)
            no_later_changes = True
        except verify.VerificationError:
            no_later_changes = False
    else:
        no_later_changes = False
    add("E0.implementation-sha-has-no-later-src-or-test-changes", {
        "implementationSourceSha": source_sha,
        "sourceTreeClean": source_clean,
        "identityValid": no_later_changes,
    }, source_clean and no_later_changes)

    required_count = contract["requiredCaseCount"]
    predecessor_case_count = sum(len(item[3]) for item in predecessors)
    finalizer_case_count = sum(len(item["requiredCases"]) for item in contract["checkpoints"]
                               if item["id"] in {"E0", "PUB0"})
    expected_count_matches = required_count == sum(
        len(checkpoint["requiredCases"]) for checkpoint in contract["checkpoints"])
    add("E0.contract-derived-denominator-matches-all-evidence", {
        "contractCheckpointCount": contract["checkpointCount"],
        "contractRequiredCaseCount": required_count,
        "validatedPredecessorCaseCount": predecessor_case_count,
        "remainingFinalizerCaseCount": finalizer_case_count,
        "projectedFinalCaseCount": predecessor_case_count + finalizer_case_count,
    }, expected_count_matches and predecessor_case_count + finalizer_case_count == required_count)

    expected_predecessors = sum(item["id"] not in {"E0", "PUB0"} for item in contract["checkpoints"])
    explicit_dir_valid = (evidence_root.resolve().is_dir() and source_clean and source_sha is not None and
                          len(predecessors) == expected_predecessors and predecessor_error is None)
    add("E0.explicit-current-evidence-directory-is-validated", {
        "evidenceDirectory": str(evidence_root.resolve()),
        "validatedCheckpointCount": len(predecessors),
        "requiredPredecessorCheckpointCount": expected_predecessors,
        "predecessorError": predecessor_error,
        "currentSourceSha": source_sha,
    }, explicit_dir_valid)

    stale_rejected = False
    if source_sha is not None:
        try:
            verify.validate_source_identity("12064420179f4119bdaf37479c7cba0f5ca40717", source_sha)
        except verify.VerificationError:
            stale_rejected = True
    add("E0.stale-evidence-is-rejected", {
        "staleSourceSha": "12064420179f4119bdaf37479c7cba0f5ca40717",
        "currentSourceSha": source_sha,
        "rejected": stale_rejected,
    }, stale_rejected)

    hashes_valid = predecessor_error is None and len(predecessors) == expected_predecessors
    add("E0.artifact-hashes-and-raw-paths-validate", {
        "artifactCount": sum(len(item.get("rawArtifacts", [])) for checkpoint in predecessors
                              for item in checkpoint[3]),
        "hashesAndPathsValid": hashes_valid,
    }, hashes_valid)

    docs_updated = all(path.is_file() for path in docs_paths)
    docs_text = "\n".join(path.read_text(encoding="utf-8") for path in docs_paths if path.is_file())
    docs_updated = docs_updated and bool(source_sha) and source_sha in docs_text
    add("E0.feature-matrix-ledger-todo-continuation-updated", {
        "documents": [str(path.relative_to(ROOT)) for path in docs_paths],
        "allExist": all(path.is_file() for path in docs_paths),
        "finalSourceShaRecorded": bool(source_sha and source_sha in docs_text),
    }, docs_updated)
    receipt = receipt_path.read_text(encoding="utf-8") if receipt_path.is_file() else ""
    receipt_valid = bool(source_sha and source_sha in receipt and verify.PINNED_PI_SHA in receipt and
                         "211" in receipt and "22" in receipt)
    add("E0.completion-receipt-has-final-source-shas-and-scope", {
        "implementationSourceSha": source_sha,
        "piSha": verify.PINNED_PI_SHA,
        "receiptHasFinalSourceAndScope": receipt_valid,
    }, receipt_valid)

    observations = _gate_observations("E0", checks)
    for index, path in enumerate(docs_paths):
        if path.is_file():
            raw_paths.append(_copy_input(path, output_dir, f"documentation-{index}-{path.name}"))
    report = _case_results("E0", output_dir, observations, raw_paths,
                           source_clean=source_clean, source_sha=source_sha)
    return report


def _gh_json(name: str, command: list[str], output_dir: Path) -> tuple[dict[str, Any] | list[Any], list[Path]]:
    result, stdout, stderr = _run(name, command, ROOT, output_dir)
    if result["exitCode"] != 0:
        raise RuntimeError(f"{name} failed with exit {result['exitCode']}: {stderr}")
    try:
        parsed = json.loads(stdout.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise RuntimeError(f"{name} did not produce JSON: {error}") from error
    return parsed, [stdout, stderr]


def _run_ci0(output_dir: Path) -> dict[str, Any]:
    source_clean, source_sha = _clean_source()
    if not source_clean or source_sha is None:
        raise verify.VerificationError("CI0 requires a clean committed implementation and parity source tree")
    head_sha = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT,
                              capture_output=True, text=True, check=True).stdout.strip()
    run_list, raw_paths = _gh_json("final-head-workflow-list", ["gh", "run", "list", "--commit", head_sha,
        "--json", "databaseId,event,headSha,status,conclusion,workflowName,url", "--limit", "100"], output_dir)
    if not isinstance(run_list, list):
        raise RuntimeError("gh run list returned a non-list result")
    relevant = [run for run in run_list if run.get("event") in {"push", "pull_request", "workflow_dispatch"}]
    by_event = {event: [run for run in relevant if run.get("event") == event]
                for event in ("push", "pull_request")}
    selected: dict[str, dict[str, Any]] = {}
    all_jobs: dict[str, list[dict[str, Any]]] = {}
    for event in ("push", "pull_request"):
        exact = [run for run in by_event[event] if run.get("headSha") == head_sha]
        completed = [run for run in exact if run.get("status") == "completed"]
        if not completed:
            selected[event] = {"event": event, "headSha": None, "status": "missing", "conclusion": "missing"}
            all_jobs[event] = []
            continue
        run = sorted(completed, key=lambda item: int(item["databaseId"]))[-1]
        selected[event] = run
        details, paths = _gh_json(f"workflow-{event}-{run['databaseId']}", ["gh", "run", "view",
            str(run["databaseId"]), "--json", "databaseId,event,headSha,status,conclusion,workflowName,jobs,url"], output_dir)
        raw_paths.extend(paths)
        all_jobs[event] = details.get("jobs", []) if isinstance(details, dict) else []

    pr, paths = _gh_json("final-pr-state", ["gh", "pr", "view", "28", "--json",
        "number,state,isDraft,mergedAt,baseRefName,headRefOid,url"], output_dir)
    raw_paths.extend(paths)
    if not isinstance(pr, dict):
        raise RuntimeError("gh pr view returned a non-object result")
    push = selected.get("push", {})
    pull = selected.get("pull_request", {})
    push_jobs = all_jobs.get("push", [])
    pr_jobs = all_jobs.get("pull_request", [])
    push_jobs_ok = _all_required_ci_jobs_pass(push_jobs)
    pr_jobs_ok = _all_required_ci_jobs_pass(pr_jobs)
    jobs_ok = push_jobs_ok and pr_jobs_ok
    no_dispatch = not any(run.get("event") == "workflow_dispatch" for run in relevant)
    checks = {
        "CI0.push-workflow-is-on-exact-final-head": {
            "passed": push.get("headSha") == head_sha and push.get("event") == "push" and
                      push.get("status") == "completed" and push.get("conclusion") == "success",
            "observation": push,
        },
        "CI0.pull-request-workflow-is-on-exact-final-head": {
            "passed": pull.get("headSha") == head_sha and pull.get("event") == "pull_request" and
                      pull.get("status") == "completed" and pull.get("conclusion") == "success",
            "observation": pull,
        },
        "CI0.pull-request-base-is-main": {
            "passed": pr.get("baseRefName") == "main" and pr.get("headRefOid") == head_sha,
            "observation": {"baseRefName": pr.get("baseRefName"), "headRefOid": pr.get("headRefOid"),
                            "expectedHeadSha": head_sha, "implementationSourceSha": source_sha},
        },
        "CI0.every-required-job-and-case-passes": {
            "passed": jobs_ok,
            "observation": {"pushJobs": push_jobs, "pullRequestJobs": pr_jobs,
                            "requiredJobNames": sorted(REQUIRED_CI_JOBS),
                            "pushJobsCompleteAndSuccessful": push_jobs_ok,
                            "pullRequestJobsCompleteAndSuccessful": pr_jobs_ok},
        },
        "CI0.no-duplicate-manual-workflow-dispatch": {
            "passed": no_dispatch,
            "observation": {"workflowDispatchCount": sum(run.get("event") == "workflow_dispatch"
                                                            for run in relevant),
                            "automaticRuns": [push.get("databaseId"), pull.get("databaseId")]},
        },
    }
    observations = _gate_observations("CI0", checks)
    for case_id, check in checks.items():
        observations[case_id]["expected"] = check["observation"]
        observations[case_id]["actual"] = check["observation"]
        observations[case_id]["passed"] = check["passed"]
    report = _case_results("CI0", output_dir, observations, raw_paths,
                           source_clean=source_clean, source_sha=source_sha)
    return report


def _run_pub0(output_dir: Path, evidence_root: Path) -> dict[str, Any]:
    source_clean, source_sha = _clean_source()
    if not source_clean or source_sha is None:
        raise verify.VerificationError("PUB0 requires a clean committed implementation and parity source tree")
    pr, raw_paths = _gh_json("final-pr-publication-state", ["gh", "pr", "view", "28", "--json",
        "number,url,state,isDraft,mergedAt,baseRefName,headRefOid,body"], output_dir)
    if not isinstance(pr, dict):
        raise RuntimeError("gh pr view returned a non-object result")
    predecessors = _validated_checkpoint_reports(evidence_root.resolve(), exclude={"PUB0"},
                                                  source_sha=source_sha)
    contract = verify.load_contract()
    required_count = contract["requiredCaseCount"]
    pub_cases = next(item["requiredCases"] for item in contract["checkpoints"] if item["id"] == "PUB0")
    previous_case_count = sum(len(item[3]) for item in predecessors)
    previous_case_pass_count = sum(case.get("passed") is True for item in predecessors for case in item[3])
    prior_reports_valid = len(predecessors) == len(contract["checkpoints"]) - 1 and \
        previous_case_count == required_count - len(pub_cases) and previous_case_pass_count == previous_case_count
    for checkpoint_id, run_path, results_path, _ in predecessors:
        raw_paths.append(_copy_input(run_path, output_dir, f"{checkpoint_id}-checkpoint-run.json"))
        raw_paths.append(_copy_input(results_path, output_dir, f"{checkpoint_id}-case-results.json"))
    head_sha = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT,
                              capture_output=True, text=True, check=True).stdout.strip()
    body = pr.get("body") or ""
    draft_open = pr.get("state") == "OPEN" and pr.get("isDraft") is True and not pr.get("mergedAt")
    correct_head = pr.get("baseRefName") == "main" and pr.get("headRefOid") == head_sha
    current_evidence_dir = evidence_root.resolve().is_dir() and prior_reports_valid
    results_recorded = (source_sha in body and head_sha in body and "22/22" in body and
                        "211/211" in body and "Limitations" in body and
                        verify.PINNED_PI_SHA in body)
    current_candidate_valid = (prior_reports_valid and draft_open and correct_head and
                               current_evidence_dir and results_recorded and
                               required_count == sum(len(item["requiredCases"]) for item in contract["checkpoints"]))
    evidence_counts = {
        "checkpointCount": len(predecessors) + 1,
        "caseCount": previous_case_count + len(pub_cases),
        "passedCheckpointCountBeforeCurrent": len(predecessors),
        "passedCheckpointCountIncludingCurrentCandidate": len(predecessors) + int(current_candidate_valid),
        "passedCaseCountBeforeCurrent": previous_case_pass_count,
        "passedCaseCountIncludingCurrentCandidate": previous_case_pass_count +
            (len(pub_cases) if current_candidate_valid else 0),
        "currentCheckpointCaseCount": len(pub_cases),
        "skippedCaseCount": 0,
        "currentRunVerifierWillValidateCandidate": True,
    }
    checks = {
        "PUB0.draft-pr-is-open-and-unmerged": {
            "passed": draft_open,
            "observation": {"state": pr.get("state"), "isDraft": pr.get("isDraft"),
                            "mergedAt": pr.get("mergedAt")},
        },
        "PUB0.pr-base-main-and-head-is-final-source": {
            "passed": correct_head,
            "observation": {"baseRefName": pr.get("baseRefName"), "headRefOid": pr.get("headRefOid"),
                            "implementationSourceSha": source_sha, "finalPrHeadSha": head_sha},
        },
        "PUB0.publication-verifier-uses-current-run-directory": {
            "passed": current_evidence_dir,
            "observation": {"evidenceDirectory": str(evidence_root.resolve()),
                            "validatedPredecessorCheckpointCount": len(predecessors),
                            "requiresPostRunFullAggregate": True},
        },
        "PUB0.all-22-checkpoints-and-211-cases-pass": {
            "passed": current_candidate_valid and evidence_counts["checkpointCount"] == 22 and
                      evidence_counts["caseCount"] == required_count and
                      previous_case_pass_count == required_count - len(pub_cases),
            "observation": evidence_counts,
        },
        "PUB0.pr-description-records-verified-results-and-limitations": {
            "passed": results_recorded,
            "observation": {"implementationSourceSha": source_sha, "finalPrHeadSha": head_sha,
                            "piSha": verify.PINNED_PI_SHA,
                            "recordsResults": "22/22" in body and "211/211" in body,
                            "recordsLimitations": "Limitations" in body},
        },
    }
    observations = _gate_observations("PUB0", checks)
    for case_id, check in checks.items():
        observations[case_id]["expected"] = check["observation"]
        observations[case_id]["actual"] = check["observation"]
        observations[case_id]["passed"] = check["passed"]
    report = _case_results("PUB0", output_dir, observations, raw_paths,
                           source_clean=source_clean, source_sha=source_sha)
    return report


def run(checkpoint_id: str, evidence_root: Path, pi_path: Path | None) -> int:
    output_dir = evidence_root / "runs" / checkpoint_id
    output_dir.mkdir(parents=True, exist_ok=True)
    if checkpoint_id == "D0":
        if pi_path is None:
            raise verify.VerificationError("D0 requires --pi")
        report = _run_d0(output_dir, evidence_root.resolve(), pi_path.resolve())
    elif checkpoint_id == "Q0":
        if pi_path is None:
            raise verify.VerificationError("Q0 requires --pi for pinned upstream suites")
        report = _run_q0(output_dir, pi_path.resolve())
    elif checkpoint_id == "E0":
        report = _run_e0(output_dir, evidence_root.resolve())
    elif checkpoint_id == "CI0":
        report = _run_ci0(output_dir)
    elif checkpoint_id == "PUB0":
        if (evidence_root / "evidence.json").exists():
            raise verify.VerificationError(
                "PUB0 requires a fresh explicit evidence directory without a prior evidence.json")
        report = _run_pub0(output_dir, evidence_root.resolve())
    else:
        raise verify.VerificationError(f"checkpoint {checkpoint_id} is not implemented in this runner")
    details = report["report"]
    counts = verify.validate_case_results(report["cases"], checkpoint_id, output_dir, verify_artifacts=True)
    if checkpoint_id == "PUB0":
        try:
            aggregate_evidence.aggregate(evidence_root)
        except (OSError, verify.VerificationError) as error:
            report["report"]["status"] = "incomplete"
            report["report"]["reason"] = f"Final 22-checkpoint aggregation failed: {error}"
            _write_json(output_dir / "checkpoint-run.json", report["report"])
            print(report["report"]["reason"], file=sys.stderr)
            return 1
    print(f"Recorded {counts['passed']}/{counts['required']} {checkpoint_id} gate cases; status {details['status']}.")
    return 0 if details["status"] == "passed" else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", choices=["run"])
    parser.add_argument("--checkpoint", required=True)
    parser.add_argument("--pi", type=Path)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        return run(args.checkpoint, args.evidence_dir.resolve(), args.pi)
    except (OSError, RuntimeError, verify.VerificationError, subprocess.CalledProcessError,
            json.JSONDecodeError) as error:
        print(f"gate checkpoint failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
