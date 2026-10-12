from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import pi_reference


class PiReferenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='pisharp-reference-test-')
        self.root = Path(self.temporary.name)
        self.checkout = self.root / 'checkout'
        self.checkout.mkdir()
        self.git('init', '-q')
        (self.checkout / 'source.txt').write_text('baseline\n')
        self.git('add', 'source.txt')
        self.git('-c', 'user.name=Reference Fixture', '-c', 'user.email=fixture@example.invalid',
                 '-c', 'commit.gpgsign=false', 'commit', '-qm', 'fixture baseline')
        self.sha = self.git('rev-parse', 'HEAD').strip()
        self.reference = self.root / 'reference.txt'
        self.reference.write_text(self.sha + '\n')
        self.pin = patch.object(pi_reference, 'PIN_PATH', self.reference)
        self.pin.start()

    def tearDown(self):
        self.pin.stop()
        self.temporary.cleanup()

    def git(self, *arguments):
        return subprocess.check_output(['git', '-C', str(self.checkout), *arguments],
                                       text=True, stderr=subprocess.STDOUT)

    def test_matching_commit_allows_untracked_oracle_fixtures(self):
        (self.checkout / 'oracle.test.ts').write_text('temporary oracle\n')
        self.assertEqual(self.sha, pi_reference.verify_pi_checkout(self.checkout))

    def test_wrong_commit_is_rejected_with_both_source_pins(self):
        expected = '0' * 40
        self.reference.write_text(expected + '\n')
        with self.assertRaises(pi_reference.PiReferenceError) as caught:
            pi_reference.verify_pi_checkout(self.checkout)
        self.assertIn(expected, str(caught.exception))
        self.assertIn(self.sha, str(caught.exception))
        self.assertIn('SHA mismatch', str(caught.exception))

    def test_uncommitted_source_change_is_rejected(self):
        (self.checkout / 'source.txt').write_text('changed\n')
        with self.assertRaisesRegex(pi_reference.PiReferenceError, 'tracked changes'):
            pi_reference.verify_pi_checkout(self.checkout)

    def test_staged_source_change_is_rejected(self):
        (self.checkout / 'source.txt').write_text('changed\n')
        self.git('add', 'source.txt')
        with self.assertRaisesRegex(pi_reference.PiReferenceError, 'tracked changes'):
            pi_reference.verify_pi_checkout(self.checkout)

    def test_subdirectory_cannot_masquerade_as_checkout_root(self):
        child = self.checkout / 'child'
        child.mkdir()
        with self.assertRaisesRegex(pi_reference.PiReferenceError, 'repository root'):
            pi_reference.verify_pi_checkout(child)

    def test_malformed_reference_is_rejected_before_comparison(self):
        self.reference.write_text('main\n')
        with self.assertRaisesRegex(pi_reference.PiReferenceError, 'full lowercase commit SHA'):
            pi_reference.verify_pi_checkout(self.checkout)

    def test_acquisition_refuses_existing_directory_and_preserves_contents(self):
        with self.assertRaisesRegex(pi_reference.PiReferenceError, 'Destination already exists'):
            pi_reference.obtain_pi_checkout(self.checkout)
        self.assertEqual('baseline\n', (self.checkout / 'source.txt').read_text())

    def test_standalone_node_probes_reject_wrong_oracle_before_loading_pi(self):
        parity = Path(pi_reference.__file__).parent
        # Child processes read the actual configured pin, not this test's patched path.
        for name in ['codemode-bash-probe.mjs', 'codemode-model-probe.mjs', 'tool-batch-probe.mjs']:
            with self.subTest(probe=name):
                result = subprocess.run(['node', str(parity / name), str(self.checkout), 'false'],
                                        text=True, capture_output=True, timeout=10)
                self.assertNotEqual(0, result.returncode)
                self.assertIn('Pi checkout SHA mismatch', result.stderr)
                self.assertEqual('', result.stdout)


if __name__ == '__main__':
    unittest.main()
