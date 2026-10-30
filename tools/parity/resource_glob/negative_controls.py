#!/usr/bin/env python3
"""Executable rejection checks for the frozen glob differential verifier."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import differential

ROOT = differential.ROOT
VERIFIER = Path(__file__).with_name('differential.py')


class DifferentialNegativeControls(unittest.TestCase):
    def run_verifier(self, pi, case_index=None):
        command = [sys.executable, str(VERIFIER), '--pi', str(pi)]
        if case_index:
            command.extend(['--case-index', str(case_index)])
        return subprocess.run(command, text=True, capture_output=True)

    def test_rejects_wrong_pi_sha(self):
        result = self.run_verifier(ROOT)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('SHA mismatch', result.stderr)

    def test_rejects_changed_frozen_input(self):
        cases = json.loads(differential.CASE_INDEX.read_text())
        cases[0]['path'] = 'tampered/file.ts'
        with tempfile.TemporaryDirectory() as directory:
            changed = Path(directory) / 'cases.json'
            changed.write_text(json.dumps(cases))
            result = self.run_verifier('/tmp/pisharp-pi-resource-glob', changed)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('case index SHA-256 mismatch', result.stderr)

    def test_rejects_changed_frozen_expectation(self):
        cases = json.loads(differential.CASE_INDEX.read_text())
        cases[0]['expectedPi'] = not cases[0]['expectedPi']
        with tempfile.TemporaryDirectory() as directory:
            changed = Path(directory) / 'cases.json'
            changed.write_text(json.dumps(cases))
            result = self.run_verifier('/tmp/pisharp-pi-resource-glob', changed)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('case index SHA-256 mismatch', result.stderr)

    def test_rejects_duplicate_and_missing_frozen_ids(self):
        for mutation in ('duplicate', 'missing'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                cases = json.loads(differential.CASE_INDEX.read_text())
                if mutation == 'duplicate':
                    cases[-1]['id'] = cases[0]['id']
                else:
                    cases.pop()
                changed = Path(directory) / 'cases.json'
                changed.write_text(json.dumps(cases))
                result = self.run_verifier('/tmp/pisharp-pi-resource-glob', changed)
                self.assertNotEqual(0, result.returncode)
                expected = 'duplicate case IDs' if mutation == 'duplicate' else 'case index SHA-256 mismatch'
                self.assertIn(expected, result.stderr)

    def test_rejects_duplicate_or_missing_pi_sharp_case_execution(self):
        cases = [{'id': 'G1-case', 'expectedPi': True}]
        with self.assertRaisesRegex(ValueError, 'do not match'):
            differential.compare_results(cases, [])
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            differential.compare_results(cases, [{'id': 'G1-case', 'actual': True},
                                                   {'id': 'G1-case', 'actual': True}])

    def test_rejects_incorrect_matcher_result(self):
        report = differential.compare_results([{'id': 'G1-case', 'expectedPi': True}],
                                              [{'id': 'G1-case', 'actual': False}])
        self.assertEqual('failed', report[0]['status'])

    def test_rejects_incorrect_override_precedence(self):
        case = {'id': 'P3-force-include-after-glob-exclusion', 'expectedPi': True,
                'overrides': ['!extensions/**/*.dll', '+extensions/plugin.dll']}
        report = differential.compare_results([case], [{'id': case['id'], 'actual': False}])
        self.assertEqual('failed', report[0]['status'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
