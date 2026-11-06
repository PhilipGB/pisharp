"""Tests for the ENV-PARSER-001 fixture, private oracle and report verifier."""
from __future__ import annotations

import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "terminal"))

import common
import evaluate
from fixtures import get_fixture
from verify_report import negative_controls, validate


class FixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        common.STATE_ROOT.mkdir(parents=True, exist_ok=True)

    def test_baseline_matches_recorded_checksum_and_manifest(self):
        baseline = common.verify_baseline(get_fixture("ENV-PARSER-001"))
        self.assertEqual(get_fixture("ENV-PARSER-001").id, "ENV-PARSER-001")
        self.assertEqual(3, baseline["baselineObserved"]["total"])
        self.assertEqual(1, baseline["baselineObserved"]["failed"])

    def test_baseline_source_has_the_single_intended_defect(self):
        source = (get_fixture("ENV-PARSER-001").root / get_fixture("ENV-PARSER-001").production_files[0]).read_text()
        self.assertIn("line.Split('=')", source)
        self.assertIn("parts.Length != 2", source)

    def test_private_oracle_rejects_baseline_and_accepts_first_equals_fix(self):
        with tempfile.TemporaryDirectory(dir=common.STATE_ROOT) as temporary:
            root = Path(temporary)
            source = get_fixture("ENV-PARSER-001").root / get_fixture("ENV-PARSER-001").production_files[0]
            buggy = root / "buggy"
            buggy.mkdir()
            shutil.copy2(source, buggy / "EnvFile.cs")
            shutil.copy2(get_fixture("ENV-PARSER-001").private_dir / "EnvFilePrivateTests.cs", buggy / "EnvFilePrivateTests.cs")
            shutil.copy2(get_fixture("ENV-PARSER-001").private_dir / "EnvFilePrivateTests.csproj", buggy / "EnvFilePrivateTests.csproj")
            buggy_result = evaluate._run_project(buggy / "EnvFilePrivateTests.csproj")
            buggy_outcomes = {
                test_id: next((outcome for name, outcome in buggy_result["tests"].items()
                               if f".{test_id}_" in name), "missing")
                for test_id in get_fixture("ENV-PARSER-001").private_ids
            }
            self.assertEqual("failed", buggy_outcomes["H02"])
            self.assertEqual("failed", buggy_outcomes["H03"])
            self.assertEqual(6, sum(1 for value in buggy_outcomes.values() if value == "passed"))

            fixed = root / "fixed"
            fixed.mkdir()
            corrected = source.read_text().replace(
                "var parts = line.Split('=');\n            if (parts.Length != 2)\n            {\n                continue;\n            }\n\n            var key = parts[0].Trim();",
                "var equalsIndex = line.IndexOf('=');\n            if (equalsIndex < 0)\n            {\n                continue;\n            }\n\n            var key = line[..equalsIndex].Trim();")
            corrected = corrected.replace("values[key] = parts[1];", "values[key] = line[(equalsIndex + 1)..];")
            (fixed / "EnvFile.cs").write_text(corrected)
            shutil.copy2(get_fixture("ENV-PARSER-001").private_dir / "EnvFilePrivateTests.cs", fixed / "EnvFilePrivateTests.cs")
            shutil.copy2(get_fixture("ENV-PARSER-001").private_dir / "EnvFilePrivateTests.csproj", fixed / "EnvFilePrivateTests.csproj")
            fixed_result = evaluate._run_project(fixed / "EnvFilePrivateTests.csproj")
            fixed_outcomes = {
                test_id: next((outcome for name, outcome in fixed_result["tests"].items()
                               if f".{test_id}_" in name), "missing")
                for test_id in get_fixture("ENV-PARSER-001").private_ids
            }
            self.assertTrue(all(value == "passed" for value in fixed_outcomes.values()), fixed_outcomes)

    def test_change_integrity_rejects_test_tampering(self):
        with tempfile.TemporaryDirectory(dir=common.STATE_ROOT) as temporary:
            snapshot = Path(temporary)
            shutil.copytree(get_fixture("ENV-PARSER-001").root, snapshot, dirs_exist_ok=True)
            (snapshot / "tests/Fixture.Tests/EnvFileTests.cs").write_text("tampered")
            integrity = evaluate.change_integrity(get_fixture("ENV-PARSER-001"), snapshot)
            self.assertFalse(integrity["onlyPermittedProductionFileChanged"])
            self.assertFalse(integrity["passed"])
            self.assertIn("tests/Fixture.Tests/EnvFileTests.cs", integrity["changedFiles"])


