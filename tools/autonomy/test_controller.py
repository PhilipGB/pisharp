#!/usr/bin/env python3
"""Run with python3 -m unittest discover -s tools/autonomy -p 'test_*.py'."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from controller import Controller, load_ledger


class ControllerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="pisharp-autonomy-test-")
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.ledger = self.repo / "ledger.json"
        self.log = self.repo / "dispatch.log"
        self.runtime = self.repo / "runtime"
        self.ledger.write_text(json.dumps({"plan_received": True,
            "final_audit": {"complete": True, "evidence": ["fixture final audit"]},
            "required_ids": ["first", "second"], "tasks": [
            {"id": identifier, "title": identifier, "status": "not_started",
             "acceptance": ["fixture accepts task"], "implementation": [], "evidence": []}
            for identifier in ("first", "second")] }))
        subprocess.run(["git", "init", "-q"], cwd=self.repo, check=True)
        subprocess.run(["git", "-c", "user.name=Test", "-c", "user.email=test@example.org",
                        "commit", "--allow-empty", "-qm", "bootstrap"], cwd=self.repo, check=True)

    def controller(self, mode="normal", **kwargs):
        worker = [sys.executable, str(Path(__file__).with_name("fake_worker.py")),
                  str(self.ledger), str(self.log), mode]
        return Controller(self.repo, self.ledger, self.runtime, worker, timeout=10,
                          final_command=[sys.executable, "-c", "exit(0)"], require_pushed=False, **kwargs)

    def test_settled_worker_dispatches_next_and_then_stops(self):
        self.assertEqual(0, self.controller().run())
        self.assertEqual(["first", "second"], self.log.read_text().splitlines())
        state = json.loads((self.runtime / "state.json").read_text())
        self.assertTrue(state["completed"])
        self.assertEqual(["dispatched", "settled", "validated", "dispatched", "settled", "validated"],
                         [item["event"] for item in state["progress"]])
        self.assertEqual(0, self.controller().run())
        self.assertEqual(2, len(self.log.read_text().splitlines()))

    def test_restart_resumes_durable_ledger_without_repeating_first(self):
        self.assertEqual(5, self.controller(max_dispatches=1).run())
        self.assertEqual(["first"], self.log.read_text().splitlines())
        self.assertEqual(0, self.controller().run())
        self.assertEqual(["first", "second"], self.log.read_text().splitlines())

    def test_crashed_worker_is_restarted_and_recovers_task(self):
        self.assertEqual(0, self.controller("crash-once").run())
        self.assertEqual(["first", "first", "second"], self.log.read_text().splitlines())
        state = json.loads((self.runtime / "state.json").read_text())
        self.assertIn("failure", [item["event"] for item in state["progress"]])

    def test_unchanged_settled_task_is_not_prompted_indefinitely(self):
        self.assertEqual(4, self.controller("never-update").run())
        self.assertEqual(["first", "second"], self.log.read_text().splitlines())
        self.assertFalse(json.loads((self.runtime / "state.json").read_text())["completed"])

    def test_lock_prevents_second_worker(self):
        first = self.controller()
        try:
            with self.assertRaisesRegex(RuntimeError, "lease"):
                self.controller()
        finally:
            first.close()

    def test_missing_original_plan_blocks_final_completion(self):
        ledger = json.loads(self.ledger.read_text())
        ledger["plan_received"] = False
        self.ledger.write_text(json.dumps(ledger))
        self.assertEqual(4, self.controller().run())
        state = json.loads((self.runtime / "state.json").read_text())
        self.assertFalse(state["completed"])
        self.assertIn("Original plan", state["stop_reason"])

    def test_removing_requirements_or_excluding_another_feature_fails_closed(self):
        ledger = json.loads(self.ledger.read_text())
        ledger["tasks"].pop()
        self.ledger.write_text(json.dumps(ledger))
        with self.assertRaisesRegex(ValueError, "removed"):
            load_ledger(self.ledger)
        ledger["required_ids"] = ["first"]
        ledger["tasks"][0]["status"] = "excluded"
        self.ledger.write_text(json.dumps(ledger))
        with self.assertRaisesRegex(ValueError, "exclusion"):
            load_ledger(self.ledger)

    def test_invalid_claim_cannot_complete(self):
        ledger = json.loads(self.ledger.read_text())
        ledger["tasks"][0]["status"] = "verified"
        self.ledger.write_text(json.dumps(ledger))
        with self.assertRaisesRegex(ValueError, "lacks"):
            load_ledger(self.ledger)


if __name__ == "__main__":
    unittest.main()
