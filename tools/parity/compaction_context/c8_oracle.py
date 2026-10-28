#!/usr/bin/env python3
"""Run paired pinned-Pi and PiSharp session persistence and branch observations."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any

import c5_oracle
import verify

CASE_IDS = (
    "C8.compaction-entry-persists-summary-boundary-usage",
    "C8.canonical-ancestor-messages-remain-recoverable",
    "C8.jsonl-export-import-round-trips-compaction",
    "C8.reload-restores-compacted-context-not-projection",
    "C8.branch-context-is-reconstructed-from-selected-path",
    "C8.switching-before-and-after-compaction-restores-correct-path",
    "C8.fork-and-clone-preserve-compacted-session",
    "C8.restart-recovery-keeps-provider-continuation",
    "C8.openai-responses-chat-anthropic-after-reload",
    "C8.metadata-label-and-extension-fields-survive",
)

PROVIDER_TEST = (
    "FullyQualifiedName~ProviderOverflowLoopbackTests.RealSdkContinuesFromReloadedCompactedSession"
)
PROVIDER_APIS = {
    "openai-responses": "/v1/responses",
    "openai-completions": "/v1/chat/completions",
    "anthropic-messages": "/v1/messages",
}
def _text(value: Any) -> str:
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        return "".join(_text(item) for item in value)
    if isinstance(value, dict):
        if isinstance(value.get("text"), str):
            return value["text"]
        if isinstance(value.get("content"), (str, list)):
            return _text(value["content"])
    return ""


def _normalize_text(value: str, working_root: str | None) -> str:
    if working_root:
        value = value.replace(working_root, "<working-directory>")
    value = value.replace("<temporary-working-directory>", "<working-directory>")
    value = re.sub(r"(?m)^- Main documentation: .*README\.md$", "- Main documentation: <project>/README.md", value)
    value = re.sub(r"(?m)^- Additional docs: .*?$", "- Additional docs: <project>/docs", value)
    value = re.sub(r"(?m)^- Examples: .*? \(extensions, custom tools, SDK\)$",
                   "- Examples: <project>/examples (extensions, custom tools, SDK)", value)
    return value


def _semantic_messages(api: str, request: dict[str, Any], working_root: str | None = None) -> list[dict[str, str]]:
    messages = request.get("input") if api == "openai-responses" else request.get("messages")
    if not isinstance(messages, list):
        raise RuntimeError(f"{api} request omitted its message list")
    normalized: list[dict[str, str]] = []
    for message in messages:
        if not isinstance(message, dict):
            raise RuntimeError(f"{api} request contains a non-object message")
        role = str(message.get("role", ""))
        content = message.get("content", message.get("text", ""))
        text = _normalize_text(_text(content), working_root)
        if text or role:
            normalized.append({"role": role, "text": text})
    system = request.get("system", request.get("instructions"))
    system_text = _normalize_text(_text(system), working_root) if isinstance(system, (str, list)) else ""
    if system_text and not any(message["role"] == "system" for message in normalized):
        normalized.insert(0, {"role": "system", "text": system_text})
    return [message for message in normalized if message["text"]]


def _run_provider_capture(evidence_dir: Path) -> tuple[dict[str, Any], list[Path]]:
    capture_dir = evidence_dir / "provider-requests"
    capture_dir.mkdir(parents=True, exist_ok=True)
    command = [
        "dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--no-restore",
        "--filter", PROVIDER_TEST, "--logger", "console;verbosity=minimal",
    ]
    process = subprocess.run(command, cwd=c5_oracle.ROOT, capture_output=True, text=True,
                             env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4",
                                  "PISHARP_C8_PROVIDER_CAPTURE_DIR": str(capture_dir)}, check=False)
    stdout = evidence_dir / "provider-test.stdout.log"
    stderr = evidence_dir / "provider-test.stderr.log"
    stdout.write_text(process.stdout, encoding="utf-8")
    stderr.write_text(process.stderr, encoding="utf-8")
    if process.returncode:
        raise RuntimeError(f"PiSharp provider-after-reload loopback tests failed: {process.stderr}\n{process.stdout}")
    captures = {}
    files = [stdout, stderr]
    for api in PROVIDER_APIS:
        path = capture_dir / f"{api}.json"
        if not path.is_file():
            raise RuntimeError(f"PiSharp provider loopback did not record {api} request")
        captures[api] = json.loads(path.read_text(encoding="utf-8"))
        files.append(path)
    return captures, files


def _validate_case(case_id: str, pi: dict[str, Any], pisharp: dict[str, Any],
                   pi_provider: dict[str, Any], pisharp_provider: dict[str, Any]) -> tuple[Any, Any]:
    if case_id == CASE_IDS[0]:
        keys = ("summary", "boundaryPreserved", "boundaryMessage", "tokensBefore", "usageInput",
                "usageOutput", "usageCachedInput", "usageCachedWrite", "usageTotal", "detailsOwner", "detailsRevision")
        expected = {key: pi[key] for key in keys}
        actual = {key: pisharp[key] for key in keys}
    elif case_id == CASE_IDS[1]:
        expected = pi
        actual = pisharp
    elif case_id == CASE_IDS[2]:
        expected = pi
        actual = pisharp
    elif case_id == CASE_IDS[3]:
        keys = ("compactedContext", "reloadedContext", "contextsMatch", "oldRawHistoryAbsentFromContext")
        expected = {key: pi[key] for key in keys}
        actual = {key: pisharp[key] for key in keys}
    elif case_id == CASE_IDS[4]:
        expected = pi
        actual = pisharp
    elif case_id == CASE_IDS[5]:
        expected = pi
        actual = pisharp
    elif case_id == CASE_IDS[6]:
        expected = {"bothPreserveSummary": pi["bothPreserveSummary"]}
        actual = {"bothPreserveSummary": pisharp["bothPreserveSummary"]}
    elif case_id == CASE_IDS[7]:
        expected = {
            "requestCount": pi["requestCount"],
            "requestIncludesSummary": pi["requestIncludesSummary"],
            "requestIncludesRetainedHistory": pi["requestIncludesRetainedHistory"],
            "requestIncludesNewPrompt": pi["requestIncludesNewPrompt"],
        }
        requests = pisharp.get("requests", [])
        flattened = json.dumps(requests, ensure_ascii=False)
        actual = {
            "requestCount": pisharp["requestCount"],
            "requestIncludesSummary": "older history summary" in flattened,
            "requestIncludesRetainedHistory": "retained answer" in flattened,
            "requestIncludesNewPrompt": "continue after restart" in flattened,
        }
    elif case_id == CASE_IDS[8]:
        expected_apis = {}
        actual_apis = {}
        pi_requests = {item["api"]: item for item in pi["requests"]}
        if set(pi_requests) != set(PROVIDER_APIS):
            raise RuntimeError("pinned Pi did not execute all three required provider adapters")
        for api, expected_path in PROVIDER_APIS.items():
            pi_request = pi_requests[api]
            ps_capture = pisharp_provider[api]
            if pi_request["path"] != expected_path or ps_capture.get("path") != expected_path:
                raise RuntimeError(f"provider endpoint mismatch for {api}")
            expected_messages = _semantic_messages(api, pi_request["request"])
            actual_messages = _semantic_messages(api, ps_capture["request"], ps_capture.get("workingDirectory"))
            if not ps_capture.get("canonicalAncestorsPreserved") or not ps_capture.get("continuationCompleted"):
                raise RuntimeError(f"PiSharp {api} did not preserve ancestors or complete after reload")
            expected_apis[api] = expected_messages
            actual_apis[api] = actual_messages
        expected = expected_apis
        actual = actual_apis
    elif case_id == CASE_IDS[9]:
        expected = {
            "label": pi["label"],
            "extensionOwner": pi["detailsOwner"],
            "extensionRevision": pi["detailsRevision"],
            "customExtension": pi["customExtension"],
        }
        actual = {
            key: pisharp[key] for key in expected
        }
    else:
        raise ValueError(f"no C8 validation for {case_id}")
    if expected != actual:
        raise RuntimeError(f"C8 paired observations differ for {case_id}:\n" + json.dumps({
            "expected": expected, "actual": actual,
        }, indent=2, ensure_ascii=False))
    return expected, actual


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    pi_run, pi_files = c5_oracle.run_pi(pi_path, evidence_dir, "c8")
    pisharp_run, pisharp_files = c5_oracle.run_pisharp(evidence_dir, "c8")
    pisharp_provider, provider_files = _run_provider_capture(evidence_dir)
    pi_values = pi_run["cases"]
    pisharp_values = pisharp_run["cases"]
    if set(pi_values) != set(CASE_IDS) or set(pisharp_values) != set(CASE_IDS):
        raise RuntimeError("C8 oracle did not produce exactly the frozen case IDs")

    contract = verify.load_contract()
    expected_contract = {case["id"]: case["expectedObservation"] for checkpoint in contract["checkpoints"]
                         if checkpoint["id"] == "C8" for case in checkpoint["requiredCases"]}
    pi_provider_path = evidence_dir / "pi-c8-provider-requests.json"
    pisharp_provider_path = evidence_dir / "pisharp-c8-provider-requests.json"
    pi_provider_values = pi_values[CASE_IDS[8]]
    pi_provider_path.write_text(json.dumps({"requests": pi_provider_values["requests"]}, indent=2,
                                           ensure_ascii=False) + "\n", encoding="utf-8")
    pisharp_provider_path.write_text(json.dumps(pisharp_provider, indent=2, ensure_ascii=False) + "\n",
                                     encoding="utf-8")

    pi_path_out = evidence_dir / "pi-c8-observations.json"
    pisharp_path_out = evidence_dir / "pisharp-c8-observations.json"
    pi_normalized: dict[str, Any] = {}
    pisharp_normalized: dict[str, Any] = {}
    for case_id in CASE_IDS:
        expected, actual = _validate_case(case_id, pi_values[case_id], pisharp_values[case_id],
                                          pi_provider_values, pisharp_provider)
        pi_normalized[case_id] = expected
        pisharp_normalized[case_id] = actual

    pi_path_out.write_text(json.dumps({"observations": pi_normalized}, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8")
    pisharp_path_out.write_text(json.dumps({"observations": pisharp_normalized}, indent=2, ensure_ascii=False) + "\n",
                                encoding="utf-8")
    raw = [*pi_files, *pisharp_files, *provider_files, pi_provider_path, pisharp_provider_path,
           pi_path_out, pisharp_path_out]
    cases = []
    for case_id in CASE_IDS:
        pointer = f"/observations/{case_id}"
        observation = pi_normalized[case_id]
        cases.append({
            "caseId": case_id,
            "checkpointId": "C8",
            "contractExpectedObservation": expected_contract[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "equivalent",
            "assertions": [{
                "name": "paired-session-persistence-and-reload-observation",
                "caseId": case_id,
                "contractObservation": expected_contract[case_id],
                "comparison": "equivalent",
                "baselineArtifact": pi_path_out.name,
                "baselinePointer": pointer,
                "implementationArtifact": pisharp_path_out.name,
                "implementationPointer": pointer,
                "expected": observation,
                "actual": pisharp_normalized[case_id],
                "normalizedExpected": observation,
                "normalizedActual": pisharp_normalized[case_id],
                "passed": True,
            }],
            "rawArtifacts": [c5_oracle.artifact(path, evidence_dir) for path in raw],
        })
    counts = verify.validate_case_results(cases, "C8", evidence_dir, verify_artifacts=True)
    report = {
        "checkpointId": "C8",
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piSha": verify.PINNED_PI_SHA,
        "pisharpSourceSha": c5_oracle.implementation_source_sha() if c5_oracle.source_tree_is_clean() else None,
        "cases": cases,
        "checkpointResults": [{
            "checkpointId": "C8",
            "requiredCaseCount": counts["required"],
            "executedCaseCount": counts["executed"],
            "failedCaseCount": counts["failed"],
            "skippedCaseCount": counts["skipped"],
            "status": "passed" if counts["passed"] == counts["required"] else "incomplete",
        }],
        "verifierCounts": counts,
        "providerApis": sorted(PROVIDER_APIS),
    }
    (evidence_dir / "c8-case-results.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n",
                                                          encoding="utf-8")
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        report = run(args.pi.resolve(), args.evidence_dir.resolve())
        counts = report["verifierCounts"]
        print(f"C8 paired cases: {counts['passed']}/{counts['required']}; 0 failed, 0 skipped")
        return 0
    except (verify.VerificationError, OSError, RuntimeError, KeyError, TypeError, ValueError,
            subprocess.CalledProcessError) as error:
        print(f"C8 oracle failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
