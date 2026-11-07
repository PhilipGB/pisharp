#!/usr/bin/env python3
"""Focused tests for the streaming/cancellation evaluator and report verifier."""
import copy
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import evaluate  # noqa: E402
import verify_report  # noqa: E402
import run as terminal_run  # noqa: E402

ROOT = Path(__file__).resolve().parents[3]
MANIFEST = Path(__file__).with_name('scenarios.json')
EVIDENCE = ROOT / 'docs' / 'parity' / 'fixtures' / 'interactive-streaming-cancellation-001'


def cells(text):
    return [dict(chars='<empty-cell>' if character == ' ' else character, width=1)
            for character in text]


def line(text):
    return dict(cells=cells(text))


def buffer_state(text):
    return dict(viewport=[line(text)], buffers=dict(normal=dict(lines=[line(text)])),
                columns=len(text), rows=1, activeScreen='normal', cursor=dict(visible=True),
                modes=dict(private={}), defaultColors={}, exitCode=0)


def evidence_with_frames(*frames):
    return dict(frames=[dict(id=frame_id, state=state) for frame_id, state in frames],
                http=[], scenarioError=None, processByFrame=[], provenance={})


class DynamicMaskTests(unittest.TestCase):
    def test_different_width_durations_align(self):
        width = 40
        left = line(('Took 0.6s').ljust(width))
        right = line(('Took 0.98s').ljust(width))
        evaluate.mask_dynamic([left])
        evaluate.mask_dynamic([right])
        self.assertEqual(left, right)

    def test_usage_percentage_aligns_but_token_count_stays(self):
        width = 40
        left = line(('10 tokens 0.2%/8.2k').ljust(width))
        right = line(('10 tokens 10.25%/8.2k').ljust(width))
        evaluate.mask_dynamic([left])
        evaluate.mask_dynamic([right])
        self.assertEqual(left, right)
        text = evaluate.state_text(dict(viewport=[left], buffers={}))
        self.assertIn('10 tokens', text)
        self.assertIn('8.2k', text)

    def test_non_dynamic_text_is_unchanged(self):
        original = line('The final paragraph remains stable.')
        masked = copy.deepcopy(original)
        evaluate.mask_dynamic([masked])
        self.assertEqual(original, masked)


class ContentCheckTests(unittest.TestCase):
    def test_exact_once_uses_final_transcript_not_every_frame(self):
        evidence = evidence_with_frames(
            ('s01-01', buffer_state('TOKEN TOKEN')),
            ('exit', buffer_state('TOKEN')),
        )
        scenario = dict(idleCheckpoint='exit', requiredText=['TOKEN'],
                        finalTextExactOnce=['TOKEN'])
        result = evaluate.content_check(evidence, scenario)
        self.assertEqual(result['missing'], [])
        self.assertEqual(result['duplicates'], {})

    def test_duplicate_in_final_transcript_is_reported(self):
        evidence = evidence_with_frames(('exit', buffer_state('TOKEN TOKEN')))
        scenario = dict(idleCheckpoint='exit', requiredText=['TOKEN'],
                        finalTextExactOnce=['TOKEN'])
        result = evaluate.content_check(evidence, scenario)
        self.assertEqual(result['duplicates'], {'TOKEN': 2})

    def test_missing_required_text_is_reported(self):
        evidence = evidence_with_frames(('exit', buffer_state('something else')))
        scenario = dict(idleCheckpoint='exit', requiredText=['TOKEN'],
                        finalTextExactOnce=[])
        result = evaluate.content_check(evidence, scenario)
        self.assertEqual(result['missing'], ['TOKEN'])


