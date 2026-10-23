"""Guard the corpus and comparison against concealing real contract differences."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest

from builtin_coding_tool_cases import binary, cases
from builtin_coding_tool_image_comparison import assess_jpeg_codec, only_codec_pixel_differences


spec = importlib.util.spec_from_file_location('builtin_tool_differential',
                                             Path(__file__).with_name('builtin-coding-tool-differential.py'))
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


class BuiltinToolDifferentialTests(unittest.TestCase):
    @staticmethod
    def codec_fixture(kind):
        fixture = json.loads((Path(__file__).resolve().parents[2] / 'docs/parity/fixtures' /
            'builtin-coding-tool-jpeg-codec-2026-10-22.json').read_text())
        policy = next(value for value in fixture['comparisons'].values() if value['kind'] == kind)
        mime = 'image/jpeg' if kind == 'jpeg_encoder' else 'image/png'
        block = {'type': 'image', 'mimeType': mime, 'data': policy['referenceImageBase64']}
        result = {'content': [{'type': 'text', 'text': 'pinned note'}, block],
                  'structuredContent': {'type': 'image', 'mimeType': mime, 'data': block['data'], 'note': 'pinned note'}}
        return result, policy

    def test_codec_exception_cannot_hide_non_pixel_or_filesystem_differences(self):
        self.assertTrue(only_codec_pixel_differences(['/result/content/1/data/rgbaSha256']))
        for path in ['/filesystem/file/sha256', '/result/content/0/text', '/result/details',
                     '/result/isError', '/result/content/1/data/width', '/result/structuredContent/note']:
            self.assertFalse(only_codec_pixel_differences(['/result/content/1/data/rgbaSha256', path]))

    def test_jpeg_decoder_identity_is_accepted_with_zero_error(self):
        result, policy = self.codec_fixture('jpeg_decoder')
        score = assess_jpeg_codec(result, copy.deepcopy(result), policy)
        self.assertTrue(score['accepted'])
        self.assertEqual(0, score['maximumChannelDelta'])

    def test_jpeg_encoder_identity_checks_reference_quantization_and_fidelity(self):
        result, policy = self.codec_fixture('jpeg_encoder')
        score = assess_jpeg_codec(result, copy.deepcopy(result), policy)
        self.assertTrue(score['accepted'])
        self.assertTrue(score['quantizationMatched'])
        self.assertEqual(0, score['pairedMse'])

    def test_codec_missing_or_uncorrelated_structured_image_is_rejected(self):
        result, policy = self.codec_fixture('jpeg_decoder')
        native = copy.deepcopy(result)
        native['structuredContent']['data'] = 'different'
        self.assertIsNone(assess_jpeg_codec(result, native, policy))
        native['structuredContent'] = None
        self.assertIsNone(assess_jpeg_codec(result, native, policy))

    def test_jpeg_wrong_quality_and_sampling_are_rejected(self):
        import io
        import base64
        from PIL import Image
        result, policy = self.codec_fixture('jpeg_encoder')
        original = Image.open(io.BytesIO(base64.b64decode(result['content'][1]['data'])))
        for quality, sampling in [(95, 0), (20, 2)]:
            output = io.BytesIO()
            original.save(output, 'JPEG', quality=quality, subsampling=sampling)
            native = copy.deepcopy(result)
            data = base64.b64encode(output.getvalue()).decode()
            native['content'][1]['data'] = native['structuredContent']['data'] = data
            self.assertIsNone(assess_jpeg_codec(result, native, policy))

    def test_codec_reference_witness_hash_is_verified(self):
        result, policy = self.codec_fixture('jpeg_encoder')
        policy = {**policy, 'referenceRgbaSha256': 'wrong'}
        self.assertIsNone(assess_jpeg_codec(result, copy.deepcopy(result), policy))

    def test_codec_wrong_pixels_and_dimensions_are_rejected(self):
        import io
        import base64
        from PIL import Image
        for kind in ['jpeg_decoder', 'jpeg_encoder']:
            result, policy = self.codec_fixture(kind)
            reference = Image.open(io.BytesIO(base64.b64decode(result['content'][1]['data'])))
            for size in [reference.size, (reference.width + 1, reference.height)]:
                output = io.BytesIO()
                wrong = Image.new('RGB', size, (0, 0, 0))
                wrong.save(output, 'JPEG' if kind == 'jpeg_encoder' else 'PNG', quality=20, subsampling=0)
                native = copy.deepcopy(result)
                data = base64.b64encode(output.getvalue()).decode()
                native['content'][1]['data'] = native['structuredContent']['data'] = data
                score = assess_jpeg_codec(result, native, policy)
                self.assertTrue(score is None or not score['accepted'])

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
