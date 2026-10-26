"""Negative controls proving required verifier failures cannot produce success."""

from __future__ import annotations

import json
import contextlib
import io
from pathlib import Path
import shutil
import tempfile
import unittest

import fixture_support
import evidence_artifacts
import verify


class ManagedPackageVerifierTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls._temporary = tempfile.TemporaryDirectory(prefix='managed-package-verifier-')
        cls.root = Path(cls._temporary.name)
        cls.base = cls.root / 'base'
        cls.evidence = fixture_support.make_valid_evidence(cls.base)
        fixture_support.write_evidence(cls.base, cls.evidence)

    @classmethod
    def tearDownClass(cls) -> None:
        cls._temporary.cleanup()

    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix='control-', dir=self.__class__.root))
        self.evidence = fixture_support.copied_fixture(self.__class__.base, self.root / 'fixture')
        self.root = self.root / 'fixture'

    def tearDown(self) -> None:
        shutil.rmtree(self.root.parent, ignore_errors=True)

    def _case(self, case_id: str) -> dict:
        for case in self.evidence['cases']:
            if case['caseId'] == case_id:
                return case
        raise ValueError(f'fixture is missing {case_id}')

    def _reject(self) -> None:
        with self.assertRaises(verify.VerificationError):
            verify.validate_evidence(self.evidence, self.root, allow_synthetic=True)

    def test_valid_reference_control_returns_zero(self) -> None:
        result = verify.validate_evidence(self.evidence, self.root, allow_synthetic=True)
        self.assertEqual(0, result)

    def test_checkpoint_dependencies_cannot_be_bypassed(self) -> None:
        npm_install = next(item for item in self.evidence['checkpointResults']
                           if item['checkpointId'] == 'npm-install')
        npm_install.update(status='unexecuted', commandExitCode=1)
        verify._refresh_checkpoint_result(self.evidence, 'resource-distribution')
        distribution = next(item for item in self.evidence['checkpointResults']
                            if item['checkpointId'] == 'resource-distribution')
        self.assertEqual('blocked', distribution['status'])
        self.assertEqual(1, distribution['commandExitCode'])
        with self.assertRaises(verify.MandatoryFailure) as failure:
            verify.validate_evidence(self.evidence, self.root, checkpoint_id='resource-distribution',
                                     allow_synthetic=True)
        self.assertIn('dependency checkpoint npm-install is not passed', str(failure.exception))

    def test_control_01_missing_required_checkpoint_is_rejected(self) -> None:
        self.evidence['checkpointResults'].pop()
        self._reject()

    def test_control_02_missing_required_case_is_rejected(self) -> None:
        self.evidence['cases'].pop()
        self._reject()

    def test_control_03_duplicate_case_id_is_rejected(self) -> None:
        self.evidence['cases'].append(dict(self.evidence['cases'][0]))
        self._reject()

    def test_control_04_false_passed_assertion_is_rejected(self) -> None:
        self._case('npm.install-exact')['assertions']['npm.install-exact'] = False
        self._reject()

    def test_control_05_replaced_oracle_result_is_rejected(self) -> None:
        self._case('npm.install-exact')['oracle']['state']['packageVersion'] = '9.9.9'
        self._reject()

    def test_control_06_changed_pi_sha_is_rejected(self) -> None:
        self.evidence['provenance']['piSha'] = '0' * 40
        self._reject()

    def test_control_07_changed_pisharp_sha_is_rejected(self) -> None:
        self.evidence['provenance']['pisharpSourceSha'] = '0' * 40
        self._reject()

    def test_control_08_unexecuted_pass_is_rejected(self) -> None:
        case = self._case('npm.install-exact')
        case['executed'] = False
        case['passed'] = True
        self._reject()

    def test_control_09_stale_actual_result_is_rejected(self) -> None:
        self._case('npm.install-exact')['actual']['state']['packageVersion'] = '1.2.2'
        self._reject()

    def test_control_10_missing_required_artifact_is_rejected(self) -> None:
        path = self.root / 'cases/npm.install-exact.json'
        path.unlink()
        self.evidence['artifacts'] = [item for item in self.evidence['artifacts']
                                      if item['path'] != 'cases/npm.install-exact.json']
        self._reject()

    def test_control_11_unapproved_exclusion_is_rejected(self) -> None:
        self.evidence['exclusions'].append({
            'caseId': 'npm.install-exact', 'classification': 'unsupported-upstream',
            'reason': 'test control exclusion', 'sourceReference': 'unapproved',
            'artifactPath': 'cases/npm.install-exact.json',
        })
        self._reject()

    def test_control_12_zero_discovered_tests_is_rejected(self) -> None:
        self.evidence['testResults']['discovered'] = 0
        self._reject()

    def test_control_13_skipped_mandatory_test_is_rejected(self) -> None:
        self._case('npm.install-exact')['skipped'] = True
        self._reject()

    def test_control_14_malformed_json_is_rejected(self) -> None:
        malformed = self.root / 'malformed.json'
        malformed.write_text('{not-json', encoding='utf-8')
        with self.assertRaises(verify.VerificationError):
            verify._load_json(malformed)

    def test_control_15_changed_required_package_version_is_rejected(self) -> None:
        self._case('npm.install-exact')['actual']['state']['packageVersion'] = '1.2.4'
        self._reject()

    def test_control_16_changed_required_git_head_is_rejected(self) -> None:
        self.evidence['provenance']['checkedOutSourceSha'] = 'f' * 40
        self._reject()

    def test_control_17_changed_required_package_scope_is_rejected(self) -> None:
        self._case('npm.install-exact')['actual']['state']['scope'] = 'project'
        self._reject()

    def test_control_18_changed_resource_invocation_is_rejected(self) -> None:
        self._case('distribution.skill')['actual']['state']['resourceInvocation']['value'] = 'tampered'
        self._reject()

    def test_missing_checkpoint_uses_mandatory_failure_exit_code(self) -> None:
        self.evidence['checkpointResults'].pop()
        fixture_support.write_evidence(self.root, self.evidence)
        with contextlib.redirect_stderr(io.StringIO()):
            result = verify.main(['check', '--all', '--evidence', str(self.root)])
        self.assertEqual(1, result)

    def test_malformed_evidence_uses_invalid_evidence_exit_code(self) -> None:
        (self.root / 'evidence.json').write_text('{malformed', encoding='utf-8')
        with contextlib.redirect_stderr(io.StringIO()):
            result = verify.main(['check', '--all', '--evidence', str(self.root)])
        self.assertEqual(2, result)

    def test_unknown_checkpoint_uses_invalid_configuration_exit_code(self) -> None:
        fixture_support.write_evidence(self.root, self.evidence)
        with contextlib.redirect_stderr(io.StringIO()):
            result = verify.main(['check', '--checkpoint', 'not-a-checkpoint', '--evidence', str(self.root)])
        self.assertEqual(2, result)

    def test_missing_pinned_pi_uses_execution_unavailable_exit_code(self) -> None:
        with contextlib.redirect_stderr(io.StringIO()):
            result = verify.main([
                'execute', '--all', '--pi', str(self.root / 'missing-pi'),
                '--output', str(self.root / 'unavailable-output'),
            ])
        self.assertEqual(3, result)

    def test_committed_case_digest_normalizes_temporary_paths_and_timestamps(self) -> None:
        first = {'path': '/tmp/run-one/package/cache', 'timestamp': '2026-10-26T10:00:00Z',
                 'result': {'version': '1.2.3'}}
        second = {'path': '/tmp/run-two/package/cache', 'timestamp': '2026-10-26T11:00:00Z',
                  'result': {'version': '1.2.3'}}
        first_digest = evidence_artifacts._case_digest(first, repository='/repo', evidence_root='/evidence')
        second_digest = evidence_artifacts._case_digest(second, repository='/repo', evidence_root='/evidence')
        self.assertEqual(first_digest, second_digest)

    def test_committed_case_coverage_is_exactly_the_contract_inventory(self) -> None:
        contract = verify.load_contract()
        coverage = evidence_artifacts.build_case_coverage(contract)
        covered = [case_id for checkpoint in coverage['checkpoints']
                   for case_id in checkpoint['requiredCaseIds']]
        required = [case_id for checkpoint in contract['checkpoints']
                    for case_id in checkpoint['requiredCaseIds']]
        self.assertEqual(required, covered)
        self.assertEqual(len(required), coverage['requiredCaseCount'])


if __name__ == '__main__':
    unittest.main()
