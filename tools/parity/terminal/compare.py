"""Exact terminal-state comparison with only non-visible cursor and redraw normalization."""
import base64
import codecs


def differences(left, right, path='$'):
    if type(left) is not type(right):
        yield dict(path=path, pi=left, pisharp=right)
    elif isinstance(left, dict):
        for key in sorted(left.keys() | right.keys()):
            if key not in left or key not in right:
                yield dict(path=f'{path}.{key}', pi=left.get(key), pisharp=right.get(key))
            else:
                yield from differences(left[key], right[key], f'{path}.{key}')
    elif isinstance(left, list):
        if len(left) != len(right):
            yield dict(path=f'{path}.length', pi=len(left), pisharp=len(right))
        for index, (first, second) in enumerate(zip(left, right)):
            yield from differences(first, second, f'{path}[{index}]')
    elif left != right:
        yield dict(path=path, pi=left, pisharp=right)


class ControlTrace:
    def __init__(self):
        self.events = []
        self.pending = bytearray()
        self.kind = None
        self.introducer = None
        self.decoder = codecs.getincrementaldecoder('utf-8')('surrogateescape')

    def write(self, data):
        for character in self.decoder.decode(data):
            byte = ord(character)
            if 0xdc80 <= byte <= 0xdcff:
                byte -= 0xdc00
            encoded = character.encode('utf-8', 'surrogateescape')
            if not self.pending:
                if byte == 0x1b:
                    self.pending.extend(encoded)
                    self.kind = 'escape'
                elif byte in [0x9b, 0x90, 0x98, 0x9d, 0x9e, 0x9f]:
                    self.pending.extend(encoded)
                    self.kind = 'csi' if byte == 0x9b else 'string'
                    self.introducer = byte
                elif byte < 0x20 or 0x7f <= byte <= 0x9f:
                    self.events.append(encoded.hex())
                continue
            self.pending.extend(encoded)
            if self.kind == 'escape':
                if byte == ord('['):
                    self.kind = 'csi'
                elif byte in [ord(value) for value in ']PX^_']:
                    self.kind = 'string'
                    self.introducer = byte
                elif not 0x20 <= byte <= 0x2f:
                    self.finish()
            elif self.kind == 'csi' and 0x40 <= byte <= 0x7e:
                self.finish()
            elif self.kind == 'string' and ((byte == 0x07 and self.introducer in [ord(']'), 0x9d]) or byte == 0x9c or self.pending[-2:] == b'\x1b\\'):
                self.finish()

    def finish(self):
        self.events.append(self.pending.hex())
        self.pending.clear()
        self.kind = None


def compare_products(left, right, concurrent_http_groups=()):
    """Compare exact rendered states and fixture request order; retain raw bytes as evidence.

    PTY output can arrive on either side of a named checkpoint without changing
    the bytes sent or the rendered state. Keep those per-frame control buckets
    for diagnosis and preserve the complete byte stream. Compare every state
    transition at synchronized-output boundaries, collapsing adjacent duplicate
    snapshots and hidden cursor coordinates only. Only source-proven concurrent
    request batches may declare order independent.
    """
    left_frames = left.get('frames', [])
    right_frames = right.get('frames', [])
    state_differences = list(differences(
        [{'id': frame.get('id'), 'state': normalize_hidden_cursor(frame.get('state'))} for frame in left_frames],
        [{'id': frame.get('id'), 'state': normalize_hidden_cursor(frame.get('state'))} for frame in right_frames], '$.frames'))
    left_renders = canonical_render_frames(left.get('renders', []))
    right_renders = canonical_render_frames(right.get('renders', []))
    render_differences = list(differences(left_renders, right_renders, '$.renders'))
    control_boundary_differences = list(differences(
        [frame.get('controls', []) for frame in left_frames],
        [frame.get('controls', []) for frame in right_frames], '$.controlBoundaries'))
    left_raw = base64.b64decode(left.get('raw', ''))
    right_raw = base64.b64decode(right.get('raw', ''))
    raw_match = left_raw == right_raw
    left_http = canonicalize_concurrent_requests(left.get('http', []), concurrent_http_groups)
    right_http = canonicalize_concurrent_requests(right.get('http', []), concurrent_http_groups)
    http_differences = list(differences(left_http, right_http, '$.http'))
    result_differences = list(state_differences)
    result_differences.extend(render_differences)
    raw_difference = None
    if not raw_match:
        first_difference = next((index for index, pair in enumerate(zip(left_raw, right_raw))
                                 if pair[0] != pair[1]), min(len(left_raw), len(right_raw)))
        raw_difference = dict(piBytes=len(left_raw), pisharpBytes=len(right_raw),
                              firstDifferingByte=first_difference)
    result_differences.extend(http_differences)
    return dict(match=not result_differences,
                terminalMatch=not state_differences and not render_differences,
                rawMatch=raw_match,
                rawDifference=raw_difference,
                httpMatch=not http_differences,
                stateDifferences=state_differences,
                renderDifferences=render_differences,
                comparedRenderFrameCount=len(left_renders),
                httpDifferences=http_differences,
                controlBoundaryDifferenceCount=len(control_boundary_differences),
                controlBoundaryFirstDifferences=control_boundary_differences[:20],
                differences=result_differences)


def normalize_hidden_cursor(state):
    if not isinstance(state, dict):
        return state
    normalized = json_copy(state)
    cursor = normalized.get('cursor')
    if isinstance(cursor, dict) and cursor.get('visible') is False:
        cursor.pop('x', None)
        cursor.pop('y', None)
        active_screen = normalized.get('activeScreen')
        active_buffer = normalized.get('buffers', {}).get(active_screen)
        if isinstance(active_buffer, dict):
            active_buffer.pop('cursorX', None)
            active_buffer.pop('cursorY', None)
    return normalized


def json_copy(value):
    if isinstance(value, dict):
        return {key: json_copy(item) for key, item in value.items()}
    if isinstance(value, list):
        return [json_copy(item) for item in value]
    return value


def canonical_render_frames(frames):
    result = []
    for frame in frames:
        normalized = normalize_hidden_cursor(frame)
        if not result or normalized != result[-1]:
            result.append(normalized)
    return result


def canonicalize_concurrent_requests(requests, groups):
    """Treat only declared, source-proven parallel request batches as unordered."""
    result = [dict(request) for request in requests]
    for group in groups:
        expected_requests = group['requests'] if isinstance(group, dict) else group
        occurrence = group.get('occurrence', 1) if isinstance(group, dict) else 1
        expected = sorted((request.get('method'), request.get('path')) for request in expected_requests)
        width = len(expected)
        seen = 0
        for start in range(len(result) - width + 1):
            batch = result[start:start + width]
            actual = sorted((request.get('method'), request.get('path')) for request in batch)
            if actual == expected:
                seen += 1
                if seen == occurrence:
                    result[start:start + width] = sorted(batch, key=lambda request: (
                        request.get('method', ''), request.get('path', ''),
                        repr(sorted(request.items()))))
                    break
    return result
