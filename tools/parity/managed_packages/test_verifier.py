"""Negative controls proving required verifier failures cannot produce success."""

from __future__ import annotations

import json
import contextlib
import copy
import importlib.util
import io
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest import mock

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

    def test_detached_pull_request_checkout_uses_source_branch_name(self) -> None:
        with (mock.patch.object(verify, '_git', return_value=''),
              mock.patch.dict('os.environ', {
                  'GITHUB_HEAD_REF': 'milestone/managed-package-lifecycle-parity',
                  'GITHUB_REF_NAME': '27/merge',
              }, clear=True)):
            self.assertEqual('milestone/managed-package-lifecycle-parity', verify._runtime_branch_name())

    def test_detached_push_checkout_uses_ref_name(self) -> None:
        with (mock.patch.object(verify, '_git', return_value=''),
              mock.patch.dict('os.environ', {
                  'GITHUB_REF_NAME': 'milestone/managed-package-lifecycle-parity',
              }, clear=True)):
            self.assertEqual('milestone/managed-package-lifecycle-parity', verify._runtime_branch_name())

    def test_unknown_detached_checkout_does_not_invent_branch(self) -> None:
        with (mock.patch.object(verify, '_git', return_value=''),
              mock.patch.dict('os.environ', {}, clear=True),
              self.assertRaises(verify.ExecutionUnavailable)):
            verify._runtime_branch_name()

    def test_pr_branch_history_uses_head_only_for_verified_merge_parents(self) -> None:
        merge, base, head = 'a' * 40, 'b' * 40, 'c' * 40
        event = self.root / 'pr-event.json'
        event.write_text(json.dumps({'pull_request': {'head': {'sha': head}, 'base': {'sha': base}}}))
        with (mock.patch.dict('os.environ', {'GITHUB_EVENT_NAME': 'pull_request',
                                             'GITHUB_EVENT_PATH': str(event)}, clear=True),
              mock.patch.object(verify, '_git', side_effect=[merge, f'{merge} {base} {head}'])):
            self.assertEqual(head, verify._branch_history_head())
        with (mock.patch.dict('os.environ', {'GITHUB_EVENT_NAME': 'pull_request',
                                             'GITHUB_EVENT_PATH': str(event)}, clear=True),
              mock.patch.object(verify, '_git', side_effect=[merge, f'{merge} {base} {"d" * 40}']),
              self.assertRaises(verify.MandatoryFailure)):
            verify._branch_history_head()

    def test_non_pr_branch_history_uses_checkout_head(self) -> None:
        with (mock.patch.dict('os.environ', {'GITHUB_EVENT_NAME': 'push'}, clear=True),
              mock.patch.object(verify, '_git', return_value='a' * 40)):
            self.assertEqual('a' * 40, verify._branch_history_head())

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

    def test_same_git_install_accepts_only_the_captured_upstream_clone_race(self) -> None:
        oracle = {
            'allProcessesReaped': True, 'boundedSeconds': True, 'settingsValid': True,
            'sourceConfigured': False, 'sourceOccurrences': 0, 'packageRootPresent': False,
            'installedVersion': None, 'packageManifestValid': False,
        }
        actual = {
            'allProcessesReaped': True, 'boundedSeconds': True, 'settingsValid': True,
            'sourceConfigured': True, 'sourceOccurrences': 1, 'packageRootPresent': True,
            'installedVersion': 'a' * 40, 'expectedVersion': 'a' * 40,
            'packageManifestValid': True,
        }
        case = {
            'equal': False, 'oracle': oracle, 'actual': actual,
            'observations': {
                'pi': {
                    **oracle, 'originHeadCloneRace': True,
                    'commandOutcomeClasses': ['failure', 'failure'],
                },
                'pisharp': {**actual, 'commandOutcomeClasses': ['success', 'success']},
            },
        }
        self.assertTrue(verify._is_same_git_install_safety_difference(case))

        case['observations']['pi']['originHeadCloneRace'] = False
        self.assertFalse(verify._is_same_git_install_safety_difference(case))
        case['observations']['pi']['cloneDestinationCollision'] = True
        self.assertFalse(verify._is_same_git_install_safety_difference(case))
        case['observations']['pi']['cloneCollisionErrors'] = [
            'fatal: destination path /tmp/repo already exists and is not an empty directory\nError: git clone x failed',
            "fatal: cannot copy '/usr/share/git-core/templates/hooks/x': No such file or directory\nError: git clone x failed",
        ]
        self.assertTrue(verify._is_same_git_install_safety_difference(case))
        case['observations']['pisharp']['packageManifestValid'] = False
        self.assertFalse(verify._is_same_git_install_safety_difference(case))

    def test_clone_destination_collision_requires_both_specific_git_errors(self) -> None:
        path = verify.ROOT / 'tools/parity/managed-package-concurrency-process-differential.py'
        spec = importlib.util.spec_from_file_location('concurrency_collision_control', path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        first = {'exitCode': 1, 'stderr': 'fatal: destination path /tmp/repo already exists and is not an empty directory\nError: git clone x failed'}
        second = {'exitCode': 1, 'stderr': "fatal: cannot copy '/usr/share/git-core/templates/hooks/x': No such file or directory\nError: git clone x failed"}
        self.assertTrue(module.clone_destination_collision([first, second]))
        self.assertFalse(module.clone_destination_collision([first, {'exitCode': 1, 'stderr': 'network error'}]))
        self.assertFalse(module.clone_destination_collision([first, {**second, 'exitCode': 0}]))

    def test_same_git_install_safety_difference_requires_valid_pisharp_package(self) -> None:
        case = {
            'equal': False, 'oracle': {'packageRootPresent': False}, 'actual': {'packageRootPresent': True},
            'observations': {
                'pi': {
                    'originHeadCloneRace': True, 'commandOutcomeClasses': ['failure', 'failure'],
                    'settingsValid': True, 'sourceConfigured': False, 'sourceOccurrences': 0,
                    'packageRootPresent': False, 'installedVersion': None,
                    'packageManifestValid': False,
                },
                'pisharp': {
                    'allProcessesReaped': True, 'boundedSeconds': True, 'settingsValid': True,
                    'sourceConfigured': True, 'sourceOccurrences': 1, 'packageRootPresent': True,
                    'installedVersion': None, 'expectedVersion': 'a' * 40,
                    'packageManifestValid': True, 'commandOutcomeClasses': ['success', 'success'],
                },
            },
        }
        self.assertFalse(verify._is_same_git_install_safety_difference(case))

    def test_same_npm_install_accepts_only_captured_enoent_race_with_safe_result(self) -> None:
        oracle = {
            'commandOutcomeClasses': ['failure', 'success'], 'allProcessesReaped': True,
            'boundedSeconds': True, 'settingsValid': True, 'sourceConfigured': True,
            'sourceOccurrences': 1, 'packageRootPresent': True, 'installedVersion': '1.0.0',
            'packageManifestValid': True, 'expectedVersion': '1.0.0', 'updateArtifacts': [],
        }
        actual = {**oracle, 'commandOutcomeClasses': ['success', 'success']}
        case = {
            'equal': False, 'oracle': oracle, 'actual': actual,
            'observations': {
                'pi': {**oracle, 'npmTargetInstallRace': True},
                'pisharp': actual,
            },
        }
        self.assertTrue(verify._is_same_npm_install_safety_difference(case))
        case['observations']['pi']['npmTargetInstallRace'] = False
        self.assertFalse(verify._is_same_npm_install_safety_difference(case))

    def test_same_npm_install_safety_difference_requires_two_successful_pisharp_processes(self) -> None:
        case = {
            'equal': False, 'oracle': {'installedVersion': None}, 'actual': {'installedVersion': '1.0.0'},
            'observations': {
                'pi': {
                    'npmTargetInstallRace': True, 'commandOutcomeClasses': ['failure', 'failure'],
                    'allProcessesReaped': True, 'boundedSeconds': True, 'settingsValid': True,
                    'sourceOccurrences': 0, 'updateArtifacts': [],
                },
                'pisharp': {
                    'allProcessesReaped': True, 'boundedSeconds': True, 'settingsValid': True,
                    'sourceConfigured': True, 'sourceOccurrences': 1, 'packageRootPresent': True,
                    'installedVersion': '1.0.0', 'expectedVersion': '1.0.0',
                    'packageManifestValid': True, 'updateArtifacts': [],
                    'commandOutcomeClasses': ['failure', 'success'],
                },
            },
        }
        self.assertFalse(verify._is_same_npm_install_safety_difference(case))

    def test_package_manager_cancellation_accepts_only_known_exit_race_with_safe_cleanup(self) -> None:
        oracle = {
            'exitCode': 130, 'argv': ['install', 'pkg@1.0.0', '--prefix', '<install-root>'],
            'sourceConfigured': False, 'packageManagerConfig': ['npm'], 'npmCommandPreserved': True,
            'settingsUnchanged': True, 'packageDirectoryExists': True,
            'managerStarted': True, 'cancelled': True,
        }
        actual = {**oracle, 'exitCode': 1, 'packageDirectoryExists': False}
        self.assertTrue(verify._package_manager_cancellation_safety_difference(oracle, actual))

        actual['exitCode'] = 130
        self.assertTrue(verify._package_manager_cancellation_safety_difference(oracle, actual))
        for field, value in (
            ('exitCode', 2), ('packageDirectoryExists', True), ('sourceConfigured', True),
            ('settingsUnchanged', False), ('npmCommandPreserved', False),
            ('managerStarted', False), ('cancelled', False), ('packageManagerConfig', ['pnpm']),
        ):
            corrupted = {**actual, field: value}
            self.assertFalse(verify._package_manager_cancellation_safety_difference(oracle, corrupted), field)
        self.assertFalse(verify._package_manager_cancellation_safety_difference(
            {**oracle, 'exitCode': 1}, actual))

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

    def test_committed_case_digest_normalizes_fixture_roots_and_loopback_ports(self) -> None:
        first = {'source': 'npm:x@file:/repo/artifacts/run-one/profile/tmp/package-a/a.tgz',
                 'git': 'git://127.0.0.1:12345/fixture/repo', 'gitDaemonPort': 12345}
        second = {'source': 'npm:x@file:/repo/artifacts/run-two/profile/tmp/package-b/a.tgz',
                  'git': 'git://127.0.0.1:54321/fixture/repo', 'gitDaemonPort': 54321}
        one = evidence_artifacts._case_side_digest('git.install-url-shorthands', 'actual', first,
                                                   repository='/repo', evidence_root='/repo/artifacts/run-one')
        two = evidence_artifacts._case_side_digest('git.install-url-shorthands', 'actual', second,
                                                   repository='/repo', evidence_root='/repo/artifacts/run-two')
        self.assertEqual(one, two)
        second['source'] = second['source'].replace('a.tgz', 'different.tgz')
        self.assertNotEqual(one, evidence_artifacts._case_side_digest(
            'git.install-url-shorthands', 'actual', second, repository='/repo',
            evidence_root='/repo/artifacts/run-two'))

    def test_committed_fixture_git_normalization_preserves_equality_relationships(self) -> None:
        def digest(state):
            return evidence_artifacts._case_side_digest(
                'git.update-moving', 'actual', state, repository='/repo', evidence_root='/evidence')
        self.assertEqual(digest({'head': 'a' * 40, 'expectedHead': 'a' * 40}),
                         digest({'head': 'b' * 40, 'expectedHead': 'b' * 40}))
        self.assertNotEqual(digest({'head': 'a' * 40, 'expectedHead': 'a' * 40}),
                            digest({'head': 'a' * 40, 'expectedHead': 'b' * 40}))

    def test_calibration_normalization_preserves_mismatched_digests(self) -> None:
        def digest(first, second):
            return evidence_artifacts._case_side_digest(
                'd0.same-pi-package-cli', 'actual',
                {'state': {'baselineNormalizedOracleSha256': first, 'repeatNormalizedOracleSha256': second}},
                repository='/repo', evidence_root='/evidence')
        self.assertEqual(digest('a' * 64, 'a' * 64), digest('b' * 64, 'b' * 64))
        self.assertNotEqual(digest('a' * 64, 'a' * 64), digest('a' * 64, 'b' * 64))

    def test_committed_concurrency_receipt_accepts_only_bounded_outcome_variation(self) -> None:
        historical = {'cases': [
            {'caseId': 'concurrent.same-git-install', 'executed': True, 'passed': True,
             'classification': 'strict', 'oracleSha256': 'a', 'actualSha256': 'valid'},
            {'caseId': 'other', 'executed': True, 'passed': True,
             'classification': 'strict', 'oracleSha256': 'x', 'actualSha256': 'y'},
        ], 'classificationCounts': {'strict': 2, 'equivalent': 0, 'intentional-safety-difference': 0}}
        current = copy.deepcopy(historical)
        current['cases'][0].update(classification='intentional-safety-difference', oracleSha256='b')
        current['classificationCounts'].update(strict=1, **{'intentional-safety-difference': 1})
        self.assertTrue(verify._portable_concurrency_fixture_matches('case-results.json', historical, current))
        tampered = copy.deepcopy(current)
        tampered['cases'][0]['actualSha256'] = 'unsafe'
        self.assertFalse(verify._portable_concurrency_fixture_matches('case-results.json', historical, tampered))
        tampered = copy.deepcopy(current)
        tampered['cases'][1]['oracleSha256'] = 'unrelated-change'
        self.assertFalse(verify._portable_concurrency_fixture_matches('case-results.json', historical, tampered))
        tampered = copy.deepcopy(historical)
        tampered['cases'][0]['oracleSha256'] = 'unrelated-change'
        self.assertFalse(verify._portable_concurrency_fixture_matches('case-results.json', historical, tampered))
        tampered = copy.deepcopy(historical)
        tampered['classificationCounts']['strict'] = 1
        self.assertFalse(verify._portable_concurrency_fixture_matches('case-results.json', tampered, current))

        inventory = {'capabilities': [{'capabilityId': 'concurrency',
                                       'requiredCaseIds': ['concurrent.same-git-install'],
                                       'classifications': {'strict': 1, 'equivalent': 0,
                                                           'intentional-safety-difference': 0}}]}
        current_inventory = copy.deepcopy(inventory)
        current_inventory['capabilities'][0]['classifications'].update(
            strict=0, **{'intentional-safety-difference': 1})
        self.assertTrue(verify._portable_concurrency_fixture_matches(
            'inventory.json', inventory, current_inventory, historical))
        inventory['capabilities'][0]['classifications']['strict'] = 2
        self.assertFalse(verify._portable_concurrency_fixture_matches(
            'inventory.json', inventory, current_inventory, historical))

    def test_committed_cancellation_digest_normalizes_only_the_bounded_exit_race(self) -> None:
        first = {'exitCode': 0, 'state': {'exitCode': 1, 'cancelled': True,
                                          'packageDirectoryExists': False}}
        second = {'exitCode': 0, 'state': {'exitCode': 130, 'cancelled': True,
                                           'packageDirectoryExists': False}}
        first_digest = evidence_artifacts._case_side_digest(
            'pm.cancellation', 'actual', first, repository='/repo', evidence_root='/evidence')
        second_digest = evidence_artifacts._case_side_digest(
            'pm.cancellation', 'actual', second, repository='/repo', evidence_root='/evidence')
        self.assertEqual(first_digest, second_digest)

        unsafe = {'exitCode': 0, 'state': {'exitCode': 2, 'cancelled': True,
                                           'packageDirectoryExists': False}}
        self.assertNotEqual(first_digest, evidence_artifacts._case_side_digest(
            'pm.cancellation', 'actual', unsafe, repository='/repo', evidence_root='/evidence'))
        self.assertNotEqual(first_digest, evidence_artifacts._case_side_digest(
            'pm.failure', 'actual', second, repository='/repo', evidence_root='/evidence'))

    def test_committed_case_coverage_is_exactly_the_contract_inventory(self) -> None:
        contract = verify.load_contract()
        coverage = evidence_artifacts.build_case_coverage(contract)
        covered = [case_id for checkpoint in coverage['checkpoints']
                   for case_id in checkpoint['requiredCaseIds']]
        required = [case_id for checkpoint in contract['checkpoints']
                    for case_id in checkpoint['requiredCaseIds']]
        self.assertEqual(required, covered)
        self.assertEqual(len(required), coverage['requiredCaseCount'])

    def test_exact_head_run_selection_ignores_stale_heads_and_other_events(self) -> None:
        runs = [
            {'event': 'push', 'head_branch': 'milestone/managed-package-lifecycle-parity',
             'head_sha': 'a' * 40, 'name': 'Linux CI', 'run_number': 10, 'id': 10},
            {'event': 'pull_request', 'head_branch': 'milestone/managed-package-lifecycle-parity',
             'head_sha': 'b' * 40, 'name': 'Linux CI', 'run_number': 11, 'id': 11},
            {'event': 'push', 'head_branch': 'milestone/managed-package-lifecycle-parity',
             'head_sha': 'b' * 40, 'name': 'Linux CI', 'run_number': 12, 'id': 12},
        ]
        selected = verify._latest_exact_workflow_run(
            runs, event='push', branch='milestone/managed-package-lifecycle-parity', head_sha='b' * 40)
        self.assertEqual(12, selected['id'])

    def test_exact_head_workflow_gate_rejects_missing_or_skipped_jobs(self) -> None:
        workflow = (verify.ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        corpus_step = verify._workflow_step_block(workflow, 'Run managed-package parity corpus')
        self.assertIn(
            'PISHARP_TEST_OWNERSHIP_ORACLE: ${{ github.workspace }}/artifacts/parity/builtin-ownership/pinned.json',
            corpus_step)
        without_oracle = corpus_step.replace(
            '          PISHARP_TEST_OWNERSHIP_ORACLE: ${{ github.workspace }}/artifacts/parity/builtin-ownership/pinned.json\n',
            '')
        self.assertNotIn('PISHARP_TEST_OWNERSHIP_ORACLE:', without_oracle)
        without_oracle_workflow = workflow.replace(corpus_step, without_oracle, 1)
        self.assertFalse(verify._workflow_has_required_steps(
            without_oracle_workflow, verify.load_contract()['upstream']['revision']))
        self.assertTrue(verify._workflow_has_required_steps(
            workflow, verify.load_contract()['upstream']['revision']))
        required = verify._required_workflow_jobs(workflow)
        self.assertEqual({'macos-session-locks', 'macos-clipboard-contracts', 'windows-clipboard-contracts',
                          'managed-package-verifier', 'mcp-conformance'}, set(required))
        package_steps = [
            {'name': name, 'conclusion': 'success'} for name in (
                'Verify managed-package evidence controls', 'Hydrate pinned Pi model data',
                'Build pinned Pi offline', 'Run managed-package parity corpus',
                'Record managed-package CI integration', 'Independently check managed-package evidence',
                'Run dotnet test PiSharp.slnx --no-build --no-restore',
            )
        ]
        jobs = [{'name': name, 'status': 'completed', 'conclusion': 'success', 'steps': []}
                for name in required]
        jobs[required.index('managed-package-verifier')]['steps'] = package_steps
        jobs[required.index('mcp-conformance')]['steps'] = [
            {'name': 'Run node tools/parity/mcp-conformance/run.mjs', 'conclusion': 'success'}]
        state = verify._workflow_gate_state(jobs, required)
        self.assertTrue(state['requiredJobsPresent'])
        self.assertTrue(state['allJobsCompletedSuccessfully'])
        self.assertTrue(state['packageGatePassed'])
        self.assertTrue(state['pinnedPiPrepared'])
        self.assertTrue(state['fullDotnetSuitePassed'])
        self.assertTrue(state['mcpConformancePassed'])

        jobs[-1]['conclusion'] = 'skipped'
        state = verify._workflow_gate_state(jobs, required)
        self.assertFalse(state['allJobsCompletedSuccessfully'])

    def test_remote_pr_artifact_must_bind_to_current_head_and_base(self) -> None:
        evidence = fixture_support.make_valid_evidence(self.root / 'remote')
        source_sha = 'e' * 40
        head_sha = 'b' * 40
        base_sha = 'c' * 40
        evidence['provenance'].update({
            'repository': 'https://github.com/PhilipGB/pisharp',
            'piSha': verify.load_contract()['upstream']['revision'],
            'pisharpSourceSha': source_sha,
            'checkedOutSourceSha': source_sha,
            'fixtureVersion': verify.FIXTURE_VERSION,
            'testControl': False,
            'binaryIdentity': {'scope': 'repository', 'path': 'src/PiSharp.Cli/PiSharp.Cli.csproj',
                               'sha256': 'f' * 64},
        })
        case = next(item for item in evidence['cases'] if item['caseId'] == 'ci.pr-head-base')
        case['actual'] = {'exitCode': 0, 'state': {
            'headSha': head_sha, 'baseSha': base_sha, 'baseRef': 'main', 'mergeSha': source_sha,
            'mergeContainsHead': True, 'mergeContainsBase': True,
        }}
        run = {'event': 'pull_request', 'head_branch': verify._runtime_state()['branch'],
               'head_sha': head_sha}
        pull_request = {'head': {'sha': head_sha}, 'base': {'sha': base_sha}}
        verify._validate_remote_ci_provenance(evidence, run=run, pull_request=pull_request)

        case['actual']['state']['mergeContainsBase'] = False
        with self.assertRaises(verify.MandatoryFailure):
            verify._validate_remote_ci_provenance(evidence, run=run, pull_request=pull_request)

    def test_publication_checker_records_live_pr_and_exact_head_cases(self) -> None:
        output = self.root / 'publication'
        report = fixture_support.make_valid_evidence(output)
        fixture_support.write_evidence(output, report)
        head_sha = 'b' * 40
        base_sha = 'c' * 40
        branch = 'milestone/managed-package-lifecycle-parity'
        pr = {
            'number': 124, 'state': 'open', 'draft': True, 'merged': False, 'merged_at': None,
            'html_url': 'https://github.com/PhilipGB/pisharp/pull/124',
            'head': {'repo': {'full_name': 'PhilipGB/pisharp'}, 'ref': branch, 'sha': head_sha},
            'base': {'repo': {'full_name': 'PhilipGB/pisharp'}, 'ref': 'main', 'sha': base_sha},
        }
        runs = [
            {'id': 101, 'name': 'Linux CI', 'event': 'push', 'head_branch': branch,
             'head_sha': head_sha, 'run_number': 1, 'status': 'completed', 'conclusion': 'success',
             'html_url': 'https://github.com/PhilipGB/pisharp/actions/runs/101'},
            {'id': 102, 'name': 'Linux CI', 'event': 'pull_request', 'head_branch': branch,
             'head_sha': head_sha, 'run_number': 2, 'status': 'completed', 'conclusion': 'success',
             'html_url': 'https://github.com/PhilipGB/pisharp/actions/runs/102'},
        ]
        workflow = (verify.ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        required = verify._required_workflow_jobs(workflow)
        jobs = [{'id': name, 'name': name, 'status': 'completed', 'conclusion': 'success', 'steps': []}
                for name in required]
        package_job = next(item for item in jobs if item['name'] == 'managed-package-verifier')
        package_job['steps'] = [{'name': name, 'conclusion': 'success'} for name in (
            'Verify managed-package evidence controls', 'Hydrate pinned Pi model data',
            'Build pinned Pi offline', 'Run managed-package parity corpus',
            'Record managed-package CI integration', 'Independently check managed-package evidence',
            'Run dotnet test PiSharp.slnx --no-build --no-restore',
        )]
        mcp_job = next(item for item in jobs if item['name'] == 'mcp-conformance')
        mcp_job['steps'] = [{'name': 'Run node tools/parity/mcp-conformance/run.mjs', 'conclusion': 'success'}]

        responses = {
            '': {'full_name': 'PhilipGB/pisharp'},
            'pulls/124': pr,
            f'branches/{branch.replace("/", "%2F")}': {'commit': {'sha': head_sha}},
            'branches/main': {'commit': {'sha': base_sha}},
            f'actions/runs?branch={branch.replace("/", "%2F")}&per_page=100': {'workflow_runs': runs},
            'actions/runs/101/jobs?per_page=100': {'jobs': jobs},
            'actions/runs/102/jobs?per_page=100': {'jobs': jobs},
            'actions/runs/101/artifacts?per_page=100': {'artifacts': [
                {'name': 'managed-package-parity', 'expired': False}]},
            'actions/runs/102/artifacts?per_page=100': {'artifacts': [
                {'name': 'managed-package-parity', 'expired': False}]},
        }

        def api_response(endpoint: str, *, label: str) -> tuple[dict, dict]:
            self.assertIn(endpoint, responses, label)
            return responses[endpoint], {'argv': ['GET', endpoint], 'exitCode': 0}

        with (mock.patch.object(verify, '_runtime_state', return_value={
                    'branch': branch, 'headSha': head_sha}),
              mock.patch.object(verify, '_run_github_api', side_effect=api_response),
              mock.patch.object(verify, '_download_run_package_evidence',
                                side_effect=lambda run, root: (root / f'download-{run["id"]}',
                                                               {'argv': ['gh', 'run', 'download', str(run['id'])]})),
              mock.patch.object(verify, '_remote_artifact_passes', return_value=(True, {'passed': True})),
              mock.patch.object(verify, 'validate_evidence'),
              mock.patch.object(verify, '_write_milestone_result')):
            result = verify.check_publication(124, output)

        self.assertEqual(0, result)
        completed = verify._load_json(output / 'evidence.json')
        for checkpoint_id in ('draft-publication', 'exact-head-ci'):
            checkpoint = next(item for item in completed['checkpointResults']
                              if item['checkpointId'] == checkpoint_id)
            self.assertEqual('passed', checkpoint['status'])
        required_case_count = sum(len(item['requiredCaseIds']) for item in verify.load_contract()['checkpoints'])
        self.assertEqual(required_case_count, completed['testResults']['discovered'])
        self.assertEqual(completed['testResults']['discovered'], completed['testResults']['executed'])


if __name__ == '__main__':
    unittest.main()
