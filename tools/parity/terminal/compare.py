"""Exact terminal-state comparison with transient cursor and loader normalization."""
import base64
import codecs
import copy
import re
from itertools import zip_longest

SPINNER_FRAMES = {"⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"}
HTTP_DOCUMENTATION_PATH_NORMALIZATION = (
    'Only generated Main documentation, Additional docs, and Examples package-root lines in system messages are '
    'canonicalized; raw request bodies remain captured.')
MAX_RENDER_DIFFERENCES = 100


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


def normalize_documentation_install_paths(value):
    if isinstance(value, str):
        return re.sub(r'(?m)^(- Main documentation: |- Additional docs: |- Examples: ).+$',
                      lambda match: match.group(1) + '<documentation-path>', value)
    if isinstance(value, list):
        return [normalize_documentation_install_paths(item) for item in value]
    return value


def canonicalize_documentation_paths_in_requests(requests):
    """Canonicalize only generated package-root documentation lines in system messages."""
    result = copy.deepcopy(requests)
    for request in result:
        body = request.get('body')
        messages = body.get('messages') if isinstance(body, dict) else None
        if not isinstance(messages, list):
            continue
        for message in messages:
            if not isinstance(message, dict) or message.get('role') != 'system':
                continue
            content = message.get('content')
            if isinstance(content, (str, list)):
                message['content'] = normalize_documentation_install_paths(content)
    return result


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
    for diagnosis and preserve the complete byte stream. Compare every settled
    checkpoint and stable render transition at synchronized-output boundaries,
    collapsing adjacent duplicates, hidden cursor coordinates, transient loader
    glyphs, and in-flight Working-indicator frames. Same-Pi calibration shows
    that a fast scripted response can finish before this animated frame is
    captured, so its presence and partial usage footer depend on PTY scheduling.
    Only source-proven concurrent request batches may declare order independent.
    """
    left_frames = left.get('frames', [])
    right_frames = right.get('frames', [])
    state_differences = []
    state_difference_count = 0
    control_boundary_differences = []
    control_boundary_difference_count = 0
    if len(left_frames) != len(right_frames):
        state_difference_count += 1
        state_differences.append(dict(path='$.frames.length', pi=len(left_frames), pisharp=len(right_frames)))
        control_boundary_difference_count += 1
        control_boundary_differences.append(dict(path='$.controlBoundaries.length',
                                                 pi=len(left_frames), pisharp=len(right_frames)))
    for index, (left_frame, right_frame) in enumerate(zip(left_frames, right_frames)):
        state_difference_count += append_difference_sample(
            {'id': left_frame.get('id'), 'state': normalize_terminal_state(left_frame.get('state'))},
            {'id': right_frame.get('id'), 'state': normalize_terminal_state(right_frame.get('state'))},
            f'$.frames[{index}]', state_differences, MAX_RENDER_DIFFERENCES)
        control_boundary_difference_count += append_difference_sample(
            left_frame.get('controls', []), right_frame.get('controls', []),
            f'$.controlBoundaries[{index}]', control_boundary_differences, 20)
    render_differences, render_difference_count, compared_render_frame_count = compare_render_sequences(
        left.get('renders', []), right.get('renders', []))
    left_raw = base64.b64decode(left.get('raw', ''))
    right_raw = base64.b64decode(right.get('raw', ''))
    raw_match = left_raw == right_raw
    raw_left_http = canonicalize_concurrent_requests(left.get('http', []), concurrent_http_groups)
    raw_right_http = canonicalize_concurrent_requests(right.get('http', []), concurrent_http_groups)
    raw_http_differences = list(differences(raw_left_http, raw_right_http, '$.http'))
    left_http = canonicalize_documentation_paths_in_requests(raw_left_http)
    right_http = canonicalize_documentation_paths_in_requests(raw_right_http)
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
    difference_count = state_difference_count + render_difference_count + len(http_differences)
    return dict(match=difference_count == 0,
                terminalMatch=state_difference_count == 0 and render_difference_count == 0,
                rawMatch=raw_match,
                rawDifference=raw_difference,
                httpMatch=not http_differences,
                rawHttpMatch=not raw_http_differences,
                rawHttpDifferenceCount=len(raw_http_differences),
                rawHttpFirstDifferences=raw_http_differences[:20],
                httpNormalization=HTTP_DOCUMENTATION_PATH_NORMALIZATION,
                stateDifferences=state_differences,
                stateDifferenceCount=state_difference_count,
                renderDifferences=render_differences,
                renderDifferenceCount=render_difference_count,
                comparedRenderFrameCount=compared_render_frame_count,
                httpDifferences=http_differences,
                differenceCount=difference_count,
                controlBoundaryDifferenceCount=control_boundary_difference_count,
                controlBoundaryFirstDifferences=control_boundary_differences,
                differences=result_differences)


def append_difference_sample(left, right, path, sample, limit):
    count = 0
    for difference in differences(left, right, path):
        count += 1
        if len(sample) < limit:
            sample.append(difference)
    return count


def normalize_terminal_state(state):
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
    rows = normalized.get('rows')
    if isinstance(rows, int) and rows > 0:
        for buffer in normalized.get('buffers', {}).values():
            normalize_loader_spinner(buffer.get('lines', []), rows)
        normalize_loader_spinner(normalized.get('viewport', []), rows)
    return normalized


def normalize_loader_spinner(lines, rows):
    if not isinstance(lines, list):
        return
    for row_index, line in enumerate(lines):
        if row_index < rows - 5 or not isinstance(line, dict):
            continue
        cells = line.get('cells')
        if not isinstance(cells, list):
            continue
        for column, cell in enumerate(cells[:8]):
            if isinstance(cell, dict) and cell.get('chars') in SPINNER_FRAMES:
                cell['chars'] = '<loader-spinner>'


def json_copy(value):
    if isinstance(value, dict):
        return {key: json_copy(item) for key, item in value.items()}
    if isinstance(value, list):
        return [json_copy(item) for item in value]
    return value


def canonical_render_frames(frames):
    result = []
    result.extend(iter_canonical_render_frames(frames))
    return result


def iter_canonical_render_frames(frames):
    previous = None
    first = True
    for frame in frames:
        if has_in_flight_working_indicator(frame):
            continue
        normalized = normalize_terminal_state(frame)
        if first or normalized != previous:
            yield normalized
        previous = normalized
        first = False


def has_in_flight_working_indicator(state):
    if not isinstance(state, dict):
        return False
    rows = state.get('rows')
    if not isinstance(rows, int) or rows < 5:
        return False
    active = state.get('activeScreen')
    buffer = state.get('buffers', {}).get(active, {})
    lines = buffer.get('lines', []) if isinstance(buffer, dict) else []
    for line in lines:
        if not isinstance(line, dict):
            continue
        cells = line.get('cells', [])
        text = ''.join(cell.get('chars', '') for cell in cells if isinstance(cell, dict))
        if 'Working' in text and text.count('─') >= len(cells) // 2:
            return True
    return False


def compare_render_sequences(left_frames, right_frames):
    """Compare canonical render streams while retaining only a bounded diff sample."""
    missing = object()
    differences_found = []
    total_differences = 0
    left_count = 0
    right_count = 0
    for index, (left, right) in enumerate(zip_longest(
            iter_canonical_render_frames(left_frames),
            iter_canonical_render_frames(right_frames), fillvalue=missing)):
        if left is not missing:
            left_count += 1
        if right is not missing:
            right_count += 1
        if left is missing or right is missing:
            continue
        for difference in differences(left, right, f'$.renders[{index}]'):
            total_differences += 1
            if len(differences_found) < MAX_RENDER_DIFFERENCES:
                differences_found.append(difference)

    if left_count != right_count:
        total_differences += 1
        length_difference = dict(path='$.renders.length', pi=left_count, pisharp=right_count)
        differences_found.insert(0, length_difference)
        del differences_found[MAX_RENDER_DIFFERENCES:]

    return differences_found, total_differences, left_count


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
