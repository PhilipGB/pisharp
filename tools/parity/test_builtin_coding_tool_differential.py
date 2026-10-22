"""Guard the corpus and comparison against concealing real contract differences."""
import copy
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest

from builtin_coding_tool_cases import binary, cases


spec = importlib.util.spec_from_file_location('builtin_tool_differential',
                                             Path(__file__).with_name('builtin-coding-tool-differential.py'))
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


class BuiltinToolDifferentialTests(unittest.TestCase):
    def test_distinct_meaningful_cases_cover_every_tool(self):
        fixtures = cases()
        self.assertEqual(len(fixtures), len({case['caseId'] for case in fixtures}))
        for tool in ['read', 'write', 'edit', 'bash', 'grep', 'find', 'ls']:
            selected = [case for case in fixtures if case['tool'] == tool]
            self.assertGreaterEqual(len(selected), 10)
            self.assertTrue(any(case.get('preCancelled') for case in selected))
            self.assertTrue(any('invalid-' in case['caseId'] for case in selected))

    def test_missing_text_and_wrong_line_order_are_differences(self):
        self.assertEqual(['/text'], driver.differences({'text': 'a\nb'}, {'text': 'b\na'}))
        self.assertEqual(['/text'], driver.differences({'text': 'a\nb'}, {'text': 'a'}))

    def test_missing_metadata_and_unexpected_success_are_differences(self):
        self.assertEqual(['/details/truncation'], driver.differences(
            {'details': {'truncation': {'truncated': True}}}, {'details': {}}))
        self.assertEqual(['/isError'], driver.differences({'isError': True}, {'isError': False}))

    def test_full_output_bytes_and_modes_are_compared(self):
        self.assertEqual(['/mode', '/sha256'], driver.differences(
            {'mode': 0o600, 'sha256': 'correct'}, {'mode': 0o644, 'sha256': 'wrong'}))

    def test_reproducible_binary_and_repeated_inputs(self):
        self.assertEqual(b'a\xff', driver.encoded_bytes(binary(b'a\xff')))
        self.assertEqual(b'abcabc', driver.encoded_bytes({'repeat': 'abc', 'count': 2}))
        self.assertEqual(b'abcabc!', driver.encoded_bytes({'concat': [{'repeat': 'abc', 'count': 2}, '!']}))

    def test_only_explicit_variable_strings_are_replaced(self):
        source = {'text': '/tmp/owned/file\nwrong-result', 'details': {'missing': True}}
        value = driver.replace_values(source, {'/tmp/owned': '<workspace>'})
        self.assertEqual('<workspace>/file\nwrong-result', value['text'])
        self.assertEqual({'missing': True}, value['details'])

    def test_snapshot_detects_content_and_inode_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            target = root / 'file'
            target.write_text('original')
            before, identities = driver.filesystem(root)
            replacement = root / 'replacement'
            replacement.write_text('changed')
            replacement.replace(target)
            after, _ = driver.filesystem(root, identities)
            self.assertFalse(after['file']['inodePreserved'])
            self.assertNotEqual(before['file']['sha256'], after['file']['sha256'])

    def test_snapshot_preserves_links_and_permissions(self):
        if os.name == 'nt':
            return
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            target = root / 'file'
            target.write_text('original')
            target.chmod(0)
            (root / 'hard').hardlink_to(target)
            (root / 'broken').symlink_to('missing')
            snapshot, _ = driver.filesystem(root)
            self.assertEqual(0, snapshot['file']['mode'])
            self.assertEqual(0, target.stat().st_mode & 0o777)
            self.assertEqual(['hard'], snapshot['file']['hardlinkPeers'])
            self.assertEqual('missing', snapshot['broken']['target'])

    def test_result_normalization_retains_raw_updates_and_validates_duration(self):
        result = {'content': [], 'structuredContent': {'wall_time_seconds': 0.2},
                  'updates': [{'content': []}, {'content': [{'type': 'text', 'text': 'complete'}]}]}
        original = copy.deepcopy(result)
        value = driver.normalize_result(result, Path('/tmp/owned'), {}, 'exact-bytes')
        self.assertEqual('<measured duration>', value['structuredContent']['wall_time_seconds'])
        self.assertEqual(original['updates'][0], value['updateContract']['initial'])
        self.assertEqual(original['updates'][-1], value['updateContract']['final'])
        self.assertEqual(original, result)

    def test_invalid_duration_is_not_normalized_away(self):
        with self.assertRaises(ValueError):
            driver.normalize_result({'structuredContent': {'wall_time_seconds': -1}, 'updates': []},
                                    Path('/tmp/owned'), {}, 'exact-bytes')

    def test_output_artifacts_must_belong_to_the_isolated_temp_root(self):
        with self.assertRaises(RuntimeError):
            driver.capture_artifacts({'details': {'fullOutputPath': '/unrelated/private-file'}}, Path('/tmp/owned'))


if __name__ == '__main__':
    unittest.main()