class Fixture002Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        common.STATE_ROOT.mkdir(parents=True, exist_ok=True)

    def test_baseline_002_checksum_and_sequential_validation(self):
        baseline = common.verify_baseline(get_fixture("ENV-PARSER-002"))
        self.assertEqual(1, baseline["initial"]["failed"])
        self.assertEqual(1, baseline["afterDefectA"]["failed"])
        self.assertEqual(0, baseline["afterBothDefects"]["failed"])
        self.assertEqual(1, baseline["afterBothDefects"]["passed"])

    def test_private_oracle_requires_both_002_defects_fixed(self):
        import shutil
        import tempfile
        from fixture_transforms import correct_defect_a, correct_defect_b

        spec = get_fixture("ENV-PARSER-002")
        with tempfile.TemporaryDirectory(dir=common.STATE_ROOT) as temporary:
            root = Path(temporary)
            baseline = root / "baseline"
            shutil.copytree(spec.root, baseline)
            base = evaluate.evaluate_private(spec, baseline)
            self.assertEqual("failed", base["outcomes"]["H02"])
            self.assertEqual("passed", base["outcomes"]["H03"])
            self.assertEqual("passed", base["outcomes"]["H06"])
            self.assertEqual("failed", base["outcomes"]["H07"])

            after_a = root / "after-a"
            shutil.copytree(spec.root, after_a)
            env_file = after_a / "src/Fixture.Core/EnvFile.cs"
            env_file.write_text(correct_defect_a(env_file.read_text()))
            partial = evaluate.evaluate_private(spec, after_a)
            self.assertEqual("passed", partial["outcomes"]["H02"])
            self.assertEqual("passed", partial["outcomes"]["H03"])
            self.assertEqual("passed", partial["outcomes"]["H06"])
            self.assertEqual("failed", partial["outcomes"]["H07"])

            after_both = root / "after-both"
            shutil.copytree(spec.root, after_both)
            env_file = after_both / "src/Fixture.Core/EnvFile.cs"
            env_file.write_text(correct_defect_a(env_file.read_text()))
            interpolator = after_both / "src/Fixture.Core/EnvInterpolator.cs"
            interpolator.write_text(correct_defect_b(interpolator.read_text()))
            complete = evaluate.evaluate_private(spec, after_both)
            self.assertEqual(8, complete["passed"], complete["outcomes"])


class ReportVerifierTests(unittest.TestCase):
    def _report(self):
        candidates = [HERE.parent.parent.parent / "docs/parity/fixtures/objective-agent-parity-2026-11-06/report.json",
                      HERE.parent.parent.parent / "artifacts/parity/objective-agent-parity-20261106/report.json"]
        for candidate in candidates:
            if candidate.is_file():
                return json.loads(candidate.read_text())
        self.skipTest("evaluated report not available yet")

    def test_negative_controls_reject_falsification(self):
        report = self._report()
        controls = negative_controls(report, get_fixture(report["fixture"]["id"]))
        self.assertTrue(all(control["rejected"] for control in controls), controls)

    def test_verifier_accepts_complete_report_only_when_arithmetic_is_exact(self):
        report = self._report()
        errors = validate(report, require_complete=True, spec=get_fixture(report["fixture"]["id"]))
        self.assertEqual([], errors)


if __name__ == "__main__":
    unittest.main()