class FindingPredicateTests(unittest.TestCase):
    def setUp(self):
        self.manifest = json.loads(MANIFEST.read_text())

    def test_unknown_difference_has_no_finding(self):
        scenario = dict(self.manifest['scenarios']['S01'])
        difference = dict(checkpoint='s01-06-idle', category='layout',
                          path='$.cursor.visible', pi=True, pisharp=False)
        self.assertIsNone(evaluate.match_finding('S01', scenario, difference))

    def test_known_paths_map_explicitly(self):
        scenario = dict(self.manifest['scenarios']['S01'])
        difference = dict(checkpoint='s01-04-incomplete-code-block', category='style',
                          path='$.buffers.alternate.lines[7].cells[3].foreground.value',
                          pi=10983639, pisharp=-1)
        self.assertEqual(evaluate.match_finding('S01', scenario, difference),
                         'S01-code-syntax-builtin-highlight')

        scenario = dict(self.manifest['scenarios']['S02'])
        difference = dict(checkpoint='s02-01-before-reasoning', category='style',
                          path='$.buffers.alternate.lines[21].cells[0].foreground.value',
                          pi=6391244, pisharp=7108219)
        self.assertEqual(evaluate.match_finding('S02', scenario, difference),
                         'S02-reasoning-status-style')

        scenario = dict(self.manifest['scenarios']['S05'])
        difference = dict(checkpoint='s05-05-tool-cancellation-settles', category='text',
                          path='$.viewport[10].cells[1].chars', pi='E', pisharp='O')
        self.assertEqual(evaluate.match_finding('S05', scenario, difference),
                         'S05-tool-cancel-error-wording')

    def test_wrong_values_do_not_match(self):
        scenario = dict(self.manifest['scenarios']['S01'])
        difference = dict(checkpoint='s01-04-incomplete-code-block', category='style',
                          path='$.buffers.alternate.lines[7].cells[3].foreground.value',
                          pi=1, pisharp=2)
        self.assertIsNone(evaluate.match_finding('S01', scenario, difference))


class ProvenanceTests(unittest.TestCase):
    def test_git_provenance_includes_nonignored_untracked_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            repository = Path(temporary)
            subprocess.run(['git', 'init', '-q'], cwd=repository, check=True)
            (repository / 'tracked.cs').write_text('class Tracked {}\n')
            subprocess.run(['git', 'add', 'tracked.cs'], cwd=repository, check=True)
            subprocess.run(['git', '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
                            'commit', '-q', '-m', 'initial'], cwd=repository, check=True)
            self.assertFalse(terminal_run.git_provenance(repository)['dirty'])
            (repository / 'untracked.cs').write_text('class Untracked {}\n')
            provenance = terminal_run.git_provenance(repository)
            self.assertTrue(provenance['dirty'])
            self.assertTrue(any(item.startswith('?? untracked.cs')
                                for item in provenance['trackedChanges']))

    def test_artifact_hash_reads_publish_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            for name, content in (('PiSharp.Cli.dll', b'dll'),
                                  ('PiSharp.Cli.deps.json', b'deps'),
                                  ('PiSharp.Cli.runtimeconfig.json', b'runtime')):
                (directory / name).write_bytes(content)
            (directory / 'pisharp-artifact.json').write_text(json.dumps(dict(
                sourceSha='1' * 40, sourceDirty=False, buildCommand='dotnet publish')))
            artifact = terminal_run.artifact_provenance(directory / 'PiSharp.Cli.dll')
            self.assertEqual(artifact['builtFromSha'], '1' * 40)
            self.assertEqual(artifact['sha256'],
                             __import__('hashlib').sha256(b'dll').hexdigest())
            self.assertEqual(artifact['depsSha256'],
                             __import__('hashlib').sha256(b'deps').hexdigest())
            self.assertEqual(artifact['runtimeConfigSha256'],
                             __import__('hashlib').sha256(b'runtime').hexdigest())


