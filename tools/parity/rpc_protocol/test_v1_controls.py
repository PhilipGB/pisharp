#!/usr/bin/env python3
"""Positive and mutation controls for the frozen RPC evidence verifier."""
from __future__ import annotations

import copy
import hashlib
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from typing import Any, Callable

import verify


NEGATIVE_CONTROL_IDS = {
    "V1.wrong-pi-sha",
    "V1.wrong-implementation-sha",
    "V1.stale-evidence",
    "V1.mutated-jsonl-framing",
    "V1.missing-response",
    "V1.duplicate-response",
    "V1.wrong-correlation-id",
    "V1.reordered-lifecycle",
    "V1.dropped-stream-fragment",
    "V1.altered-tool-arguments",
    "V1.incorrect-queue-state",
    "V1.incorrect-session-selection",
    "V1.incorrect-token-cost-totals",
    "V1.incorrect-cancellation-settlement",
    "V1.unexecuted-case",
    "V1.skipped-case",
    "V1.unexpected-case-id",
    "V1.missing-raw-capture",
    "V1.altered-artifact-hash",
    "V1.failed-ci",
    "V1.wrong-head-ci",
    "V1.changed-contract",
}


def _records() -> list[dict[str, Any]]:
    return [
        {"type": "response", "id": "req-001", "command": "get_state", "success": True, "data": {}},
        {"type": "event", "event": "message_update", "delta": "hello"},
        {"type": "event", "event": "message_end", "role": "assistant"},
        {"type": "event", "event": "turn_end"},
        {"type": "toolcall", "id": "tool-1", "arguments": {"value": 1}},
        {"type": "event", "event": "queue_update", "pendingMessageCount": 1},
        {"type": "session", "sessionPath": "/sessions/a.jsonl", "leafId": "leaf-a"},
        {"type": "stats", "usage": {"input": 2, "output": 1, "cost": {"total": 0.25}}},
        {"type": "event", "event": "agent_settled"},
    ]


def _write_capture_pair(directory: Path) -> dict[str, Any]:
    records = {"records": _records()}
    pi_path = directory / "pi1.stdout"
    pisharp_path = directory / "pisharp.stdout"
    pi_path.write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
    pisharp_path.write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
    case = next(
        case for case in verify.load_contract()["checkpoints"]
        if case["id"] == "V1"
    )["caseDefinitions"][0]
    value = json.loads(pi_path.read_text(encoding="utf-8"))["records"]
    return {
        "case": case,
        "directory": directory,
        "pi_path": pi_path,
        "pisharp_path": pisharp_path,
        "result": {
            "caseId": case["id"],
            "checkpointId": "V1",
            "contractExpectedObservation": case["expectedObservation"],
            "executed": True,
            "passed": True,
            "skipped": False,
            "rawArtifacts": [
                {"path": "pi1.stdout", "role": "pi1.stdout", "sha256": hashlib.sha256(pi_path.read_bytes()).hexdigest()},
                {"path": "pisharp.stdout", "role": "pisharp.stdout", "sha256": hashlib.sha256(pisharp_path.read_bytes()).hexdigest()},
            ],
            "assertions": [{
                "contractObservation": case["expectedObservation"],
                "comparison": case["comparisonRules"]["mode"],
                "piArtifact": "pi1.stdout",
                "pisharpArtifact": "pisharp.stdout",
                "piPointer": "/records",
                "pisharpPointer": "/records",
                "piValue": value,
                "pisharpValue": copy.deepcopy(value),
                "passed": True,
            }],
        },
    }


def _valid_header() -> dict[str, Any]:
    return {
        "contractSha256": verify.FROZEN_CONTRACT_SHA256,
        "piCommit": verify.PINNED_PI_SHA,
        "implementationSourceCommit": verify.BASE_SHA,
        "finalHeadCommit": verify.BASE_SHA,
        "pisharpBaseCommit": verify.BASE_SHA,
    }


