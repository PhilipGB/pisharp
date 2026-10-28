"""Positive and negative controls for the frozen compaction evidence verifier."""
from __future__ import annotations

import copy
import json
import tempfile
import unittest
from pathlib import Path

import verify


class CompactionContextVerifierTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.contract = verify.load_contract()
        cls.required = verify.required_cases(cls.contract)

    def valid_evidence(self) -> dict:
        cases = []
        for case_id, (checkpoint, contract_case) in self.required.items():
            expected = contract_case["expectedObservation"]
            cases.append({
                "caseId": case_id,
                "checkpointId": checkpoint["id"],
                "contractExpectedObservation": expected,
                "executed": True,
                "passed": True,
                "skipped": False,
                "classification": "strict",
                "assertions": [{"name": "control", "comparison": "strict",
                                "expected": expected, "actual": expected, "passed": True}],
                "rawArtifacts": [{"path": "control.json", "sha256": "a" * 64}],
            })
        checkpoints = []
        for item in self.contract["checkpoints"]:
            count = len(item["requiredCases"])
            checkpoints.append({"checkpointId": item["id"], "requiredCaseCount": count,
                                "executedCaseCount": count, "failedCaseCount": 0,
                                "skippedCaseCount": 0, "status": "passed"})
        return {
            "provenance": {
                "contractSha256": verify.FROZEN_CONTRACT_SHA256,
                "piSha": verify.PINNED_PI_SHA,
                "pisharpSourceSha": self.contract["sourcePins"]["pisharpBaseCommit"],
            },
            "cases": cases,
            "checkpointResults": checkpoints,
        }

    def check(self, evidence: dict) -> None:
        verify.validate_evidence(evidence, Path("."), verify_artifacts=False, verify_source=False)

    def assert_rejected(self, evidence: dict) -> None:
        with self.assertRaises(verify.VerificationError):
            self.check(evidence)

    def mutate_assertion(self, evidence: dict, case_id: str, *, actual: object) -> None:
        case = next(item for item in evidence["cases"] if item["caseId"] == case_id)
        case["assertions"][0]["actual"] = actual

    def test_valid_positive_control_is_accepted(self) -> None:
        result = verify.validate_evidence(self.valid_evidence(), Path("."),
                                          verify_artifacts=False, verify_source=False)
        self.assertEqual(self.contract["requiredCaseCount"], result["executed"])
        self.assertEqual(self.contract["checkpointCount"], result["checkpoints"])

    def test_altered_pi_source_pin_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        evidence["provenance"]["piSha"] = "0" * 40
        self.assert_rejected(evidence)

    def test_mutated_provider_request_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        self.mutate_assertion(evidence, "C11.responses-wire-request-after-compaction", actual={"messages": []})
        self.assert_rejected(evidence)

    def test_incorrect_cut_point_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        self.mutate_assertion(evidence, "C3.user-boundary-selection", actual={"firstKeptEntryId": "wrong"})
        self.assert_rejected(evidence)

    def test_dropped_tool_call_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        self.mutate_assertion(evidence, "C6.parallel-calls-and-results-remain-matched", actual={"calls": 1, "results": 2})
        self.assert_rejected(evidence)

    def test_duplicated_tool_call_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        self.mutate_assertion(evidence, "C3.sequential-tool-cycles", actual={"calls": 3, "results": 2})
        self.assert_rejected(evidence)

    def test_wrong_branch_selection_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        self.mutate_assertion(evidence, "C8.branch-context-is-reconstructed-from-selected-path", actual={"branch": "inactive"})
        self.assert_rejected(evidence)

    def test_missing_usage_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        self.mutate_assertion(evidence, "C10.input-output-cache-read-cache-write-remain-distinct", actual={"usage": None})
        self.assert_rejected(evidence)

    def test_missing_cost_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        self.mutate_assertion(evidence, "C10.known-pricing-and-input-tiers-are-calculated", actual={"cost": None})
        self.assert_rejected(evidence)

    def test_incorrect_cancellation_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        self.mutate_assertion(evidence, "C13.cancel-during-summary-cleans-lifecycle-state", actual={"cancelled": False})
        self.assert_rejected(evidence)

    def test_missing_required_case_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        evidence["cases"].pop()
        self.assert_rejected(evidence)

    def test_skipped_required_case_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        evidence["cases"][0]["skipped"] = True
        self.assert_rejected(evidence)

    def test_failed_case_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        evidence["cases"][0]["passed"] = False
        self.assert_rejected(evidence)

    def test_stale_implementation_source_is_rejected(self) -> None:
        with self.assertRaises(verify.VerificationError):
            verify.validate_source_identity("d969d3ec5c4f0bb90e9c446a6c505c0374f59dd9",
                                            "12064420179f4119bdaf37479c7cba0f5ca40717")

    def test_failed_ci_result_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        self.mutate_assertion(evidence, "CI0.every-required-job-and-case-passes", actual={"conclusion": "failure"})
        self.assert_rejected(evidence)

    def test_wrong_head_ci_result_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        self.mutate_assertion(evidence, "CI0.push-workflow-is-on-exact-final-head", actual={"headSha": "0" * 40})
        self.assert_rejected(evidence)

    def test_missing_checkpoint_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        evidence["checkpointResults"].pop()
        self.assert_rejected(evidence)

    def test_changed_contract_denominator_is_rejected(self) -> None:
        contract = copy.deepcopy(self.contract)
        contract["checkpoints"][4]["requiredCases"].pop()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "contract.json"
            path.write_text(json.dumps(contract), encoding="utf-8")
            with self.assertRaises(verify.VerificationError):
                verify.load_contract(path)

    def test_missing_raw_artifact_is_rejected(self) -> None:
        evidence = self.valid_evidence()
        with self.assertRaises(verify.VerificationError):
            verify.validate_evidence(evidence, Path("/definitely/missing"),
                                     verify_artifacts=True, verify_source=False)


if __name__ == "__main__":
    unittest.main()