class VerifierTests(unittest.TestCase):
    def setUp(self):
        self.temp = Path(tempfile.mkdtemp(prefix='streaming-verify-'))
        for name in verify_report.REQUIRED_FILES:
            shutil.copy2(EVIDENCE / name, self.temp / name)

    def tearDown(self):
        shutil.rmtree(self.temp)

    def load(self, name):
        return json.loads((self.temp / name).read_text())

    def write(self, name, document):
        (self.temp / name).write_text(json.dumps(document, indent=2) + '\n')

    def checks(self, require_verification=False):
        return verify_report.verify(self.temp, MANIFEST, require_verification)

    def assert_fails(self, name, require_verification=False):
        checks = self.checks(require_verification)
        self.assertFalse(all(item['passed'] for item in checks),
                         f'{name} mutation unexpectedly passed')
        return checks

    def test_unmodified_evidence_passes(self):
        checks = self.checks()
        self.assertTrue(all(item['passed'] for item in checks),
                        [item for item in checks if not item['passed']])

    def scenario(self, document, scenario_id):
        return next(item for item in document['scenarios'] if item['id'] == scenario_id)

    def test_missing_scenario_is_rejected(self):
        for name in ('report.json', 'comparison.json', 'ui-report.json'):
            document = self.load(name)
            if name == 'report.json':
                document['scenarios'] = [item for item in document['scenarios']
                                         if item['id'] != 'S03']
            elif isinstance(document['scenarios'], dict):
                document['scenarios'].pop('S03', None)
            else:
                document['scenarios'] = [item for item in document['scenarios']
                                         if item['id'] != 'S03']
            self.write(name, document)
        self.assert_fails('missing scenario')

    def test_missing_checkpoint_is_rejected(self):
        document = self.load('report.json')
        self.scenario(document, 'S01')['contracts']['pi']['80x24-dark-fullscreen'][
            'checkpointOrder'].pop()
        self.write('report.json', document)
        self.assert_fails('missing checkpoint')

    def test_bad_fixture_checksum_is_rejected(self):
        document = self.load('report.json')
        first = next(iter(document['fixtureChecksums']))
        document['fixtureChecksums'][first] = '0' * 64
        self.write('report.json', document)
        self.assert_fails('bad checksum')

    def test_provenance_missing_sha_is_rejected(self):
        document = self.load('report.json')
        document['captureProvenance']['pi'][0]['sha'] = ''
        self.write('report.json', document)
        self.assert_fails('missing provenance sha')

    def test_provenance_dirty_state_mismatch_is_rejected(self):
        document = self.load('report.json')
        document['captureProvenance']['pisharp'][0]['dirty'] = True
        document['captureProvenance']['pisharp'][0]['trackedChanges'] = []
        self.write('report.json', document)
        self.assert_fails('dirty state mismatch')

    def test_pi_contract_failure_marked_assessable_is_rejected(self):
        document = self.load('report.json')
        contract = self.scenario(document, 'S01')['contracts']['pi']['80x24-dark-fullscreen']
        contract['functionalChecks']['content'] = False
        contract['passes'] = False
        self.write('report.json', document)
        self.assert_fails('Pi contract failure')

    def test_missing_pi_contract_is_rejected(self):
        document = self.load('report.json')
        self.scenario(document, 'S01')['contracts']['pi'] = {}
        self.write('report.json', document)
        self.assert_fails('missing Pi contract')

    def test_forbidden_stream_occurrence_marked_clean_is_rejected(self):
        document = self.load('report.json')
        contract = self.scenario(document, 'S04')['contracts']['pisharp']['80x24-dark-fullscreen']
        contract['forbiddenStream']['occurrenceCount'] = 1
        contract['forbiddenStream']['counts']['CANCEL_LATE'] = 1
        contract['forbiddenStream']['valid'] = False
        contract['cleanupChecks']['noLateOutput'] = True
        self.write('report.json', document)
        self.assert_fails('forbidden stream marked clean')

    def test_missing_forbidden_stream_evidence_is_rejected(self):
        document = self.load('report.json')
        self.scenario(document, 'S05')['contracts']['pisharp']['80x24-dark-fullscreen'][
            'forbiddenStream'] = None
        self.write('report.json', document)
        self.assert_fails('missing forbidden stream')

    def test_inflated_total_is_rejected(self):
        document = self.load('report.json')
        document['total'] += 10
        self.write('report.json', document)
        self.assert_fails('inflated total')

    def test_inflated_scenario_score_is_rejected(self):
        document = self.load('report.json')
        self.scenario(document, 'S01')['functional'] = 10
        self.write('report.json', document)
        self.assert_fails('inflated scenario score')

    def test_invalid_calibration_marked_assessable_is_rejected(self):
        document = self.load('report.json')
        scenario = self.scenario(document, 'S02')
        scenario['calibration']['80x24-dark-fullscreen'] = True
        self.write('report.json', document)
        self.assert_fails('invalid calibration')

    def test_failed_cancellation_marked_successful_is_rejected(self):
        document = self.load('report.json')
        scenario = self.scenario(document, 'S04')
        scenario['contracts']['pisharp']['80x24-dark-fullscreen']['absenceFailures'].append(
            dict(checkpoint='s04-06-partial-retained', token='CANCEL_LATE'))
        self.write('report.json', document)
        self.assert_fails('failed cancellation')

    def test_missing_cleanup_evidence_is_rejected(self):
        document = self.load('report.json')
        scenario = self.scenario(document, 'S05')
        scenario['contracts']['pisharp']['80x24-dark-fullscreen']['cleanupEvidence'] = {}
        self.write('report.json', document)
        self.assert_fails('missing cleanup evidence')

    def test_unmatched_difference_is_rejected(self):
        document = self.load('report.json')
        scenario = self.scenario(document, 'S01')
        scenario['unmatchedDifferences'] = [dict(case='80x24-dark-fullscreen',
                                                 checkpoint='s01-06-idle',
                                                 category='layout',
                                                 path='$.cursor.visible')]
        self.write('report.json', document)
        self.assert_fails('unmatched difference')

    def test_orphan_finding_is_rejected(self):
        # The clean bundle has no findings, so exercise the same integrity check
        # with an orphan finding that has no matching classified comparison sample.
        document = self.load('report.json')
        finding = dict(evaluate.FINDINGS['S01-code-syntax-builtin-highlight'])
        finding.update(id='S01-code-syntax-builtin-highlight', scenario='S01',
                       cases=['80x24-dark-fullscreen'],
                       checkpoints=['s01-04-incomplete-code-block'],
                       differenceCount=1,
                       samplePaths=['$.buffers.alternate.lines[7].cells[3].foreground.value'])
        document['findings'].append(finding)
        self.write('report.json', document)
        checks = self.assert_fails('orphan finding')
        self.assertTrue(any(item['name'] == 'findings complete' and not item['passed']
                            for item in checks),
                        [item for item in checks if not item['passed']])

    def test_missing_artifact_hash_is_rejected(self):
        document = self.load('report.json')
        document['captureProvenance']['pisharp'][0]['artifact']['sha256'] = ''
        self.write('report.json', document)
        self.assert_fails('missing artifact hash')

    def test_built_from_sha_mismatch_is_rejected(self):
        document = self.load('report.json')
        document['captureProvenance']['pisharp'][0]['artifact']['builtFromSha'] = '0' * 40
        self.write('report.json', document)
        self.assert_fails('artifact source mismatch')

    def test_differing_case_artifact_hash_is_rejected(self):
        document = self.load('report.json')
        scenario = self.scenario(document, 'S03')
        artifact = scenario['captures']['80x24-dark-fullscreen']['comparison']['pisharp'][
            'artifact']
        artifact['sha256'] = '0' * 64
        self.write('report.json', document)
        self.assert_fails('differing artifact hash')

    def test_stale_verification_checksum_is_rejected(self):
        shutil.copy2(EVIDENCE / 'verification.json', self.temp / 'verification.json')
        document = self.load('report.json')
        document['total'] += 1
        self.write('report.json', document)
        self.assert_fails('stale verification checksum', require_verification=True)


if __name__ == '__main__':
    unittest.main()
