#!/usr/bin/env python3
"""Contract integrity regressions for the RPC parity acceptance harness."""
from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import verify
from process_oracle import _D0_SCENARIO_CASES, scenario_case_ids
from quality_oracle import quality_case_ids
from source_oracle import SOURCE_CASE_IDS, source_case_ids
from finalization_oracle import CI0_CASE_IDS, E0_CHILD_CHECKPOINTS, PUB0_CASE_IDS


class FrozenRpcContractTests(unittest.TestCase):
    def test_committed_contract_and_inventory_are_source_pinned(self) -> None:
        contract = verify.load_contract()
        self.assertEqual(24, contract["checkpointCount"])
        self.assertEqual(contract["requiredCaseCount"], len(contract["requiredCaseIds"]))
        self.assertEqual(len(contract["requiredCaseIds"]), len(set(contract["requiredCaseIds"])))
        self.assertEqual("b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6", contract["sourcePins"]["piCommit"])

    def test_credential_gated_upstream_tests_are_exact_and_unverified(self) -> None:
        inventory = json.loads(verify.INVENTORY_PATH.read_text(encoding="utf-8"))
        cases = inventory["credentialGatedUpstreamCases"]
        self.assertEqual(20, len(cases))
        self.assertEqual(20, inventory["counts"]["credentialGatedUpstreamCases"])
        self.assertTrue(all("never counted as a pass" in item["classification"] for item in cases))
        self.assertEqual(20, len({(item["source"], item["testName"]) for item in cases}))

    def test_contract_byte_change_fails_frozen_digest(self) -> None:
        original = json.loads(verify.CONTRACT_PATH.read_text(encoding="utf-8"))
        original["requiredCaseIds"][0] = "P0.changed-case"
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "contract.json"
            path.write_text(json.dumps(original), encoding="utf-8")
            with self.assertRaisesRegex(verify.VerificationError, "digest changed"):
                verify.load_contract(path)

    def test_wrong_pi_pin_fails_even_without_digest_check(self) -> None:
        changed = json.loads(verify.CONTRACT_PATH.read_text(encoding="utf-8"))
        changed["sourcePins"]["piCommit"] = "0" * 40
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "contract.json"
            path.write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaisesRegex(verify.VerificationError, "pinned Pi SHA"):
                verify.load_contract(path, verify_lock=False)

    def test_wrong_merge_pin_fails_even_without_digest_check(self) -> None:
        changed = json.loads(verify.CONTRACT_PATH.read_text(encoding="utf-8"))
        changed["sourcePins"]["pisharpBaseCommit"] = "0" * 40
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "contract.json"
            path.write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaisesRegex(verify.VerificationError, "merge base changed"):
                verify.load_contract(path, verify_lock=False)

    def test_duplicate_checkpoint_case_id_is_rejected(self) -> None:
        changed = json.loads(verify.CONTRACT_PATH.read_text(encoding="utf-8"))
        first = changed["checkpoints"][0]["requiredCaseIds"][0]
        replaced = changed["checkpoints"][1]["requiredCaseIds"][0]
        global_index = changed["requiredCaseIds"].index(replaced)
        changed["checkpoints"][1]["requiredCaseIds"][0] = first
        changed["checkpoints"][1]["caseDefinitions"][0]["id"] = first
        changed["requiredCaseIds"][global_index] = first
        changed["cases"][global_index]["id"] = first
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "contract.json"
            path.write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaisesRegex(verify.VerificationError, "duplicate case ID"):
                verify.load_contract(path, verify_lock=False)

    def test_case_without_executable_runner_is_rejected(self) -> None:
        changed = json.loads(verify.CONTRACT_PATH.read_text(encoding="utf-8"))
        changed["checkpoints"][0]["caseDefinitions"][0]["runner"] = ""
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "contract.json"
            path.write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaisesRegex(verify.VerificationError, "lacks case field runner"):
                verify.load_contract(path, verify_lock=False)

    def test_d0_calibration_cases_are_backed_by_process_scenarios(self) -> None:
        contract = verify.load_contract()
        d0 = next(checkpoint for checkpoint in contract["checkpoints"] if checkpoint["id"] == "D0")
        case_ids = [case["id"] for case in d0["caseDefinitions"]]
        registered = scenario_case_ids()
        self.assertEqual(case_ids, list(_D0_SCENARIO_CASES))
        self.assertTrue(set(case_ids).issubset(registered))
        self.assertTrue(all(source in registered for sources in _D0_SCENARIO_CASES.values()
                            for source in sources))

    def test_source_audit_cases_are_registered_in_frozen_order(self) -> None:
        contract = verify.load_contract()
        for checkpoint_id, registered_ids in SOURCE_CASE_IDS.items():
            checkpoint = next(item for item in contract["checkpoints"] if item["id"] == checkpoint_id)
            self.assertEqual(registered_ids, tuple(case["id"] for case in checkpoint["caseDefinitions"]))
        self.assertEqual(set(SOURCE_CASE_IDS["P0"] + SOURCE_CASE_IDS["P1"] + SOURCE_CASE_IDS["V0"]),
                         source_case_ids())

    def test_quality_cases_have_an_executable_gate_in_frozen_order(self) -> None:
        contract = verify.load_contract()
        q0 = next(item for item in contract["checkpoints"] if item["id"] == "Q0")
        self.assertEqual(quality_case_ids(), tuple(case["id"] for case in q0["caseDefinitions"]))

    def test_e0_reverifies_every_prepublication_checkpoint(self) -> None:
        contract = verify.load_contract()
        self.assertEqual(("P0", "P1", "V0", "V1", *[f"R{i}" for i in range(1, 16)], "D0", "Q0"),
                         E0_CHILD_CHECKPOINTS)
        self.assertEqual(24, contract["checkpointCount"])
        self.assertEqual("E0", contract["checkpoints"][-3]["id"])

    def test_e0_rejects_a_missing_or_mutated_artifact_manifest(self) -> None:
        checkpoint_ids = ["P0", "P1", "V0", "V1", *[f"R{i}" for i in range(1, 16)], "D0", "Q0"]
        source = "a" * 40
        evidence = {
            "implementationSourceCommit": source,
            "sourceBoundEvidence": {
                "implementationSourceCommit": source,
                "checkpoints": [{"checkpointId": checkpoint_id} for checkpoint_id in checkpoint_ids],
                "artifactManifest": {"path": "E0/evidence-manifest.json", "sha256": "0" * 64},
            },
        }
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaisesRegex(verify.VerificationError, "raw-artifact manifest is missing or has a hash mismatch"):
                verify.validate_e0_evidence(evidence, root / "E0", current_head=source)

    def test_ci0_cases_cover_automatic_exact_head_workflows(self) -> None:
        contract = verify.load_contract()
        ci0 = next(item for item in contract["checkpoints"] if item["id"] == "CI0")
        self.assertEqual(CI0_CASE_IDS, tuple(case["id"] for case in ci0["caseDefinitions"]))

    def test_pub0_cases_keep_the_pr_open_as_a_draft(self) -> None:
        contract = verify.load_contract()
        pub0 = next(item for item in contract["checkpoints"] if item["id"] == "PUB0")
        self.assertEqual(PUB0_CASE_IDS, tuple(case["id"] for case in pub0["caseDefinitions"]))
        self.assertIn("PUB0.draft-pr-open-unmerged", PUB0_CASE_IDS)

    def test_strict_case_cannot_declare_normalization(self) -> None:
        changed = json.loads(verify.CONTRACT_PATH.read_text(encoding="utf-8"))
        changed["checkpoints"][0]["caseDefinitions"][0]["comparisonRules"]["normalizations"] = ["drop response id"]
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "contract.json"
            path.write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaisesRegex(verify.VerificationError, "normalization under strict"):
                verify.load_contract(path, verify_lock=False)


if __name__ == "__main__":
    unittest.main()
