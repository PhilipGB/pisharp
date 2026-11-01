"""Negative control for the ordering calibration flags.

Calibration is diagnostic only, but its flags must never hide a PiSharp-only ordering. These cases
are pure functions of the observed orders, so they never need a Pi checkout or a product run.
"""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from calibrate_order import classify  # noqa: E402


class CalibrationFlagTests(unittest.TestCase):
    def test_pi_variation_does_not_mask_a_pisharp_only_order(self):
        # Pi produced two orders; PiSharp produced those and a third one Pi never produced.
        flags = classify(['A', 'B'], ['A', 'B', 'C'])

        self.assertIn('pi-varies', flags)
        self.assertIn('pisharp-outside-pi-observed', flags)
        self.assertNotIn('gap', flags)

    def test_pi_only_variation_is_not_a_gap(self):
        flags = classify(['A', 'B'], ['A'])

        self.assertEqual(['pi-varies'], flags)

    def test_pisharp_varying_beyond_a_stable_pi_is_reported(self):
        flags = classify(['A'], ['A', 'B'])

        self.assertIn('pisharp-varies', flags)
        self.assertIn('pisharp-outside-pi-observed', flags)
        self.assertNotIn('gap', flags)

    def test_stable_disagreement_is_a_gap(self):
        self.assertEqual(['pisharp-outside-pi-observed', 'gap'], classify(['A'], ['B']))

    def test_identical_orders_report_nothing(self):
        self.assertEqual([], classify(['A'], ['A']))


if __name__ == '__main__':
    unittest.main()
