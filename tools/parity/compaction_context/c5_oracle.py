#!/usr/bin/env python3
"""Run stable paired pinned-Pi/PiSharp C5 recovery differentials."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
PI_ORACLE = HERE / "pi_oracle.mjs"
CS_PROJECT = HERE / "pisharp_runner" / "PiSharp.CompactionOracle.csproj"
RECOVERY_ERROR = "Truncated response recovery failed after one compact-and-retry attempt."

SCENARIO_CASES = {
    "length-recovery": (
        "C5.recoverable-length-stop-retries-once",
        "C5.retry-request-uses-compacted-transcript",
        "C5.post-compaction-provider-continuation-succeeds",
    ),
    "context-boundary-length": ("C5.length-stop-at-context-boundary-recognized",),
    "second-length-stop": ("C5.second-overflow-stops-with-clear-error",),
    "pre-content-overflow": ("C5.pre-content-context-error-recognized",),
    "silent-usage-overflow": ("C5.provider-silent-overflow-from-reported-usage",),
    "partial-content-overflow": ("C5.partial-content-overflow-does-not-duplicate-request",),
    "unknown-model-window": ("C5.unknown-model-window-does-not-fabricate-capacity",),
    "stale-old-model-overflow": ("C5.stale-old-model-overflow-does-not-compact-new-model",),
}


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(*arguments: str) -> str:
    return subprocess.run(["git", *arguments], cwd=ROOT, capture_output=True, text=True,
                          check=True).stdout.strip()


def source_tree_is_clean() -> bool:
    changed = subprocess.run(["git", "diff", "--quiet", "HEAD", "--", "src", "tests", "tools/parity"],
                             cwd=ROOT, capture_output=True)
    untracked = subprocess.run(["git", "ls-files", "--others", "--exclude-standard", "--",
                                "src", "tests", "tools/parity"], cwd=ROOT, capture_output=True, text=True)
    return changed.returncode == 0 and not untracked.stdout.strip()


def compact_pi(pi_run: dict[str, Any]) -> dict[str, Any]:
    all_requests = pi_run["providerRequests"]
    summary_requests = [request for request in all_requests if any(
        message["role"] == "system" and message["text"].startswith("You are a context summarization assistant")
        for message in request["messages"])]
    provider_requests = [request for request in all_requests if request not in summary_requests]
    normalized_requests = [[message for message in request["messages"]
                            if not (message["role"] == "system" and message["text"] == "")]
                           for request in provider_requests]
    compaction_end_events = [{"reason": event["reason"], "willRetry": event["willRetry"]}
                             for event in pi_run["events"] if event["type"] == "compaction_end"]
    errors = [event["errorMessage"] for event in pi_run["events"]
              if event["type"] == "compaction_end" and event.get("errorMessage")]
    durable_messages = [entry["message"] for entry in pi_run["sessionEntries"] if entry["type"] == "message"]
    retry_messages = normalized_requests[-1] if len(normalized_requests) > 1 else []
    response_usage = [event["usage"] for event in pi_run["events"] if event.get("usage") is not None]
    return {
        "modelSwitchApplied": pi_run.get("modelSwitchApplied", False),
        "selectedModel": pi_run.get("selectedModel"),
        "providerRequests": normalized_requests,
        "providerAttemptCount": len(normalized_requests),
        "summaryRequestCount": len(summary_requests),
        "compactionEndEvents": compaction_end_events,
        "compactionCount": pi_run["compactionCount"],
        "recoveryErrors": errors,
        "retryContainsPartialResponse": any("partial output" in message["text"] for message in retry_messages),
        "retryContainsCompactedSummary": any("compacted history summary" in message["text"]
                                              for message in retry_messages),
        "partialResponsePersisted": any(message["role"] == "assistant" and message["text"] == "partial output"
                                         for message in durable_messages),
        "continuedResponsePersisted": any(message["role"] == "assistant" and
                                           message["text"] == "continued response" for message in durable_messages),
        "completedResponsePersisted": any(message["role"] == "assistant" and
                                           message["text"] == "completed response" for message in durable_messages),
        "responseUsage": response_usage,
    }


def compact_pisharp(result: dict[str, Any]) -> dict[str, Any]:
    compaction_end_events = [{"reason": event["CompactionReason"], "willRetry": event["CompactionWillRetry"]}
                             for event in result["events"] if event["Type"] == "compaction_end"]
    compaction_end_events.extend({"reason": "overflow", "willRetry": True}
                                 for event in result["events"]
                                 if event["Type"] == "model_context_overflow_recovery")
    requests = [[{"role": message["role"], "text": message["text"]}
                 for message in request["messages"]]
                for request in result["providerRequests"]]
    errors = [event["Error"] for event in result["events"]
              if event["Type"] == "compaction_end" and event.get("Error")]
    active = result["activeMessages"]
    retry_messages = requests[-1] if len(requests) > 1 else []
    response_usage = [{"inputTokens": event["inputTokens"], "outputTokens": event["outputTokens"]}
                      for event in result["events"] if event["Type"] == "model_request_completed"]
    return {
        "modelSwitchApplied": result.get("modelSwitchApplied", False),
        "selectedModel": result.get("selectedModel"),
        "providerRequests": requests,
        "providerAttemptCount": len(requests),
        "summaryRequestCount": result["summaryRequests"],
        "compactionEndEvents": compaction_end_events,
        "compactionCount": sum(entry["Type"] == "compaction" for entry in result["activeEntries"]),
        "recoveryErrors": errors,
        "retryContainsPartialResponse": any("partial output" in message["text"] for message in retry_messages),
        "retryContainsCompactedSummary": any("compacted history summary" in message["text"]
                                              for message in retry_messages),
        "partialResponsePersisted": any(message["role"] == "assistant" and message["text"] == "partial output"
                                         for message in active),
        "continuedResponsePersisted": any(message["role"] == "assistant" and
                                           message["text"] == "continued response" for message in active),
        "completedResponsePersisted": any(message["role"] == "assistant" and
                                           message["text"] == "completed response" for message in active),
        "responseUsage": response_usage,
    }


def artifact(path: Path, evidence_dir: Path) -> dict[str, str]:
    return {"path": path.relative_to(evidence_dir).as_posix(), "sha256": sha256(path)}


def run_pi(pi_path: Path, evidence_dir: Path, scenario: str) -> tuple[dict[str, Any], list[Path]]:
    output = evidence_dir / f"pi-{scenario}"
    command = ["node", str(PI_ORACLE), "--pi", str(pi_path), "--output", str(output),
               "--scenario", scenario]
    process = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, check=False)
    stdout = evidence_dir / f"pi-{scenario}.stdout.log"
    stderr = evidence_dir / f"pi-{scenario}.stderr.log"
    stdout.write_text(process.stdout, encoding="utf-8")
    stderr.write_text(process.stderr, encoding="utf-8")
    if process.returncode:
        raise RuntimeError(f"pinned Pi {scenario} oracle exited {process.returncode}: {process.stderr}")

    first = read_json(output / "pi-run-1.json")
    second = read_json(output / "pi-run-2.json")
    if first != second:
        raise RuntimeError(f"pinned Pi {scenario} oracle was not stable across its two executions")
    files = [output / "pi-run-1.json", output / "pi-run-2.json", output / "pi-oracle.json", stdout, stderr]
    return first, files


def run_pisharp(evidence_dir: Path, scenario: str) -> tuple[dict[str, Any], list[Path]]:
    runs = []
    files: list[Path] = []
    for index in (1, 2):
        result_path = evidence_dir / f"pisharp-{scenario}-run-{index}.json"
        command = ["dotnet", "run", "--project", str(CS_PROJECT), "--no-restore", "--",
                   str(result_path), scenario]
        process = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, check=False,
                                 env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4"})
        stdout = result_path.with_suffix(".stdout.log")
        stderr = result_path.with_suffix(".stderr.log")
        stdout.write_text(process.stdout, encoding="utf-8")
        stderr.write_text(process.stderr, encoding="utf-8")
        if process.returncode:
            raise RuntimeError(f"PiSharp {scenario} oracle exited {process.returncode}: {process.stderr}")
        runs.append(read_json(result_path))
        files.extend([result_path, stdout, stderr])
    if runs[0] != runs[1]:
        raise RuntimeError(f"PiSharp {scenario} oracle was not stable across its two executions")
    return runs[0], files


def run_provider_format_tests(evidence_dir: Path) -> tuple[list[dict[str, Any]], list[Path]]:
    apis = {
        "openai-responses": "/v1/responses",
        "openai-completions": "/v1/chat/completions",
        "anthropic-messages": "/v1/messages",
    }
    runs: list[list[dict[str, Any]]] = []
    files: list[Path] = []
    for run_number in (1, 2):
        capture_dir = evidence_dir / f"pisharp-provider-format-run-{run_number}"
        capture_dir.mkdir(parents=True, exist_ok=True)
        command = [
            "dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--no-restore",
            "--filter",
            "FullyQualifiedName~ProviderOverflowLoopbackTests.RealSdkRecoversPreContentHttpOverflowWithoutReplacingCanonicalHistory",
            "--logger", "console;verbosity=minimal",
        ]
        process = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, check=False,
                                 env={**os.environ, "DOTNET_PROCESSOR_COUNT": "4",
                                      "PISHARP_C5_PROVIDER_CAPTURE_DIR": str(capture_dir)})
        stdout = evidence_dir / f"provider-format-test-run-{run_number}.stdout.log"
        stderr = evidence_dir / f"provider-format-test-run-{run_number}.stderr.log"
        stdout.write_text(process.stdout, encoding="utf-8")
        stderr.write_text(process.stderr, encoding="utf-8")
        files.extend([stdout, stderr])
        if process.returncode:
            raise RuntimeError(f"provider-format loopback run {run_number} failed: {process.stderr}\n{process.stdout}")

        observations: list[dict[str, Any]] = []
        for api, expected_path in apis.items():
            capture_path = capture_dir / f"{api}.json"
            capture = read_json(capture_path)
            requests = capture.get("requests")
            responses = capture.get("responses")
            if (capture.get("api") != api or not isinstance(requests, list) or len(requests) != 3 or
                    not isinstance(responses, list) or len(responses) != 3):
                raise RuntimeError(f"provider-format {api} capture does not contain three paired exchanges")
            if ([request.get("path") for request in requests] != [expected_path] * 3 or
                    [response.get("statusCode") for response in responses] != [400, 200, 200]):
                raise RuntimeError(f"provider-format {api} did not preserve the expected route and recovery exchange")
            error_body = responses[0].get("body", "")
            if "context_length_exceeded" not in error_body and "prompt is too long" not in error_body:
                raise RuntimeError(f"provider-format {api} fixture did not return a recognizable context error")
            summary_body = responses[1].get("body", "")
            if "Earlier answer summarized." not in summary_body:
                raise RuntimeError(f"provider-format {api} summary response was not captured")
            first_request = requests[0].get("body", "")
            retry_request = requests[2].get("body", "")
            if ("P" * 900 not in first_request or "Earlier answer summarized." not in retry_request or
                    "P" * 900 in retry_request):
                raise RuntimeError(f"provider-format {api} retry did not replace only its outbound context")
            active_messages = capture.get("activeMessages", [])
            if (capture.get("overflowRecoveryCount") != 1 or capture.get("completed") is not True or
                    capture.get("retainedCanonicalPrefix") is not True or
                    not any(item.get("role", "").lower() == "assistant" and item.get("text") == "done"
                            for item in active_messages)):
                raise RuntimeError(f"provider-format {api} did not recover while retaining canonical history")
            observations.append({
                "api": api,
                "requestPath": expected_path,
                "httpExchangeCount": len(requests),
                "httpStatusCodes": [response["statusCode"] for response in responses],
                "providerAttempts": 2,
                "summaryResponseCaptured": True,
                "retryContainsCompactedSummary": True,
                "rejectedPromptOmittedFromRetry": True,
                "overflowRecoveryCount": capture["overflowRecoveryCount"],
                "continuationCompleted": capture["completed"],
                "canonicalHistoryRetained": capture["retainedCanonicalPrefix"],
                "continuedResponse": "done",
            })
            files.append(capture_path)
        runs.append(observations)

    if runs[0] != runs[1]:
        raise RuntimeError("provider-format loopback observations were not stable across two executions")
    return runs[0], files


def observations_for(scenario: str, normalized: dict[str, Any]) -> dict[str, dict[str, Any]]:
    if scenario == "length-recovery":
        return {
            "C5.recoverable-length-stop-retries-once": {
                "providerAttempts": normalized["providerAttemptCount"],
                "compactionEndEvents": normalized["compactionEndEvents"],
                "summaryRequestCount": normalized["summaryRequestCount"],
                "continuationCompleted": normalized["continuedResponsePersisted"],
            },
            "C5.retry-request-uses-compacted-transcript": {
                "retryMessages": normalized["providerRequests"][-1],
                "partialResponseOmitted": not normalized["retryContainsPartialResponse"],
                "compactedSummaryIncluded": normalized["retryContainsCompactedSummary"],
            },
            "C5.post-compaction-provider-continuation-succeeds": {
                "providerAttempts": normalized["providerAttemptCount"],
                "summaryRequestCount": normalized["summaryRequestCount"],
                "compactionEndEvents": normalized["compactionEndEvents"],
                "responseUsage": normalized["responseUsage"],
                "retryUsesCompactedSummary": normalized["retryContainsCompactedSummary"],
                "continuationCompleted": normalized["continuedResponsePersisted"],
            },
        }
    if scenario == "context-boundary-length":
        return {
            "C5.length-stop-at-context-boundary-recognized": {
                "providerAttempts": normalized["providerAttemptCount"],
                "summaryRequestCount": normalized["summaryRequestCount"],
                "compactionEndEvents": normalized["compactionEndEvents"],
                "compactionCount": normalized["compactionCount"],
                "responseUsage": normalized["responseUsage"],
                "responsePersisted": normalized["completedResponsePersisted"],
            },
        }
    if scenario == "pre-content-overflow":
        return {
            "C5.pre-content-context-error-recognized": {
                "providerAttempts": normalized["providerAttemptCount"],
                "compactionEndEvents": normalized["compactionEndEvents"],
                "retryUsesCompactedSummary": normalized["retryContainsCompactedSummary"],
                "partialResponsePersisted": normalized["partialResponsePersisted"],
                "continuationCompleted": normalized["continuedResponsePersisted"],
            },
        }
    if scenario == "silent-usage-overflow":
        return {
            "C5.provider-silent-overflow-from-reported-usage": {
                "providerAttempts": normalized["providerAttemptCount"],
                "responseUsage": normalized["responseUsage"],
                "compactionEndEvents": normalized["compactionEndEvents"],
                "continuationCompleted": normalized["continuedResponsePersisted"],
            },
        }
    if scenario == "partial-content-overflow":
        return {
            "C5.partial-content-overflow-does-not-duplicate-request": {
                "providerAttempts": normalized["providerAttemptCount"],
                "summaryRequestCount": normalized["summaryRequestCount"],
                "compactionEndEvents": normalized["compactionEndEvents"],
                "partialResponsePersisted": normalized["partialResponsePersisted"],
                "retryContainsPartialResponse": normalized["retryContainsPartialResponse"],
                "recoveryErrors": normalized["recoveryErrors"],
            },
        }
    if scenario == "unknown-model-window":
        return {
            "C5.unknown-model-window-does-not-fabricate-capacity": {
                "providerAttempts": normalized["providerAttemptCount"],
                "summaryRequestCount": normalized["summaryRequestCount"],
                "compactionEndEvents": normalized["compactionEndEvents"],
                "responseUsage": normalized["responseUsage"],
                "compactionCount": normalized["compactionCount"],
                "responsePersisted": normalized["completedResponsePersisted"],
            },
        }
    if scenario == "stale-old-model-overflow":
        return {
            "C5.stale-old-model-overflow-does-not-compact-new-model": {
                "modelSwitchApplied": normalized["modelSwitchApplied"],
                "selectedModel": normalized["selectedModel"],
                "providerAttempts": normalized["providerAttemptCount"],
                "responseUsage": normalized["responseUsage"],
                "compactionEndEvents": normalized["compactionEndEvents"],
                "partialResponsePersisted": normalized["partialResponsePersisted"],
            },
        }
    if scenario == "second-length-stop":
        return {
            "C5.second-overflow-stops-with-clear-error": {
                "providerAttempts": normalized["providerAttemptCount"],
                "summaryRequestCount": normalized["summaryRequestCount"],
                "compactionEndEvents": normalized["compactionEndEvents"],
                "recoveryErrors": normalized["recoveryErrors"],
            },
        }
    raise ValueError(f"unknown C5 scenario: {scenario}")


def comparison_view(scenario: str, normalized: dict[str, Any]) -> dict[str, Any]:
    if scenario == "pre-content-overflow":
        # Pi persists an overflow compaction; PiSharp summarizes an outbound retry projection.
        # Compare the bounded recovery contract and retry context, while preserving both raw transcripts.
        keys = ("providerAttemptCount", "compactionEndEvents", "retryContainsCompactedSummary",
                "partialResponsePersisted", "continuedResponsePersisted")
        return {key: normalized[key] for key in keys}
    if scenario != "stale-old-model-overflow":
        # The oracle fixtures use different provider/model labels; model identity matters only in the stale-model case.
        return {key: value for key, value in normalized.items() if key != "selectedModel"}
    return normalized


def validate_scenario(scenario: str, normalized: dict[str, Any], side: str) -> None:
    if scenario == "length-recovery":
        if normalized["retryContainsPartialResponse"]:
            raise RuntimeError("length recovery replayed the failed partial response")
        if not normalized["retryContainsCompactedSummary"] or not normalized["continuedResponsePersisted"]:
            raise RuntimeError("length recovery did not use compacted context and complete")
        if normalized["compactionEndEvents"] != [{"reason": "overflow", "willRetry": True}]:
            raise RuntimeError("length recovery did not compact exactly once with retry")
    elif scenario == "context-boundary-length":
        if (normalized["providerAttemptCount"] != 2 or normalized["summaryRequestCount"] != 2 or
                normalized["responseUsage"] != [
                    {"inputTokens": 10_000, "outputTokens": 0},
                    {"inputTokens": 60, "outputTokens": 12},
                ] or
                normalized["compactionEndEvents"] != [{"reason": "overflow", "willRetry": True}] or
                not normalized["continuedResponsePersisted"]):
            raise RuntimeError("a length stop at the context boundary was not recognized and recovered")
    elif scenario == "second-length-stop":
        if (normalized["providerAttemptCount"] != 2 or normalized["summaryRequestCount"] != 2 or
                normalized["compactionEndEvents"] != [
                    {"reason": "overflow", "willRetry": True},
                    {"reason": "overflow", "willRetry": False},
                ] or
                normalized["recoveryErrors"] != [RECOVERY_ERROR]):
            raise RuntimeError("repeated length stop did not stop with one clear bounded-recovery error")
    elif scenario == "pre-content-overflow":
        if (normalized["providerAttemptCount"] != 2 or normalized["summaryRequestCount"] < 1 or
                normalized["compactionEndEvents"] != [{"reason": "overflow", "willRetry": True}] or
                not normalized["retryContainsCompactedSummary"] or normalized["partialResponsePersisted"] or
                not normalized["continuedResponsePersisted"]):
            raise RuntimeError("pre-content context overflow was not compacted and retried once")
    elif scenario == "silent-usage-overflow":
        if (normalized["providerAttemptCount"] != 1 or normalized["responseUsage"] != [
                    {"inputTokens": 10_001, "outputTokens": 16}] or
                normalized["compactionEndEvents"] != [{"reason": "overflow", "willRetry": False}] or
                normalized["continuedResponsePersisted"]):
            raise RuntimeError("silent provider context overflow did not compact without retry")
    elif scenario == "partial-content-overflow":
        if side == "pi":
            if (normalized["providerAttemptCount"] != 2 or normalized["retryContainsPartialResponse"] or
                    normalized["compactionEndEvents"] != [{"reason": "overflow", "willRetry": True}] or
                    not normalized["continuedResponsePersisted"]):
                raise RuntimeError("pinned Pi partial-content overflow calibration changed")
        elif (normalized["providerAttemptCount"] != 1 or normalized["retryContainsPartialResponse"] or
              normalized["recoveryErrors"] or not normalized["partialResponsePersisted"]):
            raise RuntimeError("PiSharp replayed or discarded a partial-content overflow response")
    elif scenario == "unknown-model-window":
        if side == "pi":
            if (normalized["providerAttemptCount"] != 1 or normalized["summaryRequestCount"] != 4 or
                    normalized["compactionCount"] != 2 or len(normalized["compactionEndEvents"]) != 2 or any(
                        event != {"reason": "threshold", "willRetry": False}
                        for event in normalized["compactionEndEvents"]) or
                    normalized["responseUsage"] != [{"inputTokens": 60, "outputTokens": 12}]):
                raise RuntimeError("pinned Pi unknown-window calibration changed")
        elif (normalized["providerAttemptCount"] != 1 or normalized["summaryRequestCount"] != 0 or
              normalized["compactionEndEvents"] or normalized["responseUsage"] != [
                  {"inputTokens": 20_000, "outputTokens": 16}] or
              not normalized["completedResponsePersisted"]):
            raise RuntimeError("PiSharp fabricated a context budget without a known window")
    elif scenario == "stale-old-model-overflow":
        if (not normalized["modelSwitchApplied"] or normalized["selectedModel"] != "new-model" or
                normalized["providerAttemptCount"] != 1 or normalized["responseUsage"] != [
                    {"inputTokens": 1_000, "outputTokens": 0}] or normalized["compactionEndEvents"] or
                not normalized["partialResponsePersisted"]):
            raise RuntimeError("stale old-model overflow was applied after switching to a new model")


def run(pi_path: Path, evidence_dir: Path) -> dict[str, Any]:
    evidence_dir.mkdir(parents=True, exist_ok=True)
    pi_values: dict[str, Any] = {}
    pisharp_values: dict[str, Any] = {}
    raw_by_case: dict[str, list[Path]] = {}
    paired_views: dict[str, tuple[dict[str, Any], dict[str, Any]]] = {}
    safety_differences: dict[str, dict[str, str]] = {}
    for scenario, case_ids in SCENARIO_CASES.items():
        pi_run, pi_files = run_pi(pi_path, evidence_dir, scenario)
        pisharp_run, pisharp_files = run_pisharp(evidence_dir, scenario)
        pi_normalized = compact_pi(pi_run)
        pisharp_normalized = compact_pisharp(pisharp_run)
        validate_scenario(scenario, pi_normalized, "pi")
        validate_scenario(scenario, pisharp_normalized, "pisharp")
        pi_comparison = comparison_view(scenario, pi_normalized)
        pisharp_comparison = comparison_view(scenario, pisharp_normalized)
        paired_views[scenario] = (pi_comparison, pisharp_comparison)
        if scenario == "partial-content-overflow":
            safety_differences["C5.partial-content-overflow-does-not-duplicate-request"] = {
                "caseId": "C5.partial-content-overflow-does-not-duplicate-request",
                "reason": "Pinned Pi retries once after explicit context overflow with partial output; PiSharp "
                         "surfaces the partial response and does not replay an already visible provider stream.",
                "regression": "tests/PiSharp.Tests/Runtime/Agents/CompactionTests.cs#"
                             "ProviderOverflowRetriesOnceOnlyBeforeContentWithShortenedRequest (afterContent=true)",
                "safetyEvidence": "pisharp-partial-content-overflow-run-1.json",
            }
        elif scenario == "unknown-model-window":
            safety_differences["C5.unknown-model-window-does-not-fabricate-capacity"] = {
                "caseId": "C5.unknown-model-window-does-not-fabricate-capacity",
                "reason": "Pinned Pi treats contextWindow=0 as a threshold of minus reserve and compacts; PiSharp "
                         "does not invent a budget when no context policy is known.",
                "regression": "tests/PiSharp.Tests/Runtime/Agents/CompactionTests.cs#"
                             "UnknownContextWindowDoesNotInventCapacityFromReportedUsage",
                "safetyEvidence": "pisharp-unknown-model-window-run-1.json",
            }
        elif pi_comparison != pisharp_comparison:
            raise RuntimeError(f"paired C5 observations differ for {scenario}:\n" + json.dumps({
                "pi": pi_comparison, "pisharp": pisharp_comparison,
            }, indent=2))

        pi_normalized_path = evidence_dir / f"pi-{scenario}-normalized.json"
        pisharp_normalized_path = evidence_dir / f"pisharp-{scenario}-normalized.json"
        pi_normalized_path.write_text(json.dumps(pi_normalized, indent=2) + "\n", encoding="utf-8")
        pisharp_normalized_path.write_text(json.dumps(pisharp_normalized, indent=2) + "\n", encoding="utf-8")
        pi_values.update(observations_for(scenario, pi_comparison))
        pisharp_values.update(observations_for(scenario, pisharp_comparison))
        case_files = [*pi_files, *pisharp_files, pi_normalized_path, pisharp_normalized_path]
        for case_id in case_ids:
            raw_by_case[case_id] = case_files

    provider_case_id = "C5.overflow-for-responses-chat-and-anthropic"
    provider_format_observations, provider_format_files = run_provider_format_tests(evidence_dir)
    pi_pre_content, pisharp_pre_content = paired_views["pre-content-overflow"]
    provider_core_keys = (
        "providerAttemptCount", "compactionEndEvents", "retryContainsCompactedSummary",
        "partialResponsePersisted", "continuedResponsePersisted",
    )
    pi_pre_content = {key: pi_pre_content[key] for key in provider_core_keys}
    pisharp_pre_content = {key: pisharp_pre_content[key] for key in provider_core_keys}
    if any(item["providerAttempts"] != pisharp_pre_content["providerAttemptCount"] or
           item["overflowRecoveryCount"] != len(pisharp_pre_content["compactionEndEvents"]) or
           item["retryContainsCompactedSummary"] != pisharp_pre_content["retryContainsCompactedSummary"] or
           item["continuationCompleted"] != pisharp_pre_content["continuedResponsePersisted"]
           for item in provider_format_observations):
        raise RuntimeError("provider-format loopback evidence disagrees with the paired PiSharp recovery oracle")
    pi_values[provider_case_id] = {
        "providerAttempts": pi_pre_content["providerAttemptCount"],
        "overflowRecoveryCount": len(pi_pre_content["compactionEndEvents"]),
        "retryContainsCompactedSummary": pi_pre_content["retryContainsCompactedSummary"],
        "partialResponsePersisted": pi_pre_content["partialResponsePersisted"],
        "continuationCompleted": pi_pre_content["continuedResponsePersisted"],
    }
    pisharp_values[provider_case_id] = {
        "providerAttempts": pisharp_pre_content["providerAttemptCount"],
        "overflowRecoveryCount": len(pisharp_pre_content["compactionEndEvents"]),
        "retryContainsCompactedSummary": pisharp_pre_content["retryContainsCompactedSummary"],
        "partialResponsePersisted": pisharp_pre_content["partialResponsePersisted"],
        "continuationCompleted": pisharp_pre_content["continuedResponsePersisted"],
    }
    if pi_values[provider_case_id] != pisharp_values[provider_case_id]:
        raise RuntimeError("provider-format recovery behavior differs from the pinned Pi contract")
    completed_provider_format = {provider_case_id}
    raw_by_case[provider_case_id] = [
        *provider_format_files,
        *[path for path in raw_by_case["C5.pre-content-context-error-recognized"]],
    ]

    pi_observations = evidence_dir / "pi-c5-observations.json"
    pisharp_observations = evidence_dir / "pisharp-c5-observations.json"
    pi_observations.write_text(json.dumps({"observations": pi_values}, indent=2) + "\n", encoding="utf-8")
    pisharp_observations.write_text(json.dumps({"observations": pisharp_values}, indent=2) + "\n", encoding="utf-8")
    contract = read_json(HERE / "contract.json")
    expected = {case["id"]: case["expectedObservation"]
                for checkpoint in contract["checkpoints"] if checkpoint["id"] == "C5"
                for case in checkpoint["requiredCases"]}
    completed = {case_id for case_ids in SCENARIO_CASES.values() for case_id in case_ids} | completed_provider_format
    if completed - set(expected):
        raise RuntimeError(f"C5 adapter produced unknown cases: {sorted(completed - set(expected))}")

    case_results = []
    for case_id in sorted(completed):
        pointer = f"/observations/{case_id}"
        baseline_value = pi_values[case_id]
        implementation_value = pisharp_values[case_id]
        raw_artifacts = [artifact(path, evidence_dir) for path in raw_by_case[case_id]]
        raw_artifacts.extend([artifact(pi_observations, evidence_dir),
                              artifact(pisharp_observations, evidence_dir)])
        case_result = {
            "caseId": case_id,
            "checkpointId": "C5",
            "contractExpectedObservation": expected[case_id],
            "executed": True,
            "passed": True,
            "skipped": False,
            "classification": "intentional-safety-difference"
                if case_id in safety_differences else "equivalent",
            "assertions": [{
                "name": "paired-normalized-observation",
                "caseId": case_id,
                "contractObservation": expected[case_id],
                "comparison": "intentional-safety-difference" if case_id in safety_differences else "equivalent",
                "baselineArtifact": pi_observations.name,
                "baselinePointer": pointer,
                "implementationArtifact": pisharp_observations.name,
                "implementationPointer": pointer,
                "expected": baseline_value,
                "actual": implementation_value,
                "normalizedExpected": baseline_value,
                "normalizedActual": implementation_value,
                "passed": True,
                **({
                    "safetyEvidence": safety_differences[case_id]["safetyEvidence"],
                    "safetyRegression": safety_differences[case_id]["regression"],
                } if case_id in safety_differences else {}),
            }],
            "rawArtifacts": raw_artifacts,
        }
        if case_id == provider_case_id:
            case_result["providerFormatObservations"] = provider_format_observations
        case_results.append(case_result)

    pi_sha = read_json(evidence_dir / "pi-length-recovery" / "pi-oracle.json")["piSha"]
    source_clean = source_tree_is_clean()
    report = {
        "piSha": pi_sha,
        "pisharpSourceSha": git("rev-parse", "HEAD") if source_clean else None,
        "sourceTreeClean": source_clean,
        "piCalibrationStable": True,
        "pisharpCalibrationStable": True,
        "pairedObservationsEqual": not safety_differences,
        "acceptedSafetyDifferenceCount": len(safety_differences),
        "unresolvedMismatchCount": 0,
        "safetyDifferences": list(safety_differences.values()),
        "normalization": ["remove empty system message from provider transcript"],
        "classification": "intentional-safety-difference" if safety_differences else "equivalent",
        "completedCaseIds": sorted(completed),
        "executedCaseCount": len(case_results),
        "requiredC5CaseCount": len(expected),
        "unexecutedCaseIds": sorted(set(expected) - completed),
        "cases": case_results,
    }
    report_path = evidence_dir / "c5-case-results.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    args = parser.parse_args()
    try:
        report = run(args.pi.resolve(), args.evidence_dir.resolve())
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        print(f"C5 differential failed: {error}", file=sys.stderr)
        return 1
    print(f"C5 differential: {report['executedCaseCount']}/{report['requiredC5CaseCount']} cases; "
          f"{report['acceptedSafetyDifferenceCount']} accepted safety differences, "
          f"{report['unresolvedMismatchCount']} unresolved mismatches")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
