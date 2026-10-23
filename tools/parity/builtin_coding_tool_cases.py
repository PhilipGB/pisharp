"""Reproducible input descriptions; expected results always come from pinned Pi."""
import base64
import json
from pathlib import Path
import struct
import zlib


def png(width, height, alpha=False):
    def chunk(kind, payload):
        return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload))
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            rows.extend(((x * 37 + y * 11) % 256, (x * 13 + y * 29) % 256, (x + y * 17) % 256))
            if alpha:
                rows.append((x * 23 + y * 7) % 256)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6 if alpha else 2, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))


def binary(data):
    return {'base64': base64.b64encode(data).decode('ascii')}


def solid_png(width, height):
    def chunk(kind, payload):
        return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload))
    compressor = zlib.compressobj()
    row = b'\0' + bytes((32, 96, 160)) * width
    payload = b''.join(compressor.compress(row) for _ in range(height)) + compressor.flush()
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
            + chunk(b'IDAT', payload) + chunk(b'IEND', b''))


def repeated(text, count):
    return {'repeat': text, 'count': count}


def cases():
    result = []

    def add(tool, name, arguments, files=None, **setup):
        result.append({'caseId': f'{tool}-{name}', 'tool': tool, 'arguments': arguments,
                       'files': files or {}, **setup})

    for name, text in [('utf8', 'café 日本語 😀\nβeta\n'), ('bom', '\ufeffalpha\nbeta'),
                       ('lf', 'one\ntwo\n'), ('crlf', 'one\r\ntwo\r\n'),
                       ('cr', 'one\rtwo\r'), ('empty', ''), ('no-newline', 'one\ntwo')]:
        add('read', name, {'path': 'file.txt'}, {'file.txt': text})
    for name, args in [('offset', {'offset': 2}), ('limit', {'limit': 1}),
                       ('window', {'offset': 2, 'limit': 1}), ('final-empty-line', {'offset': 4}),
                       ('offset-beyond-end', {'offset': 5}), ('offset-zero', {'offset': 0}),
                       ('offset-negative', {'offset': -2}), ('limit-zero', {'limit': 0}),
                       ('limit-negative', {'limit': -1}), ('fractional-offset', {'offset': 1.5}),
                       ('optional-null', {'offset': None, 'limit': None})]:
        add('read', name, {'path': 'file.txt', **args}, {'file.txt': 'one\ntwo\nthree\n'})
    add('read', 'absolute-nested', {'path': '{WORKSPACE}/nested/file.txt'}, {'nested/file.txt': 'absolute\n'})
    add('read', 'at-prefix', {'path': '@nested/file.txt'}, {'nested/file.txt': 'at path\n'})
    add('read', 'unicode-space-path', {'path': 'file\u00a0name.txt'}, {'file name.txt': 'spaces\n'})
    add('read', 'missing', {'path': 'missing.txt'})
    add('read', 'directory', {'path': 'directory'}, directories={'directory': 0o755})
    add('read', 'permission', {'path': 'file.txt'}, {'file.txt': 'private\n'}, modes={'file.txt': 0})
    add('read', 'symlink', {'path': 'alias'}, {'target': 'linked\n'}, symlinks={'alias': 'target'})
    for name, text in [('exact-lines', repeated('x\n', 2000)), ('over-lines', repeated('x\n', 2001)),
                       ('exact-bytes', repeated('x', 51200)), ('oversized-first-line', repeated('x', 51201)),
                       ('multibyte-line', repeated('😀', 12801)), ('byte-before-lines', repeated('x' * 300 + '\n', 300)),
                       ('streaming-window', repeated('long line 😀\r\n', 170000))]:
        add('read', name, {'path': 'file.txt'}, {'file.txt': text})
    add('read', 'invalid-utf8', {'path': 'file.txt'}, {'file.txt': binary(b'a\xff\xe2\x82\nlast')})
    add('read', 'invalid-utf8-stream', {'path': 'file.txt', 'offset': 70000, 'limit': 4},
        {'file.txt': {'base64Repeat': base64.b64encode(b'\xff' * 20 + b'\n').decode(), 'count': 120000}})
    add('read', 'huge-single-line', {'path': 'file.txt'}, {'file.txt': repeated('\0', 64 * 1024 * 1024)})
    add('read', 'binary-looking', {'path': 'file.txt'}, {'file.txt': binary(b'a\x00b\x01\nlast')})
    for name, arguments in [
        ('fractional-limit', {'limit': 1.5}), ('sub-line-limit', {'limit': 0.5}),
        ('fractional-window', {'offset': 2.5, 'limit': 1.5}),
        ('fractional-end', {'offset': 4.5}), ('negative-end-before-start', {'offset': 2, 'limit': -1}),
        ('negative-end-before-file', {'limit': -100}),
        ('huge-offset', {'offset': 1e100}), ('huge-negative-offset', {'offset': -1e100}),
        ('huge-limit', {'limit': 1e100}), ('huge-negative-limit', {'limit': -1e100})]:
        add('read', name, {'path': 'file.txt', **arguments}, {'file.txt': 'one\ntwo\nthree\n'})
    add('read', 'fractional-oversized-line', {'path': 'file.txt', 'offset': 1.5},
        {'file.txt': repeated('x', 51201)})
    for name, arguments in [('zero-window-stream', {'limit': 0}),
                             ('negative-end-stream', {'limit': -1}),
                             ('fractional-window-stream', {'offset': 70000.5, 'limit': 2.5})]:
        add('read', name, {'path': 'file.txt', **arguments}, {'file.txt': repeated('long line 😀\r\n', 170000)})
    add('read', 'file-url', {'path': 'file://{WORKSPACE}/nested/file.txt'}, {'nested/file.txt': 'URI path\n'})
    add('read', 'nfd-path', {'path': 'café.txt'}, {'cafe\u0301.txt': 'decomposed filename\n'})
    add('read', 'curly-quote-path', {'path': "Capture d'écran.txt"}, {"Capture d’écran.txt": 'quote variant\n'})
    add('read', 'nfd-curly-path', {'path': "écran d'écran.txt"}, {"e\u0301cran d’e\u0301cran.txt": 'combined variant\n'})
    add('read', 'screenshot-time-space', {'path': 'Capture 10.30 AM.png'}, {'Capture 10.30\u202fAM.png': 'time variant\n'})
    add('read', 'exact-path-precedence', {'path': 'café.txt'}, {'café.txt': 'exact\n', 'cafe\u0301.txt': 'variant\n'})
    add('read', 'empty-path', {'path': ''})
    add('read', 'nul-path', {'path': 'invalid\x00path'})
    add('read', 'file-parent', {'path': 'file.txt/child'}, {'file.txt': 'parent file\n'})
    add('read', 'permission-parent', {'path': 'secret/file.txt'}, {'secret/file.txt': 'private\n'}, modes={'secret': 0})
    add('read', 'dangling-symlink', {'path': 'alias'}, symlinks={'alias': 'missing'})
    add('read', 'symlink-loop', {'path': 'alias'}, symlinks={'alias': 'alias'})
    add('read', 'symlink-directory', {'path': 'alias'}, directories={'directory': 0o755}, symlinks={'alias': 'directory'})
    add('read', 'png-magic', {'path': 'image.dat'}, {'image.dat': binary(png(7, 5))})
    add('read', 'png-alpha', {'path': 'image.png'}, {'image.png': binary(png(13, 7, alpha=True))})
    add('read', 'png-resize', {'path': 'image.png'}, {'image.png': binary(png(241, 37))},
        options={'resizeOptions': {'maxWidth': 80, 'maxHeight': 80}})
    add('read', 'corrupt-png', {'path': 'image.png'}, {'image.png': binary(b'\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDRcorrupt')})
    add('read', 'jpeg-ls-not-inline', {'path': 'image.jls'}, {'image.jls': binary(b'\xff\xd8\xff\xf7\x00\x00')})
    add('read', 'invalid-image-extension', {'path': 'image.jpg'}, {'image.jpg': 'ordinary source text\n'})
    add('read', 'missing-required', {}, {'file.txt': 'unchanged'})
    add('read', 'invalid-path-type', {'path': []})
    add('read', 'invalid-offset-type', {'path': 'file.txt', 'offset': {}}, {'file.txt': 'unchanged'})
    add('read', 'pre-cancelled', {'path': 'file.txt'}, {'file.txt': 'unchanged'}, preCancelled=True)

    images = json.loads((Path(__file__).parent / 'fixtures' / 'builtin-coding-tool-read-images.json').read_text())['assets']
    for image in ['png-rgb', 'png-alpha', 'jpeg-rgb', 'jpeg-exif-6', 'webp-rgb', 'webp-alpha', 'webp-exif-6',
                  'bmp-rgb', 'bmp-alpha', 'gif-single', 'gif-animated', 'webp-animated', 'apng-not-inline']:
        add('read', 'image-original-' + image, {'path': 'image.dat'}, {'image.dat': {'base64': images[image]['base64']}})
        if image.startswith('bmp-'):
            result[-1]['imagePolicy'] = 'decoded-rgba'
    for image in ['png-rgb', 'png-alpha', 'jpeg-rgb', 'webp-rgb', 'webp-alpha', 'webp-exif-6', 'bmp-rgb',
                  'bmp-alpha', 'gif-single', 'gif-animated', 'webp-animated', 'png-exif-6'] + [f'jpeg-exif-{n}' for n in range(2, 9)]:
        add('read', 'image-resized-' + image, {'path': 'image.dat'}, {'image.dat': {'base64': images[image]['base64']}},
            options={'resizeOptions': {'maxWidth': 8, 'maxHeight': 6}}, imagePolicy='decoded-rgba')
    for image in ['png-rgb', 'jpeg-rgb', 'gif-single', 'webp-rgb', 'bmp-rgb']:
        add('read', 'image-truncated-' + image, {'path': 'image.dat'},
            {'image.dat': {'base64': images[image + '-truncated']['base64']}})
    for image in ['png-extreme-wide', 'png-extreme-tall']:
        add('read', 'image-' + image, {'path': 'image.dat'}, {'image.dat': {'base64': images[image]['base64']}})
    for name, profile in [('jpeg-quality', {'maxWidth': 60, 'maxHeight': 40, 'maxBytes': 4000, 'jpegQuality': 20}),
                          ('progressive-payload', {'maxBytes': 1500}), ('impossible-payload', {'maxBytes': 20})]:
        add('read', 'image-' + name, {'path': 'image.dat'}, {'image.dat': {'base64': images['png-noise']['base64']}},
            options={'resizeOptions': profile}, imagePolicy='decoded-rgba')
    codec_comparisons = json.loads((Path(__file__).resolve().parents[2] / 'docs/parity/fixtures' /
        'builtin-coding-tool-jpeg-codec-2026-10-22.json').read_text())['comparisons']
    for case in result:
        if case['caseId'] in codec_comparisons:
            case['jpegComparison'] = codec_comparisons[case['caseId']]
    for label, asset, model in [
        ('text-only-model', 'png-rgb', {'input': ['text']}),
        ('text-only-invalid-image', 'png-rgb-truncated', {'input': ['text']}),
        ('vision-model', 'png-rgb', {'input': ['text', 'image']}),
        ('model-profile', 'png-rgb', {'input': ['text', 'image'], 'inputLimits': {'images': {
            'resize': {'maxWidth': 4, 'maxHeight': 3}}}})]:
        add('read', 'image-' + label, {'path': 'image.dat'}, {'image.dat': {'base64': images[asset]['base64']}},
            context={'model': model}, imagePolicy='decoded-rgba' if label == 'model-profile' else 'exact-bytes',
            options={'resizeOptions': {'maxWidth': 8, 'maxHeight': 6}} if label == 'model-profile' else {})

    add('read', 'image-input-safety-boundary', {'path': 'image.dat'},
        {'image.dat': {'concat': [{'base64': images['png-rgb']['base64']}, repeated('\0', 20 * 1024 * 1024)]}},
        imagePolicy='decoded-rgba', acceptanceClass='intentional-safety-boundary',
        safetyBoundary='Encoded inputs above 20 MiB are omitted before allocation, even when trailing bytes are harmless.')
    add('read', 'image-pixel-safety-boundary', {'path': 'image.dat'},
        {'image.dat': binary(solid_png(8001, 4000))}, imagePolicy='decoded-rgba',
        acceptanceClass='intentional-safety-boundary',
        safetyBoundary='Images above 32,000,000 decoded pixels are omitted before allocating a decoded bitmap.')

    for name, content in [('new', 'new file\n'), ('empty', ''), ('unicode', '日本語 😀 café\n'), ('bom', '\ufefffile\n')]:
        add('write', name, {'path': 'file.txt', 'content': content})
    add('write', 'existing', {'path': 'file.txt', 'content': 'replacement\n'}, {'file.txt': 'original\n'})
    add('write', 'nested-parents', {'path': 'a/b/file.txt', 'content': 'nested\n'})
    add('write', 'absolute', {'path': '{WORKSPACE}/file.txt', 'content': 'absolute\n'})
    add('write', 'at-prefix', {'path': '@file.txt', 'content': 'at path\n'})
    add('write', 'directory', {'path': 'dir', 'content': 'bad'}, directories={'dir': 0o755})
    add('write', 'file-parent', {'path': 'parent/file', 'content': 'bad'}, {'parent': 'original'})
    add('write', 'file-ancestor', {'path': 'parent/child/file', 'content': 'bad'}, {'parent': 'original'})
    add('write', 'readonly-file', {'path': 'file.txt', 'content': 'bad'}, {'file.txt': 'original'}, modes={'file.txt': 0o444})
    add('write', 'readonly-directory', {'path': 'dir/file', 'content': 'bad'}, directories={'dir': 0o555})
    add('write', 'symlink', {'path': 'alias', 'content': 'replacement'}, {'target': 'original'}, symlinks={'alias': 'target'})
    add('write', 'dangling-symlink', {'path': 'alias', 'content': 'new target'}, symlinks={'alias': 'target'})
    add('write', 'hardlink', {'path': 'file.txt', 'content': 'replacement'}, {'file.txt': 'original'}, hardlinks={'alias': 'file.txt'})
    add('write', 'existing-mode', {'path': 'file.txt', 'content': 'replacement'}, {'file.txt': 'original'}, modes={'file.txt': 0o640})
    add('write', 'unicode-space-path', {'path': 'file\u00a0name.txt', 'content': 'spaces\n'})
    add('write', 'file-url', {'path': 'file://{WORKSPACE}/nested/file.txt', 'content': 'URI\n'})
    add('write', 'relative-normalization', {'path': './unused/../file.txt', 'content': 'normalized\n'})
    add('write', 'empty-path', {'path': '', 'content': 'bad'})
    add('write', 'nul-path', {'path': 'invalid\x00path', 'content': 'bad'})
    add('write', 'long-name', {'path': '\u03c0' * 130, 'content': 'bad'})
    add('write', 'symlink-loop', {'path': 'alias', 'content': 'bad'}, symlinks={'alias': 'alias'})
    add('write', 'symlink-directory', {'path': 'alias', 'content': 'bad'},
        directories={'directory': 0o755}, symlinks={'alias': 'directory'})
    add('write', 'symlink-parent-existing', {'path': 'alias/file', 'content': 'replacement'},
        {'directory/file': 'original'}, symlinks={'alias': 'directory'})
    add('write', 'symlink-parent-new', {'path': 'alias/file', 'content': 'new'},
        directories={'directory': 0o755}, symlinks={'alias': 'directory'})
    add('write', 'symlink-missing-target-parent', {'path': 'alias', 'content': 'bad'},
        symlinks={'alias': 'missing/target'})
    add('write', 'symlink-readonly-target', {'path': 'alias', 'content': 'bad'},
        {'target': 'original'}, symlinks={'alias': 'target'}, modes={'target': 0o444})
    add('write', 'symlink-file-parent', {'path': 'alias/child', 'content': 'bad'},
        {'target': 'original'}, symlinks={'alias': 'target'})
    add('write', 'symlink-loop-parent', {'path': 'alias/child', 'content': 'bad'}, symlinks={'alias': 'alias'})
    add('write', 'readonly-directory-existing', {'path': 'dir/file', 'content': 'replacement'},
        {'dir/file': 'original'}, modes={'dir': 0o555})
    add('write', 'readonly-ancestor', {'path': 'secret/nested/file', 'content': 'bad'}, directories={'secret': 0o500})
    add('write', 'write-only-file', {'path': 'file.txt', 'content': 'replacement'},
        {'file.txt': 'original'}, modes={'file.txt': 0o200})
    add('write', 'inaccessible-parent', {'path': 'secret/file', 'content': 'bad'},
        {'secret/file': 'original'}, modes={'secret': 0o600})
    add('write', 'missing-content', {'path': 'file.txt'}, {'file.txt': 'original'})
    add('write', 'invalid-content-type', {'path': 'file.txt', 'content': {}}, {'file.txt': 'original'})
    add('write', 'invalid-path-type', {'path': [], 'content': 'bad'})
    add('write', 'pre-cancelled', {'path': 'file.txt', 'content': 'bad'}, {'file.txt': 'original'}, preCancelled=True)

    def edit(name, text, replacements, **setup):
        add('edit', name, {'path': 'file.txt', 'edits': [{'oldText': a, 'newText': b} for a, b in replacements]},
            {'file.txt': text}, **setup)

    edit('single', 'one\ntwo\n', [('one', 'ONE')])
    edit('batch-original-snapshot', 'foo\nbar\nbaz\n', [('foo\n', 'foo bar\n'), ('bar\n', 'BAR\n')])
    edit('insert', 'alpha\nbeta\n', [('alpha\n', 'alpha\ninserted\n')])
    edit('delete', 'alpha\nbeta\n', [('alpha\n', '')])
    edit('empty-file', '', [('missing', 'new')])
    edit('empty-old-text', 'unchanged', [('', 'bad')])
    edit('ambiguous', 'repeat repeat', [('repeat', 'bad')])
    edit('missing-match', 'unchanged', [('missing', 'bad')])
    edit('invalid-batch-atomic', 'alpha\nbeta\n', [('alpha', 'ALPHA'), ('missing', 'bad')])
    edit('overlap', 'one\ntwo\nthree\n', [('one\ntwo', 'bad'), ('two\nthree', 'bad')])
    edit('nearby', 'alpha\nbeta\ngamma\n', [('alpha', 'ALPHA'), ('beta', 'BETA')])
    edit('separated-hunks', '\n'.join(f'line {n}' for n in range(1, 31)) + '\n', [('line 2\n', 'LINE 2\n'), ('line 29\n', 'LINE 29\n')])
    edit('crlf', 'alpha\r\nbeta\r\n', [('alpha\nbeta', 'ALPHA\nBETA')])
    edit('bom', '\ufeffalpha\nbeta\n', [('alpha', 'ALPHA')])
    edit('unicode', '日本語 café 😀\n', [('café', 'カフェ')])
    edit('trailing-whitespace', 'alpha  \nbeta\n', [('alpha\n', 'ALPHA\n')])
    edit('blank-lines', 'alpha\n\nbeta\n', [('alpha\n\n', 'ALPHA\n\n')])
    edit('mixed-endings', 'alpha\r\nbeta\ngamma\r', [('beta', 'BETA')])
    edit('fuzzy-unchanged-lines', 'keep “smart”\nchange “this”\nkeep café  \n', [('change "this"', 'changed')])
    edit('no-change', 'unchanged\n', [('unchanged', 'unchanged')])
    edit('readonly', 'alpha\n', [('alpha', 'bad')], modes={'file.txt': 0o444})
    edit('symlink', 'alpha\n', [('alpha', 'ALPHA')], symlinks={'alias': 'file.txt'})
    result[-1]['arguments']['path'] = 'alias'
    edit('hardlink', 'alpha\n', [('alpha', 'ALPHA')], hardlinks={'alias': 'file.txt'})
    edit('invalid-utf8', binary(b'alpha\xff\n'), [('alpha', 'ALPHA')])
    add('edit', 'missing-target', {'path': 'missing', 'edits': [{'oldText': 'x', 'newText': 'y'}]})
    add('edit', 'directory', {'path': 'dir', 'edits': [{'oldText': 'x', 'newText': 'y'}]}, directories={'dir': 0o755})
    add('edit', 'empty-batch', {'path': 'file.txt', 'edits': []}, {'file.txt': 'unchanged'})
    add('edit', 'legacy-input', {'path': 'file.txt', 'oldText': 'alpha', 'newText': 'ALPHA'}, {'file.txt': 'alpha\n'})
    add('edit', 'stringified-edits', {'path': 'file.txt', 'edits': '[{"oldText":"alpha","newText":"ALPHA"}]'}, {'file.txt': 'alpha\n'})
    add('edit', 'single-object-edits', {'path': 'file.txt', 'edits': {'oldText': 'alpha', 'newText': 'ALPHA'}}, {'file.txt': 'alpha\n'})
    add('edit', 'missing-required', {'path': 'file.txt'}, {'file.txt': 'unchanged'})
    add('edit', 'invalid-element-type', {'path': 'file.txt', 'edits': [1]}, {'file.txt': 'unchanged'})
    edit('pre-cancelled', 'unchanged', [('unchanged', 'bad')], preCancelled=True)
    edit('overlapping-occurrences', 'aaa', [('aa', 'X')])
    edit('non-overlap-duplicate-count', 'aaaaa', [('aa', 'X')])
    edit('normalized-duplicate-exact', '\u201cx\u201d\n"x"\n', [('\u201cx\u201d', 'bad')])
    edit('normalized-duplicate-fuzzy', '\u201cx\u201d\n\u201cx\u201d\n', [('"x"', 'bad')])
    edit('batch-normalized-duplicate', '\u201cx\u201d\n"x"\nbeta\n', [('\u201cx\u201d', 'bad'), ('beta', 'BETA')])
    edit('all-whitespace-old', 'ab', [(' ', 'X')])
    edit('empty-normalized-source', ' ', [('\t', 'X')])
    edit('whitespace-duplicate-count', 'a b', [(' ', 'X')])
    edit('nfkc-unchanged-lines', 'keep \ufb03\nreplace \ufb03\nkeep \u2460\n', [('replace ffi', 'done')])
    edit('multiple-fuzzy-one-line', '\u201calpha\u201d then \u201cbeta\u201d\n', [('"alpha"', 'A'), ('"beta"', 'B')])
    edit('batch-fuzzy-exact-space', '\u201cx\u201d\nalpha tail\n', [('"x"', 'X'), ('alpha ', 'A')])
    edit('stable-overlap-index', 'alpha\n', [('alpha', f'ALPHA {index}') for index in range(20)])
    edit('adjacent-boundaries', 'abcDEFghi', [('abc', 'ABC'), ('DEF', 'def')])
    edit('newline-only-replacement', 'alpha\nbeta\n', [('alpha\n', '\n')])
    edit('invalid-utf8-before-match', binary(b'\xffalpha\n'), [('alpha', 'ALPHA')])
    edit('invalid-utf8-bom', binary(b'\xef\xbb\xbfalpha\xff\n'), [('alpha', 'ALPHA')])
    edit('cr-only', 'alpha\rbeta\r', [('beta', 'BETA')])
    edit('mixed-leading-lf', 'alpha\nbeta\r\ngamma\r', [('beta', 'BETA')])
    edit('no-final-newline', 'alpha\nbeta', [('beta', 'BETA')])
    edit('final-newline-removed', 'alpha\nbeta\n', [('beta\n', 'beta')])
    edit('whole-delete', 'alpha\n', [('alpha\n', '')])
    edit('repeat-line-diff', 'a\nb\na\nc\n', [('b\na\n', 'a\nb\n')])
    for label, path_value in [('at-prefix', '@file.txt'), ('file-url', 'file://{WORKSPACE}/file.txt'),
                              ('nul-path', 'invalid\0path'), ('long-name', '\u03c0' * 130)]:
        edit(label, 'alpha\n', [('alpha', 'ALPHA')])
        result[-1]['arguments']['path'] = path_value
    for label, mode in [('unreadable-file', 0), ('write-only-file', 0o200), ('existing-mode', 0o640)]:
        edit(label, 'alpha\n', [('alpha', 'ALPHA')], modes={'file.txt': mode})
    edit('inaccessible-parent', 'unused', [('alpha', 'ALPHA')])
    result[-1]['files'] = {'secret/file': 'alpha\n'}
    result[-1]['modes'] = {'secret': 0o600}
    result[-1]['arguments']['path'] = 'secret/file'
    edit('readonly-parent', 'unused', [('alpha', 'ALPHA')])
    result[-1]['files'] = {'dir/file': 'alpha\n'}
    result[-1]['modes'] = {'dir': 0o555}
    result[-1]['arguments']['path'] = 'dir/file'
    for label, target in [('symlink-loop', 'alias'), ('dangling-symlink', 'missing'),
                          ('symlink-directory', 'dir'), ('symlink-readonly-target', 'file.txt')]:
        edit(label, 'alpha\n', [('alpha', 'ALPHA')], symlinks={'alias': target})
        result[-1]['arguments']['path'] = 'alias'
        if label == 'symlink-directory':
            result[-1]['directories'] = {'dir': 0o755}
        if label == 'symlink-readonly-target':
            result[-1]['modes'] = {'file.txt': 0o444}
    add('edit', 'file-parent', {'path': 'file/child', 'edits': [{'oldText': 'x', 'newText': 'y'}]}, {'file': 'original'})

    for name, command in [('stdout', "printf 'hello\\n'"), ('no-output', 'true'), ('exit-one', "printf 'before\\n'; exit 1"),
                           ('exit-127', 'exit 127'), ('stderr', "printf 'error\\n' >&2"),
                           ('combined-ordered', "{ printf 'out\\n'; printf 'err\\n' >&2; } 2>&1"),
                           ('unicode', "printf 'café 日本語 😀\\n'"), ('invalid-utf8', "printf '\\377\\342\\202'"),
                           ('carriage-return', "printf 'first\\rsecond\\r\\n'"),
                           ('raw-controls', "printf '\\033[31mred\\033[0m\\001\\n'"),
                           ('signal-term', "printf 'before\\n'; kill -TERM $$"),
                           ('signal-kill', "printf 'before\\n'; kill -KILL $$"),
                           ('exact-lines', 'seq 1 2000'), ('over-lines', 'seq 1 2001'),
                           ('exact-bytes', "printf '%51200s' | tr ' ' x"),
                           ('oversized-line', "printf '%100000s' | tr ' ' x"),
                           ('structured-over-one-mib', 'seq 1 220000'),
                           ('stale-session-environment', "printf '%s|%s|%s|%s|%s' \"${PI_SESSION_ID-unset}\" \"${PI_SESSION_FILE-unset}\" \"${PI_PROVIDER-unset}\" \"${PI_MODEL-unset}\" \"${PI_REASONING_LEVEL-unset}\"")]:
        add('bash', name, {'command': command})
    add('bash', 'prefix', {'command': "printf '%s\\n' \"$TOOL_PARITY_PREFIX\""},
        options={'commandPrefix': 'export TOOL_PARITY_PREFIX=from-prefix'})
    add('bash', 'configured-shell', {'command': "printf '%s' \"${BASH_VERSION-not-bash}\""}, options={'shellPath': '/bin/sh'})
    add('bash', 'missing-shell', {'command': 'true'}, options={'shellPath': '{WORKSPACE}/missing-shell'})
    add('bash', 'timeout', {'command': "printf 'started\\n'; while :; do :; done", 'timeout': 0.2})
    add('bash', 'abort-on-output', {'command': "printf 'READY\\n'; while :; do :; done"}, cancelOnOutput='READY')
    add('bash', 'timeout-zero', {'command': 'true', 'timeout': 0})
    add('bash', 'timeout-too-large', {'command': 'true', 'timeout': 2147484})
    add('bash', 'missing-required', {})
    add('bash', 'invalid-command-type', {'command': []})
    add('bash', 'pre-cancelled', {'command': "printf 'bad' > mutation"}, preCancelled=True)
    add('bash', 'utf8-bom', {'command': "printf '\\357\\273\\277hello\\n'"})
    add('bash', 'bom-only', {'command': "printf '\\357\\273\\277'"})
    for label, prefix, suffix in [('split-utf8', '\\342', '\\202\\254'),
                                  ('split-bom', '\\357\\273', '\\277hello')]:
        add('bash', label, {'command': f"printf '{prefix}'; read -r signal < ack; printf '{suffix}'", 'timeout': 5},
            fifos=['ack'], acknowledgeEmptyUpdate=True)
    add('bash', 'oversized-line-midpoint', {'command': "python3 -c \"import sys;sys.stdout.write('x'*53504)\""})
    add('bash', 'empty-lines', {'command': "python3 -c \"import sys;sys.stdout.write('\\n'*2200)\""})
    add('bash', 'stdin-ignored', {'command': 'if read -r line; then printf "read:%s" "$line"; else printf eof; fi'})
    add('bash', 'heredoc', {'command': "cat <<'END'\nhello 日本語\n$literal\nEND"})
    add('bash', 'nonexecutable-shell', {'command': 'true'}, {'shell': '#!/bin/sh\nprintf unexpected'},
        modes={'shell': 0o644}, options={'shellPath': '{WORKSPACE}/shell'})
    add('bash', 'bad-interpreter-shell', {'command': 'true'},
        {'shell': '#!/pisharp-fixture-no-such-interpreter\nprintf unexpected'}, modes={'shell': 0o755},
        options={'shellPath': '{WORKSPACE}/shell'})
    add('bash', 'directory-shell', {'command': 'true'}, directories={'shell': 0o755},
        options={'shellPath': '{WORKSPACE}/shell'})
    add('bash', 'nul-command', {'command': 'printf a\0b'})
    add('bash', 'oversized-command', {'command': "printf ok #" + 'x' * 140000})
    add('bash', 'missing-working-directory', {'command': 'true'}, executionCwd='{WORKSPACE}/missing')
    add('bash', 'file-working-directory', {'command': 'true'}, {'file': 'not a directory'}, executionCwd='{WORKSPACE}/file')
    add('bash', 'inaccessible-working-directory', {'command': 'true'}, directories={'private': 0},
        executionCwd='{WORKSPACE}/private')

    for name, args, files in [
        ('literal', {'pattern': 'a.b', 'literal': True}, {'file.txt': 'a.b\naxb\n'}),
        ('regex', {'pattern': '^a.b$'}, {'file.txt': 'a.b\naxb\nelse\n'}),
        ('case-sensitive', {'pattern': 'needle'}, {'file.txt': 'NEEDLE\nneedle\n'}),
        ('ignore-case', {'pattern': 'needle', 'ignoreCase': True}, {'file.txt': 'NEEDLE\nneedle\n'}),
        ('context', {'pattern': 'needle', 'context': 1}, {'file.txt': 'before\nneedle\nafter\n'}),
        ('overlapping-context', {'pattern': 'needle', 'context': 1}, {'file.txt': 'before\nneedle\nneedle\nafter\n'}),
        ('single-file', {'pattern': 'needle', 'path': 'file.txt'}, {'file.txt': 'needle\n'}),
        ('glob-include', {'pattern': 'needle', 'glob': '*.cs'}, {'file.cs': 'needle\n', 'file.md': 'needle\n'}),
        ('glob-exclude', {'pattern': 'needle', 'glob': '!*.md'}, {'file.cs': 'needle\n', 'file.md': 'needle\n'}),
        ('hidden', {'pattern': 'needle'}, {'.hidden/file': 'needle\n'}),
        ('binary', {'pattern': 'needle'}, {'file.bin': binary(b'needle\n\x00binary\n')}),
        ('invalid-utf8', {'pattern': 'needle'}, {'file.txt': binary(b'needle\xff\nnext\n')}),
        ('cr-only', {'pattern': 'needle'}, {'file.txt': 'before\rneedle\rafter'}),
        ('long-line', {'pattern': 'needle'}, {'file.txt': 'needle' + '😀' * 400}),
        ('negative-context', {'pattern': 'needle', 'context': -1}, {'file.txt': 'needle\n'}),
        ('no-match', {'pattern': 'absent'}, {'file.txt': 'needle\n'}),
        ('match-limit', {'pattern': 'needle', 'limit': 1}, {'file.txt': 'needle\nneedle\n'}),
        ('zero-limit', {'pattern': 'needle', 'limit': 0}, {'file.txt': 'needle\nneedle\n'}),
        ('unsupported-lookaround', {'pattern': '(?=needle)'}, {'file.txt': 'needle\n'}),
        ('invalid-regex', {'pattern': '('}, {'file.txt': 'needle\n'}),
        ('flag-like-pattern', {'pattern': '-needle'}, {'file.txt': '-needle\n'}),
        ('large-file', {'pattern': 'late-needle'}, {'file.txt': {'concat': [repeated('unmatched\n', 1100000), 'late-needle\n']}}),
        ('byte-limit', {'pattern': 'needle', 'limit': 500}, {'file.txt': repeated('needle ' + 'x' * 450 + '\n', 150)}),
    ]:
        add('grep', name, args, files)
    for name, args, files in [
        ('fractional-limit', {'pattern': 'needle', 'limit': 1.5}, {'file.txt': 'needle\nneedle\nneedle\n'}),
        ('fractional-context', {'pattern': 'needle', 'context': 1.5}, {'file.txt': 'one\ntwo\nneedle\nfour\nfive\n'}),
        ('exact-match-limit', {'pattern': 'needle', 'limit': 2}, {'file.txt': 'needle\nneedle\n'}),
        ('single-line-multiple-matches', {'pattern': 'needle', 'limit': 2}, {'file.txt': 'needle needle needle\n'}),
        ('regex-backreference', {'pattern': r'(word)\s+\1'}, {'file.txt': 'word word\n'}),
        ('regex-lookbehind', {'pattern': '(?<=pre)needle'}, {'file.txt': 'preneedle\n'}),
        ('regex-named-capture', {'pattern': '(?P<item>needle)'}, {'file.txt': 'needle\n'}),
        ('regex-posix-class', {'pattern': '[[:alpha:]]+'}, {'file.txt': 'abc\n123\n日本語\n'}),
        ('regex-unicode-script', {'pattern': r'\p{Script=Greek}+'}, {'file.txt': 'alpha\nαβγ\n'}),
        ('regex-newline', {'pattern': 'needle\nnext'}, {'file.txt': 'needle\nnext\n'}),
        ('regex-linear-time', {'pattern': '^(a+)+$'}, {'file.txt': 'a' * 2000 + '!\n'}),
        ('glob-braces', {'pattern': 'needle', 'glob': '*.{cs,md}'}, {'file.cs': 'needle\n', 'file.md': 'needle\n', 'file.py': 'needle\n'}),
        ('glob-recursive', {'pattern': 'needle', 'glob': 'src/**/*.cs'}, {'src/a.cs': 'needle\n', 'src/deep/b.cs': 'needle\n', 'test/c.cs': 'needle\n'}),
        ('glob-character-class', {'pattern': 'needle', 'glob': '[ab].txt'}, {'a.txt': 'needle\n', 'b.txt': 'needle\n', 'c.txt': 'needle\n'}),
        ('glob-invalid', {'pattern': 'needle', 'glob': '['}, {'file.txt': 'needle\n'}),
        ('literal-regex-characters', {'pattern': '[a]+(b)', 'literal': True}, {'file.txt': '[a]+(b)\naab\n'}),
        ('unicode-case-fold', {'pattern': 'kΣ', 'ignoreCase': True}, {'file.txt': 'Kσ\nKς\nkΣ\n'}),
        ('bom', {'pattern': 'needle'}, {'file.txt': '\ufeffneedle\n'}),
        ('bom-context', {'pattern': 'needle', 'context': 1}, {'file.txt': '\ufeffbefore\nneedle\nafter\n'}),
        ('crlf-context', {'pattern': 'needle', 'context': 1}, {'file.txt': 'before\r\nneedle\r\nafter\r\n'}),
        ('cr-only-context', {'pattern': 'needle', 'context': 1}, {'file.txt': 'before\rneedle\rafter'}),
        ('invalid-utf8-context', {'pattern': 'needle', 'context': 1}, {'file.txt': binary(b'before\xff\nneedle\xfe\nafter\n')}),
        ('binary-before-match', {'pattern': 'needle'}, {'file.bin': binary(b'\0binary\nneedle\n')}),
        ('filename-unicode', {'pattern': 'needle'}, {'目录/日本語.txt': 'needle\n'}),
        ('filename-newline', {'pattern': 'needle'}, {'line\nbreak.txt': 'needle\n'}),
        ('explicit-at-path', {'pattern': 'needle', 'path': '@file.txt'}, {'file.txt': 'needle\n'}),
        ('file-ancestor', {'pattern': 'needle', 'path': 'file/child'}, {'file': 'needle\n'}),
        ('huge-single-line', {'pattern': 'needle'}, {'file.txt': {'concat': ['needle', repeated('x', 12 * 1024 * 1024), '\n']}}),
        ('many-submatches', {'pattern': 'x'}, {'file.txt': repeated('x', 20000)}),
    ]:
        add('grep', name, args, files)
    add('grep', 'rgignore-precedence', {'pattern': 'needle'},
        {'.ignore': 'ignored.txt\n', '.rgignore': '!ignored.txt\n', 'ignored.txt': 'needle\n', 'visible.txt': 'needle\n'})
    add('grep', 'nested-ignore-negation', {'pattern': 'needle'},
        {'.ignore': '*.txt\n', 'nested/.ignore': '!included.txt\n', 'nested/included.txt': 'needle\n', 'nested/ignored.txt': 'needle\n'})
    add('grep', 'explicit-symlink-root', {'pattern': 'needle', 'path': 'alias'},
        {'real/file.txt': 'needle\n'}, symlinks={'alias': 'real'})
    add('grep', 'broken-symlink-root', {'pattern': 'needle', 'path': 'alias'}, symlinks={'alias': 'missing'})
    add('grep', 'inaccessible-root', {'pattern': 'needle', 'path': 'private'},
        {'private/file.txt': 'needle\n'}, modes={'private': 0})
    add('grep', 'late-match-beyond-inventory', {'pattern': 'late-needle'}, {'z/wanted.txt': 'late-needle\n'},
        generatedFiles={'directory': 'a', 'count': 20050, 'text': ''})
    add('grep', 'repository-local-private-excludes', {'pattern': 'match-value'},
        {'global-excludes': 'global-untracked.txt\nstaged-global.txt\nglobal-rescued.txt\n',
         '.ignore': '!global-rescued.txt\n!local-rescued.txt\n',
         'global-untracked.txt': 'match-value\n', 'staged-global.txt': 'match-value\n',
         'global-rescued.txt': 'match-value\n', 'local-rescued.txt': 'match-value\n', 'visible.txt': 'match-value\n'},
        gitDirectories=['.'], gitConfig={'core.excludesFile': '{WORKSPACE}/global-excludes'},
        acceptanceClass='intentional-repository-exclusion-safety')
    for tool, pattern in [('grep', 'needle'), ('find', '*.txt')]:
        arguments = {'pattern': pattern}
        files = {'visible.txt': 'needle\n', 'ignored.txt': 'needle\n', '.gitignore': 'ignored.txt\n'}
        add(tool, 'gitignore', arguments, files, gitDirectories=['.'])
        add(tool, 'ignore-outside-repo', arguments, {'visible.txt': 'needle\n', 'ignored.txt': 'needle\n', '.ignore': 'ignored.txt\n'})
        add(tool, 'nested-repositories', arguments,
            {'.gitignore': 'nested/outer-ignored.txt\n', 'nested/outer-ignored.txt': 'needle\n',
             'nested/visible.txt': 'needle\n', 'nested/.gitignore': 'inner-ignored.txt\n', 'nested/inner-ignored.txt': 'needle\n'},
            gitDirectories=['.', 'nested'])
        add(tool, 'symlink-file', arguments, {'target.txt': 'needle\n'}, symlinks={'alias.txt': 'target.txt'})
        add(tool, 'missing-root', {**arguments, 'path': 'missing'})
        add(tool, 'permission', arguments, {'secret/file.txt': 'needle\n'}, modes={'secret': 0})
        add(tool, 'missing-required', {})
        add(tool, 'invalid-pattern-type', {'pattern': []})
        add(tool, 'pre-cancelled', arguments, {'file.txt': 'needle\n'}, preCancelled=True)
    find_files = {'src/a.cs': '', 'src/deep/b.cs': '', 'docs/readme.md': '', 'file name.txt': '',
                  'literal[1].txt': '', 'Upper.CS': '', '.hidden/file.cs': '', '.gitignore': ''}
    for name, pattern in [('basename', '*.cs'), ('recursive', 'src/**/*.cs'), ('brace', '**/*.{cs,md}'),
                          ('character-class', '[a-z]*'), ('escaped-space', 'file\\ name.txt'),
                          ('escaped-class', 'literal\\[1\\].txt'), ('dotfile', '.gitignore'),
                          ('smart-case', '*.CS'), ('invalid-glob', '['), ('flag-like-pattern', '--foo'),
                          ('no-match', '*.absent')]:
        add('find', name, {'pattern': pattern}, find_files)
    add('find', 'absolute-root', {'pattern': '*.cs', 'path': '{WORKSPACE}/src'}, find_files)
    add('find', 'directory-result', {'pattern': 'src'}, find_files)
    add('find', 'broken-link', {'pattern': '*.txt'}, symlinks={'broken.txt': 'missing.txt'})
    add('find', 'file-root', {'pattern': '*', 'path': 'file.txt'}, {'file.txt': ''})
    add('find', 'result-limit', {'pattern': '*.txt', 'limit': 1}, {'one.txt': '', 'two.txt': ''})
    add('find', 'zero-limit', {'pattern': '*.txt', 'limit': 0}, {'one.txt': '', 'two.txt': ''})
    add('find', 'late-match-beyond-inventory', {'pattern': 'wanted.txt', 'limit': 1}, {'z/wanted.txt': ''},
        generatedFiles={'directory': 'a', 'count': 20050, 'text': ''})

    for name, arguments, files in [('default', {}, {'alpha': '', 'beta': ''}),
                                   ('empty', {}, {}), ('dotfiles', {}, {'.hidden': '', 'visible': ''}),
                                   ('case-order', {}, {'B': '', 'a': '', 'A': '', 'b': ''}),
                                   ('unicode-order', {}, {'z': '', 'é': '', 'É': '', 'e': '', 'ä': '', '日本語': ''}),
                                   ('punctuation-order', {}, {'_a': '', '-a': '', '.a': '', 'a': '', 'a1': ''}),
                                   ('exact-limit', {'limit': 2}, {'one': '', 'two': ''}),
                                   ('over-limit', {'limit': 1}, {'one': '', 'two': ''}),
                                   ('zero-limit', {'limit': 0}, {'one': ''}),
                                   ('negative-limit', {'limit': -1}, {'one': ''}),
                                   ('fractional-limit', {'limit': 1.5}, {'one': '', 'two': '', 'three': ''})]:
        add('ls', name, arguments, files)
    add('ls', 'directories', {}, {'file': ''}, directories={'dir': 0o755})
    add('ls', 'relative-root', {'path': 'sub'}, {'sub/file': ''})
    add('ls', 'absolute-root', {'path': '{WORKSPACE}/sub'}, {'sub/file': ''})
    add('ls', 'symlink-directory', {}, directories={'dir': 0o755}, symlinks={'alias': 'dir'})
    add('ls', 'broken-symlink', {}, {'visible': ''}, symlinks={'broken': 'missing'})
    add('ls', 'broken-link-limit', {'limit': 1}, {'b-valid': ''}, symlinks={'a-broken': 'missing'})
    add('ls', 'missing', {'path': 'missing'})
    add('ls', 'file-target', {'path': 'file'}, {'file': ''})
    add('ls', 'permission', {'path': 'secret'}, directories={'secret': 0})
    add('ls', 'invalid-path-type', {'path': []})
    add('ls', 'invalid-limit-type', {'limit': {}})
    add('ls', 'special-fifo', {}, fifos=['pipe'])
    add('ls', 'byte-limit', {}, {f'{n:04d}-' + '界' * 70: '' for n in range(300)})
    add('ls', 'pre-cancelled', {}, {'file': ''}, preCancelled=True)
    return result
