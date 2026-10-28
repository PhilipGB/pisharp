"""Regression coverage for final evidence gate helpers."""
from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest

import aggregate_evidence
import gate_checkpoints
import verify


class GateCheckpointHelperTests(unittest.TestCase):
    def test_vitest_report_keeps_passed_and_skipped_case_identities(self) -> None:
        report = {
            "numPassedTests": 1,
            "numFailedTests": 0,
            "numPendingTests": 1,
            "numTodoTests": 0,
            "testResults": [{
                "name": "/tmp/pi/packages/coding-agent/test/compaction.test.ts",
                "assertionResults": [
                    {"fullName": "compaction > offline case", "status": "passed"},
                    {"fullName": "compaction > provider case", "status": "skipped"},
                ],
            }],
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "vitest.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            counts, skipped, names = gate_checkpoints._vitest_json_report(path)

        self.assertEqual({"passed": 1, "failed": 0, "skipped": 1}, counts)
        self.assertEqual(("packages/coding-agent/test/compaction.test.ts :: compaction > provider case",), skipped)
        self.assertEqual(2, len(names))
        self.assertNotIn(skipped[0], gate_checkpoints.KNOWN_PI_SKIP_IDS)

    def test_checkpoint_result_path_supports_run_local_and_root_relative_forms(self) -> None:
        root = Path("/tmp/evidence")
        run = root / "runs" / "P0"
        self.assertEqual(run / "case-results.json", gate_checkpoints._case_results_path(
            root, run, {"checkpointId": "P0", "caseResultsPath": "case-results.json"}))
        self.assertEqual(root / "runs/C1/differential/c1-case-results.json",
                         gate_checkpoints._case_results_path(
                             root, root / "runs/C1", {"checkpointId": "C1",
                             "caseResultsPath": "runs/C1/differential/c1-case-results.json"}))

    def test_checkpoint_result_path_rejects_escape(self) -> None:
        root = Path("/tmp/evidence")
        with self.assertRaises(verify.VerificationError):
            gate_checkpoints._case_results_path(
                root, root / "runs/P0", {"checkpointId": "P0", "caseResultsPath": "../outside.json"})

    def test_aggregate_requires_exactly_the_final_source_checkpoints(self) -> None:
        self.assertEqual(18, len(aggregate_evidence.FINAL_SOURCE_CHECKPOINTS))
        self.assertIn("PUB0", aggregate_evidence.FINAL_SOURCE_CHECKPOINTS)
        self.assertNotIn("P0", aggregate_evidence.FINAL_SOURCE_CHECKPOINTS)

    def test_required_ci_jobs_are_present_and_successful(self) -> None:
        passing = [{"name": name, "conclusion": "success"}
                   for name in gate_checkpoints.REQUIRED_CI_JOBS]
        self.assertTrue(gate_checkpoints._all_required_ci_jobs_pass(passing))
        self.assertFalse(gate_checkpoints._all_required_ci_jobs_pass(passing[:-1]))
        self.assertFalse(gate_checkpoints._all_required_ci_jobs_pass(
            [*passing[:-1], {"name": passing[-1]["name"], "conclusion": "failure"}]))


if __name__ == "__main__":
    unittest.main()