def _mutate(records: list[dict[str, Any]], case_id: str) -> None:
    by_event = {record.get("event"): index for index, record in enumerate(records) if record.get("type") == "event"}
    if case_id == "V1.missing-response":
        records.pop(0)
    elif case_id == "V1.duplicate-response":
        records.insert(1, copy.deepcopy(records[0]))
    elif case_id == "V1.wrong-correlation-id":
        records[0]["id"] = "req-999"
    elif case_id == "V1.reordered-lifecycle":
        left, right = by_event["message_end"], by_event["turn_end"]
        records[left], records[right] = records[right], records[left]
    elif case_id == "V1.dropped-stream-fragment":
        records[by_event["message_update"]]["delta"] = "hllo"
    elif case_id == "V1.altered-tool-arguments":
        next(record for record in records if record.get("type") == "toolcall")["arguments"]["value"] = 2
    elif case_id == "V1.incorrect-queue-state":
        records[by_event["queue_update"]]["pendingMessageCount"] = 0
    elif case_id == "V1.incorrect-session-selection":
        next(record for record in records if record.get("type") == "session")["leafId"] = "leaf-b"
    elif case_id == "V1.incorrect-token-cost-totals":
        next(record for record in records if record.get("type") == "stats")["usage"]["cost"]["total"] = 9.99
    elif case_id == "V1.incorrect-cancellation-settlement":
        records.pop(by_event["agent_settled"])
    else:
        raise AssertionError(f"no raw-capture mutation registered for {case_id}")


