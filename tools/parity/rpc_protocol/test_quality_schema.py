#!/usr/bin/env python3
"""Positive and negative controls for authoritative Q0 .NET test counts."""
from __future__ import annotations

import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from typing import Any
from unittest.mock import patch

import finalization_oracle
import quality_oracle
import verify


def _write_artifact(evidence_dir: Path, relative: str, value: Any, role: str) -> dict[str, str]:
    payload = (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")
    path = evidence_dir / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return {"path": relative, "role": role, "sha256": hashlib.sha256(payload).hexdigest()}


def _fixture(evidence_dir: Path) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
    case = next(item for item in verify.load_contract()["checkpoints"] if item["id"] == "Q0")["caseDefinitions"]
    case = next(item for item in case if item["id"] == "Q0.full-dotnet-suite")
    counts = {"passed": 123, "failed": 0, "skipped": 0, "total": 123}
    raw_command = {
        "argv": ["dotnet", "test", "PiSharp.slnx"],
        "cwd": str(verify.ROOT),
        "exitCode": 0,
        "stdout": "Test run for PiSharp.Tests.dll\nPassed! - Failed: 0, Passed: 123, Skipped: 0, Total: 123\n",
        "stderr": "",
    }
    command_ref = _write_artifact(evidence_dir, "raw/Q0.full-dotnet-suite/command-1.json",
                                  raw_command, "Q0.full-dotnet-suite.command-1")
    observation = {
        "expectedObservation": case["expectedObservation"],
        "accepted": True,
        "summary": {"commandsPassed": True, "passed": True, "testCounts": counts},
        "commandArtifactPaths": [command_ref["path"]],
    }
    observation_ref = _write_artifact(evidence_dir, "cases/Q0.full-dotnet-suite/observation.json",
                                      observation, "Q0.full-dotnet-suite.observation")
    acceptance_ref = _write_artifact(evidence_dir, "cases/Q0.full-dotnet-suite/acceptance.json", {
        "accepted": True, "expectedObservation": case["expectedObservation"],
    }, "Q0.full-dotnet-suite.acceptance")
    result = {
        "caseId": case["id"],
        "checkpointId": "Q0",
        "contractExpectedObservation": case["expectedObservation"],
        "executed": True,
        "passed": True,
        "skipped": False,
        "rawArtifacts": [command_ref, acceptance_ref, observation_ref],
        "assertions": [{
            "contractObservation": case["expectedObservation"],
            "comparison": "strict",
            "piArtifact": acceptance_ref["path"],
            "piPointer": "/accepted",
            "piValue": True,
            "pisharpArtifact": observation_ref["path"],
            "pisharpPointer": "/accepted",
            "pisharpValue": True,
            "passed": True,
        }],
    }
    evidence = {
        "checkpointId": "Q0",
        "implementationSourceCommit": "a" * 40,
        "qualitySourceCommit": "a" * 40,
        "caseResults": [result],
    }
    return evidence, observation, raw_command


def _refresh_artifact(evidence_dir: Path, result: dict[str, Any], role: str, value: Any) -> None:
    reference = next(item for item in result["rawArtifacts"] if item["role"] == role)
    refreshed = _write_artifact(evidence_dir, reference["path"], value, role)
    reference.update(refreshed)


class Q0AuthoritativeCountTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.evidence_dir = self.root / "Q0"
        self.evidence_dir.mkdir()

    def fixture(self) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
        self.evidence_dir.mkdir(parents=True, exist_ok=True)
        return _fixture(self.evidence_dir)

    def observation_from_disk(self) -> dict[str, Any]:
        return json.loads((self.evidence_dir / "cases/Q0.full-dotnet-suite/observation.json").read_text())

    def test_valid_q0_counts_are_derived_from_raw_output(self) -> None:
        evidence, _, _ = self.fixture()
        self.assertEqual({"passed": 123, "failed": 0, "skipped": 0, "total": 123},
                         verify.validate_q0_authoritative_counts(evidence, self.evidence_dir,
                                                                 expected_source_commit="a" * 40))

    def test_correct_generator_preserves_numbers_before_setting_boolean_gate(self) -> None:
        summary = {"passed": 123, "failed": 0, "skipped": 0, "total": 123, "commandsPassed": True}
        generated = quality_oracle._with_gate_result("Q0.full-dotnet-suite", summary, True)
        self.assertIs(generated["passed"], True)
        self.assertEqual({"passed": 123, "failed": 0, "skipped": 0, "total": 123}, generated["testCounts"])

    def test_legacy_boolean_overwrite_is_rejected(self) -> None:
        evidence, observation, _ = self.fixture()
        observation["summary"].pop("testCounts")
        _refresh_artifact(self.evidence_dir, evidence["caseResults"][0],
                          "Q0.full-dotnet-suite.observation", observation)
        with self.assertRaisesRegex(verify.VerificationError, "testCounts must contain"):
            verify.validate_q0_authoritative_counts(evidence, self.evidence_dir)

    def test_invalid_count_schema_is_rejected(self) -> None:
        mutations = {
            "boolean": lambda counts: counts.update(passed=True),
            "string": lambda counts: counts.update(passed="123"),
            "missing": lambda counts: counts.pop("skipped"),
            "negative": lambda counts: counts.update(failed=-1),
            "zero-total": lambda counts: counts.update(passed=0, failed=0, skipped=0, total=0),
            "inconsistent-sum": lambda counts: counts.update(passed=122),
            "failed-but-accepted": lambda counts: counts.update(passed=122, failed=1),
            "skipped-but-accepted": lambda counts: counts.update(passed=122, skipped=1),
        }
        for name, mutate in mutations.items():
            with self.subTest(name=name):
                case_dir = self.root / name
                evidence, observation, _ = _fixture(case_dir)
                mutate(observation["summary"]["testCounts"])
                _refresh_artifact(case_dir, evidence["caseResults"][0],
                                  "Q0.full-dotnet-suite.observation", observation)
                with self.assertRaises(verify.VerificationError):
                    verify.validate_q0_authoritative_counts(evidence, case_dir)

    def test_counts_changed_without_matching_raw_output_are_rejected(self) -> None:
        evidence, observation, _ = self.fixture()
        observation["summary"]["testCounts"]["passed"] = 124
        observation["summary"]["testCounts"]["total"] = 124
        _refresh_artifact(self.evidence_dir, evidence["caseResults"][0],
                          "Q0.full-dotnet-suite.observation", observation)
        with self.assertRaisesRegex(verify.VerificationError, "differ from the hashed raw"):
            verify.validate_q0_authoritative_counts(evidence, self.evidence_dir)

    def test_wrong_suite_and_raw_artifact_mutation_are_rejected(self) -> None:
        evidence, _, raw_command = self.fixture()
        raw_command["argv"] = ["dotnet", "test", "tests/PiSharp.Tests"]
        _refresh_artifact(self.evidence_dir, evidence["caseResults"][0],
                          "Q0.full-dotnet-suite.command-1", raw_command)
        with self.assertRaisesRegex(verify.VerificationError, "not from the full dotnet test"):
            verify.validate_q0_authoritative_counts(evidence, self.evidence_dir)

    def test_wrong_source_and_stale_quality_sha_are_rejected(self) -> None:
        evidence, _, _ = self.fixture()
        with self.assertRaisesRegex(verify.VerificationError, "differs from the publication source"):
            verify.validate_q0_authoritative_counts(evidence, self.evidence_dir,
                                                    expected_source_commit="b" * 40)
        evidence["qualitySourceCommit"] = "b" * 40
        with self.assertRaisesRegex(verify.VerificationError, "quality source SHA is stale"):
            verify.validate_q0_authoritative_counts(evidence, self.evidence_dir)

    def test_pub0_uses_only_numeric_total_and_rejects_wrong_pr_description_count(self) -> None:
        evidence, _, _ = self.fixture()
        counts = verify.validate_q0_authoritative_counts(evidence, self.evidence_dir)
        contract = verify.load_contract()
        mode_counts = {
            mode: sum(case["comparisonRules"]["mode"] == mode for case in contract["cases"])
            for mode in ("strict", "equivalent", "intentional-safety-difference")
        }
        source = "a" * 40
        head = "b" * 40
        artifact_name = f"rpc-parity-evidence-{head}"
        artifact_digest = "sha256:" + "c" * 64
        ci = {"runs": [
            {"event": "push", "runId": "11"},
            {"event": "pull_request", "runId": "12"},
        ], "rpcParityEvidence": {
            "workflowName": "RPC parity evidence", "event": "push", "runId": "13",
            "headSha": head, "conclusion": "success", "jobs": {"generate-and-publish": "success"},
            "artifact": {"name": artifact_name, "digest": artifact_digest},
        }}

        def pull_body(test_count: int) -> str:
            return " ".join((
                "24/24", "252/252", verify.PINNED_PI_SHA, verify.BASE_SHA, source, head,
                "11", "12", f"Strict: {mode_counts['strict']}",
                f"Equivalent: {mode_counts['equivalent']}",
                f"Intentional safety differences: {mode_counts['intentional-safety-difference']}",
                f"{test_count:,}/{test_count:,}", "20 credential-gated", "unverified",
                "managed-package parity corpus", "13", artifact_name, artifact_digest,
            ))

        pr = {"body": pull_body(123), "headSha": head}
        matched, report = finalization_oracle._publication_description_check(pr, source, ci, counts)
        self.assertTrue(matched)
        self.assertEqual(123, report["fullDotnetTestCount"])
        pr["body"] = pull_body(122)
        matched, report = finalization_oracle._publication_description_check(pr, source, ci, counts)
        self.assertFalse(matched)
        self.assertEqual(123, report["fullDotnetTestCount"])

    def test_pub0_requires_source_pinned_rpc_evidence_artifact_reference(self) -> None:
        evidence, _, _ = self.fixture()
        counts = verify.validate_q0_authoritative_counts(evidence, self.evidence_dir)
        source = "a" * 40
        head = "b" * 40
        ci = {"runs": [
            {"event": "push", "runId": "11"},
            {"event": "pull_request", "runId": "12"},
        ], "rpcParityEvidence": {
            "workflowName": "RPC parity evidence", "event": "push", "runId": "13",
            "headSha": head, "conclusion": "success", "jobs": {"generate-and-publish": "success"},
            "artifact": {"name": f"rpc-parity-evidence-{head}", "digest": "sha256:" + "c" * 64},
        }}
        body = " ".join(("24/24", "252/252", verify.PINNED_PI_SHA, verify.BASE_SHA, source, head,
                         "11", "12", "13", f"rpc-parity-evidence-{head}", "sha256:" + "c" * 64,
                         "Strict: 230", "Equivalent: 22", "Intentional safety differences: 0",
                         "123/123", "20 credential-gated", "unverified", "managed-package parity corpus"))
        matched, _ = finalization_oracle._publication_description_check(
            {"body": body, "headSha": head}, source, ci, counts)
        self.assertTrue(matched)
        ci["rpcParityEvidence"]["artifact"]["digest"] = "sha256:" + "d" * 64
        matched, _ = finalization_oracle._publication_description_check(
            {"body": body, "headSha": head}, source, ci, counts)
        self.assertFalse(matched)

    def test_ci0_requires_an_unexpired_exact_head_evidence_artifact(self) -> None:
        head = "e" * 40
        record = {
            "workflowName": "RPC parity evidence", "event": "push", "runId": "42",
            "headSha": head, "conclusion": "success", "jobs": {"generate-and-publish": "success"},
            "artifact": {
                "artifactId": 7, "name": f"rpc-parity-evidence-{head}",
                "digest": "sha256:" + "f" * 64, "sizeInBytes": 1024,
                "expired": False, "headSha": head,
            },
        }
        verify.validate_rpc_parity_artifact(record, final_head_sha=head)
        for mutate in (
            lambda item: item["artifact"].update(expired=True),
            lambda item: item["artifact"].update(headSha="a" * 40),
            lambda item: item["artifact"].update(digest="sha1:" + "f" * 40),
            lambda item: item.update(conclusion="failure"),
        ):
            invalid = copy.deepcopy(record)
            mutate(invalid)
            with self.assertRaises(verify.VerificationError):
                verify.validate_rpc_parity_artifact(invalid, final_head_sha=head)

    def test_ci0_selects_linux_ci_separately_from_the_parity_evidence_workflow(self) -> None:
        head = "a" * 40
        runs = [
            {"databaseId": 22, "event": "push", "headSha": head, "createdAt": "2026-10-30T12:02:00Z",
             "workflowName": "RPC parity evidence"},
            {"databaseId": 21, "event": "push", "headSha": head, "createdAt": "2026-10-30T12:01:00Z",
             "workflowName": "Linux CI"},
        ]
        detail = {"databaseId": 21, "event": "push", "headSha": head, "conclusion": "success"}
        with patch.object(finalization_oracle, "_gh_json", side_effect=[
            (runs, {"stdout": "runs"}), (detail, {"stdout": "detail"}),
        ]):
            selected, _ = finalization_oracle._latest_exact_run(
                "push", "milestone/rpc-protocol-command-parity", head)
        self.assertEqual(21, selected["databaseId"])

    def test_ci0_and_pub0_counts_must_match_q0_numeric_total(self) -> None:
        evidence, _, _ = self.fixture()
        counts = verify.validate_q0_authoritative_counts(evidence, self.evidence_dir)
        self.assertEqual(123, verify.validate_publication_test_count({"fullDotnetTestCount": 123}, counts))
        for invalid in (True, "123", 122, None):
            with self.subTest(invalid=invalid):
                with self.assertRaisesRegex(verify.VerificationError, "differs from authoritative"):
                    verify.validate_publication_test_count({"fullDotnetTestCount": invalid}, counts)


class VitestOutputParsingTests(unittest.TestCase):
    def test_summary_counts_ansi_colored_vitest_output(self) -> None:
        step = {
            "stdout": "\x1b[2m      Tests \x1b[22m \x1b[1m\x1b[32m582 passed"
            "\x1b[39m\x1b[22m \x1b[2m|\x1b[22m \x1b[33m20 skipped"
            "\x1b[39m\x1b[90m (602)\x1b[39m\n",
            "stderr": "",
        }

        self.assertEqual({"failed": 0, "passed": 582, "skipped": 20, "total": 602},
                         quality_oracle._summary(step))

    def test_credential_skips_match_inventory_from_ansi_colored_vitest_output(self) -> None:
        output = (
            " \x1b[2m\x1b[90m↓\x1b[39m\x1b[22m "
            "test/agent-session-tree-navigation.test.ts\x1b[2m > \x1b[22m"
            "AgentSession tree navigation e2e\x1b[2m > \x1b[22m"
            "should navigate to user message and put text in editor\n"
        )

        self.assertEqual({
            (
                "packages/coding-agent/test/agent-session-tree-navigation.test.ts",
                "AgentSession tree navigation e2e should navigate to user message and put text in editor",
            ),
        }, quality_oracle._pinned_pi_skips(output))


if __name__ == "__main__":
    unittest.main()