class FrozenRpcVerifierMutationControls(unittest.TestCase):
    def test_control_suite_covers_the_frozen_v1_mutation_corpus(self) -> None:
        contract = verify.load_contract()
        checkpoint = next(item for item in contract["checkpoints"] if item["id"] == "V1")
        frozen_ids = {case["id"] for case in checkpoint["caseDefinitions"]}
        positive_id = "V1.valid-positive-control"
        self.assertEqual(NEGATIVE_CONTROL_IDS, frozen_ids - {positive_id})
        self.assertIn(positive_id, frozen_ids)

    def test_valid_source_bound_positive_control_is_accepted(self) -> None:
        header = _valid_header()
        verify.validate_evidence_header(header, current_head=verify.BASE_SHA, require_clean_worktree=False)
        with tempfile.TemporaryDirectory() as temporary:
            fixture = _write_capture_pair(Path(temporary))
            counts = verify.validate_case_results(
                [fixture["result"]], "V1", fixture["directory"], require_complete=False,
            )
        self.assertEqual({"required": 23, "executed": 1, "passed": 1, "failed": 0, "skipped": 0}, counts)

    def test_uncommitted_source_changes_invalidate_evidence(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repository = Path(temporary)
            subprocess.run(["git", "init", "-q", str(repository)], check=True)
            subprocess.run(["git", "-C", str(repository), "config", "user.name", "Parity Test"], check=True)
            subprocess.run(["git", "-C", str(repository), "config", "user.email", "parity@example.invalid"], check=True)
            source = repository / "src" / "owner.txt"
            source.parent.mkdir()
            source.write_text("committed", encoding="utf-8")
            subprocess.run(["git", "-C", str(repository), "add", "src/owner.txt"], check=True)
            subprocess.run(["git", "-C", str(repository), "commit", "-qm", "baseline"], check=True)
            source.write_text("uncommitted mutation", encoding="utf-8")
            with self.assertRaisesRegex(verify.VerificationError, "uncommitted production, test, parity or workflow"):
                verify.validate_clean_source_tree(cwd=repository)

    def test_implementation_source_pin_allows_docs_and_evidence_workflow_after_source(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repository = Path(temporary)
            subprocess.run(["git", "init", "-q", str(repository)], check=True)
            subprocess.run(["git", "-C", str(repository), "config", "user.name", "Parity Test"], check=True)
            subprocess.run(["git", "-C", str(repository), "config", "user.email", "parity@example.invalid"], check=True)
            source = repository / "src" / "owner.txt"
            source.parent.mkdir()
            source.write_text("implementation", encoding="utf-8")
            subprocess.run(["git", "-C", str(repository), "add", "src/owner.txt"], check=True)
            subprocess.run(["git", "-C", str(repository), "commit", "-qm", "implementation source"], check=True)
            source_commit = subprocess.run(["git", "-C", str(repository), "rev-parse", "HEAD"],
                                           capture_output=True, text=True, check=True).stdout.strip()

            workflow = repository / ".github" / "workflows" / "rpc-parity-evidence.yml"
            workflow.parent.mkdir(parents=True)
            workflow.write_text("name: RPC parity evidence\n", encoding="utf-8")
            docs = repository / "docs" / "receipt.md"
            docs.parent.mkdir()
            docs.write_text(f"source {source_commit}\n", encoding="utf-8")
            subprocess.run(["git", "-C", str(repository), "add", "."], check=True)
            subprocess.run(["git", "-C", str(repository), "commit", "-qm", "publish evidence workflow"], check=True)
            final_head = subprocess.run(["git", "-C", str(repository), "rev-parse", "HEAD"],
                                        capture_output=True, text=True, check=True).stdout.strip()

            self.assertEqual(source_commit, verify.current_implementation_source_commit(cwd=repository))
            verify.validate_source_identity(source_commit, final_head, cwd=repository)

            source.write_text("changed implementation", encoding="utf-8")
            subprocess.run(["git", "-C", str(repository), "add", "src/owner.txt"], check=True)
            subprocess.run(["git", "-C", str(repository), "commit", "-qm", "change implementation"], check=True)
            changed_head = subprocess.run(["git", "-C", str(repository), "rev-parse", "HEAD"],
                                          capture_output=True, text=True, check=True).stdout.strip()
            with self.assertRaisesRegex(verify.VerificationError, "evidence is stale"):
                verify.validate_source_identity(source_commit, changed_head, cwd=repository)

    def test_source_and_frozen_contract_mutations_are_rejected(self) -> None:
        with self.subTest(case_id="V1.wrong-pi-sha"):
            header = _valid_header()
            header["piCommit"] = "0" + header["piCommit"][1:]
            with self.assertRaisesRegex(verify.VerificationError, "pinned Pi SHA"):
                verify.validate_evidence_header(header, require_clean_worktree=False)
        with self.subTest(case_id="V1.wrong-implementation-sha"):
            header = _valid_header()
            header["implementationSourceCommit"] = "ac85aa2a0f59566115eb0109cc9022dc64f4d8fe"
            with self.assertRaisesRegex(verify.VerificationError, "not an ancestor"):
                verify.validate_evidence_header(header, require_clean_worktree=False)
        with self.subTest(case_id="V1.stale-evidence"):
            header = _valid_header()
            header["finalHeadCommit"] = "ac85aa2a0f59566115eb0109cc9022dc64f4d8fe"
            with self.assertRaisesRegex(verify.VerificationError, "evidence is stale"):
                verify.validate_evidence_header(header, require_clean_worktree=False)
        with self.subTest(case_id="V1.changed-contract"):
            original = json.loads(verify.CONTRACT_PATH.read_text(encoding="utf-8"))
            original["checkpoints"][0]["caseDefinitions"][0]["inputs"] += " changed"
            with tempfile.TemporaryDirectory() as temporary:
                path = Path(temporary) / "contract.json"
                path.write_text(json.dumps(original), encoding="utf-8")
                with self.assertRaisesRegex(verify.VerificationError, "digest changed"):
                    verify.load_contract(path)

    def test_semantic_raw_capture_mutations_are_rejected_by_the_locked_digest(self) -> None:
        semantic_mutations = {
            "V1.missing-response",
            "V1.duplicate-response",
            "V1.wrong-correlation-id",
            "V1.reordered-lifecycle",
            "V1.dropped-stream-fragment",
            "V1.altered-tool-arguments",
            "V1.incorrect-queue-state",
            "V1.incorrect-session-selection",
            "V1.incorrect-token-cost-totals",
            "V1.incorrect-cancellation-settlement",
        }
        self.assertTrue(semantic_mutations.issubset(NEGATIVE_CONTROL_IDS))
        for case_id in sorted(semantic_mutations):
            with self.subTest(case_id=case_id), tempfile.TemporaryDirectory() as temporary:
                fixture = _write_capture_pair(Path(temporary))
                raw = json.loads(fixture["pisharp_path"].read_text(encoding="utf-8"))
                _mutate(raw["records"], case_id)
                fixture["pisharp_path"].write_text(json.dumps(raw, indent=2) + "\n", encoding="utf-8")
                with self.assertRaisesRegex(verify.VerificationError, "artifact missing or hash mismatch"):
                    verify.validate_case_results(
                        [fixture["result"]], "V1", fixture["directory"], require_complete=False,
                    )

    def test_framing_and_artifact_hash_mutations_are_rejected(self) -> None:
        with self.subTest(case_id="V1.mutated-jsonl-framing"), tempfile.TemporaryDirectory() as temporary:
            fixture = _write_capture_pair(Path(temporary))
            raw_bytes = fixture["pisharp_path"].read_bytes()
            fixture["pisharp_path"].write_bytes(raw_bytes.replace(b"\n", b"\r\n", 1))
            with self.assertRaisesRegex(verify.VerificationError, "artifact missing or hash mismatch"):
                verify.validate_case_results(
                    [fixture["result"]], "V1", fixture["directory"], require_complete=False,
                )
        with self.subTest(case_id="V1.altered-artifact-hash"), tempfile.TemporaryDirectory() as temporary:
            fixture = _write_capture_pair(Path(temporary))
            fixture["result"]["rawArtifacts"][0]["sha256"] = "0" * 64
            with self.assertRaisesRegex(verify.VerificationError, "artifact missing or hash mismatch"):
                verify.validate_case_results(
                    [fixture["result"]], "V1", fixture["directory"], require_complete=False,
                )

    def test_unexecuted_skipped_unexpected_and_missing_capture_are_rejected(self) -> None:
        mutations: list[tuple[str, Callable[[dict[str, Any]], list[dict[str, Any]]], str]] = [
            ("V1.unexecuted-case", lambda result: (result.__setitem__("executed", False) or [result]), "did not execute"),
            ("V1.skipped-case", lambda result: (result.__setitem__("skipped", True) or [result]), "was skipped"),
            ("V1.unexpected-case-id", lambda result: [result, {"caseId": "V1.not-in-contract"}], "unknown or duplicate"),
            ("V1.missing-raw-capture", lambda result: (result.__setitem__("rawArtifacts", []) or [result]), "no raw evidence"),
        ]
        for case_id, mutate_result, expected_error in mutations:
            with self.subTest(case_id=case_id), tempfile.TemporaryDirectory() as temporary:
                fixture = _write_capture_pair(Path(temporary))
                results = mutate_result(fixture["result"])
                with self.assertRaisesRegex(verify.VerificationError, expected_error):
                    verify.validate_case_results(
                        results, "V1", fixture["directory"], require_complete=False,
                    )

    def test_failed_and_wrong_head_exact_ci_are_rejected(self) -> None:
        jobs = {job: "success" for job in verify.required_ci_jobs()}
        valid = {
            "runs": [
                {"runId": "push-1", "event": "push", "headSha": verify.BASE_SHA, "conclusion": "success", "jobs": dict(jobs)},
                {"runId": "pr-1", "event": "pull_request", "headSha": verify.BASE_SHA, "baseSha": verify.BASE_SHA, "conclusion": "success", "jobs": dict(jobs)},
            ]
        }
        failed = copy.deepcopy(valid)
        failed["runs"][0]["jobs"][next(iter(jobs))] = "failure"
        with self.subTest(case_id="V1.failed-ci"), self.assertRaisesRegex(verify.VerificationError, "failed, skipped or cancelled"):
            verify.validate_ci_evidence(failed, final_head_sha=verify.BASE_SHA, milestone_base_sha=verify.BASE_SHA)
        wrong_head = copy.deepcopy(valid)
        wrong_head["runs"][1]["headSha"] = "0" * 40
        with self.subTest(case_id="V1.wrong-head-ci"), self.assertRaisesRegex(verify.VerificationError, "not for the final PR head"):
            verify.validate_ci_evidence(wrong_head, final_head_sha=verify.BASE_SHA, milestone_base_sha=verify.BASE_SHA)


if __name__ == "__main__":
    unittest.main()
