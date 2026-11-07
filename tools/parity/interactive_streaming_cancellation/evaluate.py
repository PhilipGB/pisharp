#!/usr/bin/env python3
"""Score the interactive streaming/cancellation fixture from captured evidence.

The evaluator reads only the compact ``*.assertions.json.gz`` companions. It
evaluates the frozen functional, temporal and cleanup contract independently for
Pi and PiSharp, binds every capture to the exact source state that produced it,
scans the post-cancellation render stream for forbidden tokens, and maps every
decoded difference to an explicit path/checkpoint predicate. An unmatched
difference is a validation failure, not a generic finding.
"""
import argparse
import copy
import gzip
import hashlib
import json
import re
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'terminal'))
from compare import differences, normalize_terminal_state  # noqa: E402

ROOT = Path(__file__).resolve().parents[3]
BLANKS = {'', None, '<empty-cell>'}
DYNAMIC_PATTERNS = [
    re.compile(r'(?<=Elapsed )\d+(?:\.\d+)?s'),
    re.compile(r'(?<=Took )\d+(?:\.\d+)?s'),
    re.compile(r'\d+(?:\.\d+)?%(?=/)'),
]
CATEGORY_ORDER = ['text', 'style', 'layout']
SCENARIO_POINTS = dict(functional=8, temporal=4, visual=6, cleanup=5)
CLEANUP_SCENARIOS = {'S04', 'S05'}
PROVENANCE_FIELDS = ('sha', 'branch', 'dirty', 'trackedChanges', 'artifact')
SHA_PATTERN = re.compile(r'^[0-9a-f]{40}$')
HASH_PATTERN = re.compile(r'^[0-9a-f]{64}$')


def load_gzip_json(path):
    with gzip.open(path, 'rt', encoding='utf-8') as stream:
        return json.load(stream)


def state_text(state, buffer_name=None):
    """Render visible characters, treating normalized blank cells as spaces."""
    if not isinstance(state, dict):
        return ''
    lines = state.get('viewport', []) if buffer_name is None else \
        state.get('buffers', {}).get(buffer_name, {}).get('lines', [])
    rendered = []
    for line in lines:
        chars = ''.join(
            ' ' if (cell.get('chars') if isinstance(cell, dict) else None) in BLANKS
            else cell.get('chars', '')
            for cell in line.get('cells', []) if isinstance(cell, dict))
        rendered.append(chars.rstrip())
    return '\n'.join(rendered).strip('\n')


def collapsed(text):
    return re.sub(r'\s+', ' ', text).strip()


def mask_dynamic(lines, marker_width=16):
    """Mask wall-clock durations and partial-stream usage percentages.

    A fixed-width marker keeps two runs that print values of different width
    aligned. Prefix and suffix cells, including their styles, are preserved;
    only the measured value becomes a canonical marker.
    """
    for line in lines:
        if not isinstance(line, dict) or not isinstance(line.get('cells'), list):
            continue
        cells = line['cells']
        text = ''.join(' ' if (cell.get('chars') if isinstance(cell, dict) else None) in BLANKS
                       else cell.get('chars', '')
                       for cell in cells if isinstance(cell, dict))
        matches = []
        for pattern in DYNAMIC_PATTERNS:
            matches.extend((match.start(), match.end()) for match in pattern.finditer(text))
        if not matches:
            continue
        matches.sort()
        blank = next((cell for cell in reversed(cells)
                      if isinstance(cell, dict) and cell.get('chars') in BLANKS), None)
        rebuilt = []
        cursor = 0
        for start, end in matches:
            if start < cursor:
                continue
            rebuilt.extend(cells[cursor:start])
            marker = copy.deepcopy(cells[start]) if start < len(cells) else dict(chars='<dynamic>',
                                                                                width=1)
            marker['chars'] = '<dynamic>'
            marker['width'] = 1
            rebuilt.append(marker)
            for _ in range(marker_width - 1):
                padding = copy.deepcopy(blank) if blank is not None else dict(chars='<empty-cell>',
                                                                              width=1)
                padding['chars'] = '<empty-cell>'
                rebuilt.append(padding)
            cursor = end
        rebuilt.extend(cells[cursor:])
        if len(rebuilt) < len(cells):
            padding = copy.deepcopy(blank) if blank is not None else dict(chars='<empty-cell>',
                                                                          width=1)
            padding['chars'] = '<empty-cell>'
            rebuilt.extend(copy.deepcopy(padding) for _ in range(len(cells) - len(rebuilt)))
        line['cells'] = rebuilt[:len(cells)]
    return lines


def normalized_state(state):
    if not isinstance(state, dict):
        return state
    result = normalize_terminal_state(state)
    for buffer in result.get('buffers', {}).values():
        mask_dynamic(buffer.get('lines', []))
    mask_dynamic(result.get('viewport', []))
    return result


def state_differences(left, right):
    return list(differences(normalized_state(left), normalized_state(right)))


def diff_category(path):
    if path.endswith('.chars'):
        return 'text'
    if any(token in path for token in ('.foreground', '.background', '.attributes',
                                       '.underline', '.hyperlink')):
        return 'style'
    if path.endswith('.wrapped'):
        return 'style'
    return 'layout'


def by_checkpoint(evidence):
    return {frame['id']: frame['state'] for frame in evidence.get('frames', [])}


def checkpoint_order(evidence):
    return [frame['id'] for frame in evidence.get('frames', [])]


def final_state(evidence):
    frames = evidence.get('frames', [])
    for frame in reversed(frames):
        if frame.get('id') == 'exit':
            return frame['state']
    return frames[-1]['state'] if frames else {}


def final_transcript_text(evidence):
    state = final_state(evidence)
    text = state_text(state, 'normal')
    if not text:
        text = state_text(state)
    return text


def all_visible_text(evidence):
    states = [frame['state'] for frame in evidence.get('frames', [])]
    return collapsed('\n'.join(state_text(state) for state in states))


def chat_requests(evidence):
    return [request for request in evidence.get('http', [])
            if request.get('method') == 'POST'
            and str(request.get('path', '')).endswith('/chat/completions')]


def assistant_tool_call_ids(evidence):
    ids = []
    for request in chat_requests(evidence):
        for message in request.get('body', {}).get('messages', []):
            if message.get('role') != 'assistant':
                continue
            for call in message.get('tool_calls', []):
                ids.append(call.get('id'))
    return ids


def tool_messages(evidence):
    messages = []
    for request in chat_requests(evidence):
        for message in request.get('body', {}).get('messages', []):
            if message.get('role') == 'tool':
                messages.append(message)
    return messages


def process_by_checkpoint(evidence):
    return {entry['id']: entry.get('processes', {})
            for entry in evidence.get('processByFrame', [])}


def process_summary(processes):
    """Drop volatile PIDs while retaining whether each recorded process lived."""
    return {name: dict(state=entry.get('state'))
            for name, entry in sorted(processes.items())}


def product_provenance(evidence, product):
    value = (evidence.get(product) or {}).get('provenance')
    if not isinstance(value, dict):
        return None
    result = {field: value.get(field) for field in PROVENANCE_FIELDS}
    artifact = result.get('artifact')
    if isinstance(artifact, dict):
        # The raw publish manifest keeps the absolute build command; committed
        # evidence stays machine-independent and binds by source/artifact hash.
        result['artifact'] = {key: item for key, item in artifact.items()
                              if key != 'buildCommand'}
    return result


def artifact_valid(value):
    if not isinstance(value, dict):
        return False
    if not HASH_PATTERN.fullmatch(str(value.get('sha256', ''))):
        return False
    for field in ('depsSha256', 'runtimeConfigSha256'):
        if not HASH_PATTERN.fullmatch(str(value.get(field, ''))):
            return False
    return True


def provenance_valid(value):
    return (isinstance(value, dict)
            and bool(SHA_PATTERN.fullmatch(str(value.get('sha', ''))))
            and isinstance(value.get('dirty'), bool)
            and isinstance(value.get('trackedChanges'), list)
            and value['dirty'] == bool(value['trackedChanges'])
            and (value.get('artifact') is None or artifact_valid(value['artifact'])))


def capture_bundle(calibration, comparison):
    return dict(
        calibration=dict(pi=product_provenance(calibration, 'pi'),
                         pisharp=product_provenance(calibration, 'pisharp')),
        comparison=dict(pi=product_provenance(comparison, 'pi'),
                        pisharp=product_provenance(comparison, 'pisharp')),
    )


def provenance_json(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'))


def scenario_provenance_checks(captures):
    """Verify that every case ran the expected source state for each role."""
    errors = []
    pi_states = set()
    pisharp_states = set()
    for case_id, capture in captures.items():
        calibration = capture['calibration']
        comparison = capture['comparison']
        for label in ('pi', 'pisharp'):
            for name, value in (('calibration.pi', calibration['pi']),
                                ('calibration.pisharp', calibration['pisharp']),
                                ('comparison.pi', comparison['pi']),
                                ('comparison.pisharp', comparison['pisharp'])):
                if name.endswith(label) and not provenance_valid(value):
                    errors.append(f'{case_id} {name} provenance is incomplete or inconsistent')
        if calibration['pisharp'] != calibration['pi']:
            errors.append(f'{case_id} calibration second run is not the Pi source state')
        pi_states.add(provenance_json(calibration['pi']))
        pi_states.add(provenance_json(calibration['pisharp']))
        pi_states.add(provenance_json(comparison['pi']))
        pisharp_states.add(provenance_json(comparison['pisharp']))
    if len(pi_states) != 1:
        errors.append(f'Pi source state changed between captures: {sorted(pi_states)}')
    if len(pisharp_states) != 1:
        errors.append(f'PiSharp source state changed between captures: {sorted(pisharp_states)}')
    return dict(
        consistent=not errors,
        errors=errors,
        pi=sorted(json.loads(value) for value in pi_states),
        pisharp=sorted(json.loads(value) for value in pisharp_states),
    )


def calibration_differences(first_evidence, second_evidence, scenario):
    """Compare two independent Pi runs through the declared normalization."""
    first = by_checkpoint(first_evidence)
    second = by_checkpoint(second_evidence)
    found = []
    for checkpoint in scenario['checkpoints']:
        if checkpoint not in first or checkpoint not in second:
            found.append(dict(checkpoint=checkpoint, path='$.missingCheckpoint',
                              left=checkpoint in first, right=checkpoint in second))
            continue
        for difference in state_differences(first[checkpoint], second[checkpoint]):
            found.append(dict(checkpoint=checkpoint, path=difference['path'],
                              left=difference.get('pi'), right=difference.get('pisharp')))
    for label, evidence in (('first', first_evidence), ('second', second_evidence)):
        if evidence.get('scenarioError'):
            found.append(dict(checkpoint='scenario', path='$.scenarioError',
                              left=label, right=evidence['scenarioError']))
    first_processes = {key: process_summary(value)
                       for key, value in process_by_checkpoint(first_evidence).items()}
    second_processes = {key: process_summary(value)
                        for key, value in process_by_checkpoint(second_evidence).items()}
    if first_processes != second_processes:
        found.append(dict(checkpoint='process', path='$.processByFrame',
                          left=first_processes, right=second_processes))
    def forbidden_signature(value):
        return {key: value.get(key) for key in
                ('required', 'scanned', 'tokens', 'counts', 'occurrenceCount', 'valid')}
    first_forbidden = forbidden_signature(forbidden_check(first_evidence, scenario))
    second_forbidden = forbidden_signature(forbidden_check(second_evidence, scenario))
    if first_forbidden != second_forbidden:
        found.append(dict(checkpoint='stream', path='$.forbiddenStream',
                          left=first_forbidden, right=second_forbidden))
    first_requests = len(chat_requests(first_evidence))
    second_requests = len(chat_requests(second_evidence))
    if first_requests != second_requests:
        found.append(dict(checkpoint='http', path='$.http.chatRequestCount',
                          left=first_requests, right=second_requests))
    first_calls = assistant_tool_call_ids(first_evidence)
    second_calls = assistant_tool_call_ids(second_evidence)
    if first_calls != second_calls:
        found.append(dict(checkpoint='http', path='$.http.assistantToolCallIds',
                          left=first_calls, right=second_calls))
    return found


def compare_products(pi_evidence, pisharp_evidence, scenario):
    pi_frames = by_checkpoint(pi_evidence)
    ps_frames = by_checkpoint(pisharp_evidence)
    records = []
    for checkpoint in scenario['checkpoints']:
        if checkpoint not in pi_frames or checkpoint not in ps_frames:
            records.append(dict(checkpoint=checkpoint, path='$.missingCheckpoint',
                                pi=checkpoint in pi_frames, pisharp=checkpoint in ps_frames,
                                category='layout'))
            continue
        for difference in state_differences(pi_frames[checkpoint], ps_frames[checkpoint]):
            records.append(dict(checkpoint=checkpoint, category=diff_category(difference['path']),
                                path=difference['path'], pi=difference.get('pi'),
                                pisharp=difference.get('pisharp')))
    return records


def absence_failures(evidence, scenario):
    frames = by_checkpoint(evidence)
    failures = []
    for checkpoint, tokens in scenario.get('absenceByCheckpoint', {}).items():
        if checkpoint not in frames:
            failures.append(dict(checkpoint=checkpoint, reason='missing checkpoint'))
            continue
        text = collapsed(state_text(normalized_state(frames[checkpoint])))
        for token in tokens:
            if token in text:
                failures.append(dict(checkpoint=checkpoint, token=token))
    return failures


def content_check(evidence, scenario):
    state = normalized_state(final_state(evidence))
    final_text = state_text(state, 'normal') or state_text(state)
    text = collapsed(final_text)
    missing = [value for value in scenario.get('requiredText', [])
               if collapsed(value) not in text]
    duplicates = {value: text.count(collapsed(value))
                  for value in scenario.get('finalTextExactOnce', [])
                  if text.count(collapsed(value)) != 1}
    return dict(missing=missing, duplicates=duplicates, finalText=final_text)


def request_check(evidence, scenario):
    requests = chat_requests(evidence)
    calls = assistant_tool_call_ids(evidence)
    per_request_calls = []
    for request in requests:
        ids = []
        for message in request.get('body', {}).get('messages', []):
            if message.get('role') == 'assistant':
                ids.extend(call.get('id') for call in message.get('tool_calls', []))
        per_request_calls.append(ids)
    duplicate_call = any(len(ids) != len(set(ids)) for ids in per_request_calls)
    result = dict(count=len(requests), expected=scenario.get('expectedModelRequests'),
                  countMatch=len(requests) == scenario.get('expectedModelRequests'),
                  assistantToolCallIds=calls, duplicateToolCall=duplicate_call)
    if 'toolResult' in scenario:
        messages = tool_messages(evidence)
        ids = [message.get('tool_call_id') for message in messages]
        result['toolResults'] = len(messages)
        result['toolResultIds'] = ids
        result['toolResultExact'] = (len(set(ids)) == 1 and len(ids) == 1
                                     and messages[0].get('content') == scenario['toolResult'])
    return result


def idle_check(evidence, scenario):
    state = by_checkpoint(evidence).get(scenario['idleCheckpoint'])
    return dict(missing=state is None,
                hasWorking=bool(state) and 'Working' in state_text(normalized_state(state)))


def subsequent_check(evidence, scenario):
    if 'secondPromptResponse' in scenario:
        return collapsed(scenario['secondPromptResponse']) in collapsed(final_transcript_text(evidence))
    exit_state = final_state(evidence)
    return (exit_state.get('exitCode') == 0
            and bool(exit_state.get('cursor', {}).get('visible')))


def cleanup_check(evidence, scenario):
    processes = process_by_checkpoint(evidence)
    cleanup = processes.get(scenario.get('cleanupCheckpoint'), {})
    initial = processes.get(scenario.get('processCheckpoint'), {})
    exit_state = by_checkpoint(evidence).get('exit', {})
    applies = 'cleanupCheckpoint' in scenario
    return dict(
        initialProcesses=process_summary(initial),
        cleanupProcesses=process_summary(cleanup),
        processesGone=(bool(cleanup) and all(entry.get('state') is None
                                             for entry in cleanup.values())) if applies else None,
        normalScreenRestored=exit_state.get('activeScreen') == 'normal',
        cursorVisible=bool(exit_state.get('cursor', {}).get('visible')),
        synchronizedOutputOff=not exit_state.get('modes', {}).get('private', {}).get('2026', False),
    )


def forbidden_check(evidence, scenario):
    tokens = scenario.get('forbiddenStreamTokens', [])
    value = evidence.get('forbiddenStream')
    if not tokens:
        return dict(required=False, scanned=None, valid=True, tokens=[], counts={},
                    occurrenceCount=0, occurrences=[])
    if not isinstance(value, dict):
        return dict(required=True, scanned=False, valid=False, tokens=tokens, counts={},
                    occurrenceCount=None, occurrences=[])
    counts = value.get('counts', {})
    occurrence_count = value.get('occurrenceCount', value.get('total', 0))
    valid = (value.get('scanned') is True
             and value.get('tokens') == tokens
             and occurrence_count == 0
             and all(counts.get(token, 0) == 0 for token in tokens))
    return dict(required=True, **value, valid=valid)


def evaluate_contract(scenario_id, scenario, evidence):
    """Evaluate the frozen functional/temporal/cleanup contract for one product."""
    order = checkpoint_order(evidence)
    errors = evidence.get('scenarioError')
    complete = not errors and order == scenario['checkpoints']
    absences = absence_failures(evidence, scenario)
    content = content_check(evidence, scenario)
    requests = request_check(evidence, scenario)
    idle = idle_check(evidence, scenario)
    subsequent = subsequent_check(evidence, scenario)
    processes = cleanup_check(evidence, scenario)
    forbidden = forbidden_check(evidence, scenario)
    content_ok = not content['missing'] and not content['duplicates']
    requests_ok = (requests['countMatch'] and requests.get('toolResultExact', True)
                   and not requests['duplicateToolCall'])
    transitions = complete and not absences
    idle_ok = not idle['missing'] and not idle['hasWorking']
    process_ok = processes['processesGone'] is True if 'cleanupCheckpoint' in scenario else True
    forbidden_ok = forbidden['valid']
    subsequent_ok = bool(subsequent)

    functional_checks = dict(
        transitions=transitions,
        content=content_ok,
        finalState=transitions and requests_ok and idle_ok and process_ok,
        subsequent=subsequent_ok,
    )
    temporal_checks = dict(complete=complete, absence=transitions, idle=idle_ok,
                           requests=requests_ok)
    cleanup_checks = None
    cancelled_has_working = {}
    if scenario_id in CLEANUP_SCENARIOS:
        frames = by_checkpoint(evidence)
        cancelled = frames.get(scenario['cancelledCheckpoint'])
        cancelled_text = state_text(normalized_state(cancelled)) if cancelled else ''
        cancelled_has_working = {scenario['cancelledCheckpoint']: (not cancelled)
                                 or 'Working' in cancelled_text}
        cleanup_checks = dict(
            cancellationSettled=not any(cancelled_has_working.values()),
            noLateOutput=not absences and forbidden_ok,
            terminalRestored=(processes['normalScreenRestored'] and processes['cursorVisible']
                              and processes['synchronizedOutputOff']),
            cleanupTarget=(processes['processesGone'] is True
                           if 'cleanupCheckpoint' in scenario else not idle['hasWorking']),
            subsequentPrompt=subsequent_ok,
        )
    functional = sum(2 for value in functional_checks.values() if value)
    temporal = sum(1 for value in temporal_checks.values() if value)
    cleanup = sum(1 for value in cleanup_checks.values() if value) if cleanup_checks else None
    passes = (all(functional_checks.values()) and all(temporal_checks.values())
              and (cleanup_checks is None or all(cleanup_checks.values())))
    return dict(
        complete=complete,
        errors=errors,
        checkpointOrder=order,
        absenceFailures=absences,
        content=[content],
        requests=[requests],
        idleHasWorking=dict(idle=idle['hasWorking']),
        idleMissing=dict(idle=idle['missing']),
        cancelledHasWorking=cancelled_has_working,
        subsequentPrompt=dict(subsequent=subsequent_ok),
        cleanupEvidence=processes,
        forbiddenStream=forbidden,
        functionalChecks=functional_checks,
        temporalChecks=temporal_checks,
        cleanupChecks=cleanup_checks,
        functional=functional,
        temporal=temporal,
        cleanup=cleanup,
        passes=passes,
    )


def checkpoint_index(scenario, checkpoint):
    try:
        return scenario['checkpoints'].index(checkpoint)
    except ValueError:
        return None


def in_checkpoint_range(scenario, checkpoint, start, end='exit'):
    index = checkpoint_index(scenario, checkpoint)
    first = checkpoint_index(scenario, start)
    last = checkpoint_index(scenario, end)
    return (index is not None and first is not None and last is not None
            and first <= index <= last)


S01_BUILTIN_PATH = re.compile(
    r'\$\.(?:buffers\.(?:alternate|normal)\.lines\[(?:7|8)\]|viewport\[(?:7|8)\])'
    r'\.cells\[[3-7]\]\.foreground\.(?:mode|value)')
S02_ITALIC_PATH = re.compile(
    r'\$\.(?:buffers\.(?:alternate|normal)\.lines\[\d+\]|viewport\[\d+\])'
    r'\.cells\[\d+\]\.attributes\.italic')
S02_STATUS_PATH = re.compile(
    r'\$\.(?:buffers\.(?:alternate|normal)\.lines\[(?:19|21|35|37)\]|viewport\[(?:19|21|35|37)\])'
    r'\.cells\[\d+\]\.foreground\.value')
S02_TEXT_PATH = re.compile(
    r'\$\.(?:buffers\.(?:alternate|normal)\.lines\[\d+\]|viewport\[\d+\])'
    r'\.cells\[\d+\]\.chars')
S02_LAYOUT_PATH = re.compile(
    r'\$\.(?:buffers\.(?:alternate|normal)\.cursor[XY]|cursor\.(?:x|y|visible))')
S02_SHIFT_STYLE_PATH = re.compile(
    r'\$\.(?:buffers\.(?:alternate|normal)\.lines\[\d+\]|viewport\[\d+\])'
    r'\.cells\[\d+\]\.(?:foreground|background|attributes)(?:\.(?:mode|value|bold|dim|italic|underline|blink|inverse|invisible|strikethrough|overline))?')
S05_TEXT_PATH = re.compile(
    r'\$\.(?:buffers\.(?:alternate|normal)\.lines\[(?:10|11|16)\]|viewport\[(?:10|11|16)\])'
    r'\.cells\[\d+\]\.chars')
S05_STYLE_PATH = re.compile(
    r'\$\.(?:buffers\.(?:alternate|normal)\.lines\[(?:10|11|16)\]|viewport\[(?:10|11|16)\])'
    r'\.cells\[\d+\]\.(?:foreground|background|attributes)(?:\.(?:mode|value|bold|dim|italic|underline|blink|inverse|invisible|strikethrough|overline))?')


def match_finding(scenario_id, scenario, difference):
    """Map a decoded difference to one explicit finding, or None."""
    checkpoint = difference['checkpoint']
    category = difference['category']
    path = str(difference.get('path', ''))
    pi = difference.get('pi')
    pisharp = difference.get('pisharp')
    if scenario_id == 'S01':
        if (category == 'style' and in_checkpoint_range(scenario, checkpoint,
                                                         's01-04-incomplete-code-block')
                and S01_BUILTIN_PATH.fullmatch(path)
                and (pi, pisharp) in {('rgb', 'default'), (10983639, -1)}):
            return 'S01-code-syntax-builtin-highlight'
    if scenario_id == 'S02':
        if (in_checkpoint_range(scenario, checkpoint, 's02-02-reasoning-begins')
                and S02_ITALIC_PATH.fullmatch(path)
                and (pi, pisharp) == (True, False)):
            return 'S02-reasoning-italic-style'
        if (checkpoint == 's02-01-before-reasoning'
                and S02_STATUS_PATH.fullmatch(path)
                and (pi, pisharp) == (6391244, 7108219)):
            return 'S02-reasoning-status-style'
        if in_checkpoint_range(scenario, checkpoint, 's02-02-reasoning-begins'):
            if category == 'text' and S02_TEXT_PATH.fullmatch(path):
                return 'S02-reasoning-block-layout'
            if category == 'layout' and S02_LAYOUT_PATH.fullmatch(path):
                return 'S02-reasoning-block-layout'
            if category == 'style' and S02_SHIFT_STYLE_PATH.fullmatch(path):
                return 'S02-reasoning-block-layout'
    if scenario_id == 'S05' and in_checkpoint_range(scenario, checkpoint,
                                                     's05-05-tool-cancellation-settles'):
        if category == 'text' and S05_TEXT_PATH.fullmatch(path):
            return 'S05-tool-cancel-error-wording'
        if category == 'style' and S05_STYLE_PATH.fullmatch(path):
            return 'S05-tool-cancel-error-style'
    return None


FINDINGS = {
    'S01-code-syntax-builtin-highlight': dict(
        severity='P2', category='style',
        piObservation='Pi colours the Python builtin name print with the syntax keyword colour (rgb 10983639).',
        pisharpObservation='PiSharp leaves print at the default foreground; string literals match.',
        source='src/PiSharp.Cli/Tui/Rendering/TerminalSyntaxHighlighter.cs',
        correction='Add Python builtin-name highlighting to the syntax token table and cover it with a cell-style regression.',
        reproduction='python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi '
                     '--pisharp src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll '
                     '--fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s01-text-streaming.json '
                     '--case 80x24-dark-fullscreen --output artifacts/parity/repro-s01',
        evidence='comparison.json S01 style cells for print; ui-report.json checkpoint text'),
    'S02-reasoning-block-layout': dict(
        severity='P1', category='layout/text/style',
        piObservation='Pi keeps one reasoning section, renders one Thinking... placeholder while hidden, and restores the joined reasoning block without extra rows.',
        pisharpObservation='PiSharp splits reasoning chunks around toggle status lines, shows two Thinking... placeholders, and leaves extra blank/indented rows; the final transcript is shifted by three rows.',
        source='src/PiSharp.Cli/Tui/Rendering/TerminalConversationPresentation.cs',
        correction='Collapse streamed reasoning into one block, keep a single hidden placeholder, and re-render the block in place when visibility changes.',
        reproduction='python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi '
                     '--pisharp src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll '
                     '--fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s02-reasoning-streaming.json '
                     '--case 80x24-dark-fullscreen --output artifacts/parity/repro-s02',
        evidence='comparison.json S02 text/layout/style differences from s02-02 through exit; ui-report.json checkpoint text'),
    'S02-reasoning-italic-style': dict(
        severity='P2', category='style',
        piObservation='Pi renders reasoning text with the italic attribute.',
        pisharpObservation='PiSharp renders reasoning text without italic.',
        source='src/PiSharp.Cli/Tui/Rendering/TerminalConversationPresentation.cs',
        correction='Apply the reasoning/thinking italic style to every reasoning cell and add a styled-frame regression.',
        reproduction='python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi '
                     '--pisharp src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll '
                     '--fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s02-reasoning-streaming.json '
                     '--case 80x24-dark-fullscreen --output artifacts/parity/repro-s02',
        evidence='comparison.json S02 .attributes.italic differences at every visible reasoning checkpoint'),
    'S02-reasoning-status-style': dict(
        severity='P2', category='style',
        piObservation='For a reasoning-capable model Pi draws the Working rule with rgb 6391244.',
        pisharpObservation='PiSharp keeps the non-reasoning Working rule colour rgb 7108219.',
        source='src/PiSharp.Cli/Tui/Rendering/TerminalScreen.cs',
        correction='Select the reasoning-model Working status colour and pin it in a status-style test.',
        reproduction='python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi '
                     '--pisharp src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll '
                     '--fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s02-reasoning-streaming.json '
                     '--case 80x24-dark-fullscreen --output artifacts/parity/repro-s02',
        evidence='comparison.json S02 s02-01-before-reasoning Working-rule foreground cells'),
    'S05-tool-cancel-error-wording': dict(
        severity='P2', category='text',
        piObservation='Pi prints the tool-level Command aborted row and then Error: This operation was aborted.',
        pisharpObservation='PiSharp prints the same Command aborted row but the following error line is Operation aborted.',
        source='src/PiSharp.Cli/Tui/Rendering/TerminalConversationPresentation.cs',
        correction="Render the cancelled tool error with Pi's Error: prefix and abort wording; add a checkpoint assertion.",
        reproduction='python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi '
                     '--pisharp src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll '
                     '--fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s05-tool-cancellation.json '
                     '--case 80x24-dark-fullscreen --output artifacts/parity/repro-s05',
        evidence='comparison.json S05 text differences from s05-05 through exit'),
    'S05-tool-cancel-error-style': dict(
        severity='P2', category='style',
        piObservation='Pi applies the tool error styling to the Error: This operation was aborted row.',
        pisharpObservation='PiSharp applies error styling to Operation aborted, but the text span and colour runs differ with the wording.',
        source='src/PiSharp.Cli/Tui/Rendering/TerminalConversationPresentation.cs',
        correction='Render the corrected cancellation error through the same tool-error style path as Pi.',
        reproduction='python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi '
                     '--pisharp src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll '
                     '--fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s05-tool-cancellation.json '
                     '--case 80x24-dark-fullscreen --output artifacts/parity/repro-s05',
        evidence='comparison.json S05 style differences from s05-05 through exit'),
}


def score_scenario(scenario_id, scenario, comparison_by_case, calibration_by_case,
                   calibration_details, pi_contract_by_case, pisharp_contract_by_case,
                   provenance_checks_by_case, unmatched_by_case):
    errors = []
    if not all(not value for value in calibration_by_case.values()):
        errors.append('same-Pi calibration failed')
    if not all(item['consistent'] for item in provenance_checks_by_case.values()):
        errors.append('capture provenance is inconsistent or incomplete')
    if not all(item['passes'] for item in pi_contract_by_case.values()):
        errors.append('Pi failed the frozen contract')
    if not all(item['complete'] for item in pisharp_contract_by_case.values()):
        errors.append('PiSharp did not reach every named checkpoint')
    if not all(item['complete'] for item in pi_contract_by_case.values()):
        errors.append('Pi did not reach every named checkpoint')
    if any(unmatched_by_case.values()):
        errors.append('unmatched difference has no explicit finding predicate')
    if scenario_id in CLEANUP_SCENARIOS:
        for contract_group in (pi_contract_by_case, pisharp_contract_by_case):
            for contract in contract_group.values():
                forbidden = contract.get('forbiddenStream') or {}
                if forbidden.get('required') and forbidden.get('scanned') is not True:
                    errors.append('post-cancellation stream scan evidence is missing')
                    break
    if 'cleanupCheckpoint' in scenario:
        for contract_group in (pi_contract_by_case, pisharp_contract_by_case):
            for contract in contract_group.values():
                evidence = contract.get('cleanupEvidence') or {}
                if (not evidence.get('initialProcesses')
                        or not evidence.get('cleanupProcesses')):
                    errors.append('process cleanup evidence is missing')
                    break
    errors = list(dict.fromkeys(errors))
    assessable = not errors

    visual_by_size = {}
    for dimension in scenario['dims']:
        case_id = dimension['id']
        case_diffs = comparison_by_case.get(case_id, [])
        categories = {category: sum(1 for diff in case_diffs if diff['category'] == category)
                      for category in CATEGORY_ORDER}
        visual_by_size[case_id] = dict(
            text=1 if categories['text'] == 0 else 0,
            style=1 if categories['style'] == 0 else 0,
            layout=1 if categories['layout'] == 0 else 0,
            differences=categories,
        )
    visual = sum(sum(value for key, value in scores.items() if key != 'differences')
                 for scores in visual_by_size.values())

    pisharp_contracts = [pisharp_contract_by_case[dimension['id']]
                         for dimension in scenario['dims']]
    functional = min(item['functional'] for item in pisharp_contracts)
    temporal = min(item['temporal'] for item in pisharp_contracts)
    cleanup = None
    if scenario_id in CLEANUP_SCENARIOS:
        cleanup = min(item['cleanup'] for item in pisharp_contracts if item['cleanup'] is not None)
    maximum = (SCENARIO_POINTS['functional'] + SCENARIO_POINTS['temporal']
               + SCENARIO_POINTS['visual']
               + (SCENARIO_POINTS['cleanup'] if cleanup is not None else 0))
    subtotal = functional + temporal + visual + (cleanup or 0) if assessable else None

    finding_ids = sorted({diff['findingId'] for case_diffs in comparison_by_case.values()
                          for diff in case_diffs if diff.get('findingId')})
    unmatched = [dict(case=case, **diff)
                 for case, diffs in unmatched_by_case.items() for diff in diffs]
    return dict(
        id=scenario_id, title=scenario['title'], fixture=scenario['fixture'],
        calibrates=all(not value for value in calibration_by_case.values()),
        assessable=assessable,
        validationErrors=errors,
        functional=functional if assessable else None,
        temporal=temporal if assessable else None,
        visual=visual if assessable else None,
        cleanup=cleanup if assessable else None,
        visualBySize=visual_by_size,
        subtotal=subtotal,
        maximum=maximum,
        calibration=calibration_by_case,
        calibrationDifferences=calibration_details,
        captures=provenance_checks_by_case,
        contracts=dict(pi={case: pi_contract_by_case[case] for case in pi_contract_by_case},
                       pisharp={case: pisharp_contract_by_case[case]
                                for case in pisharp_contract_by_case}),
        unmatchedDifferences=unmatched,
        findings=finding_ids,
    )


def build_evidence(manifest, artifacts, revisions):
    dimensions = manifest['dimensions']
    scenario_reports = []
    comparison_report = {}
    ui_scenarios = []
    all_findings = {}
    unassessable = []
    fixture_checksums = {}
    total = 0
    assessed_maximum = 0
    category_totals = dict(functional=0, temporal=0, visual=0, cleanup=0)
    validation_errors = []
    all_pi_states = set()
    all_pisharp_states = set()
    total_unmatched = 0
    provenance_errors = []

    for scenario_id, scenario in manifest['scenarios'].items():
        scenario = dict(scenario)
        scenario['dims'] = dimensions
        fixture_path = (Path(__file__).resolve().parents[1] / 'terminal' / 'fixtures'
                        / manifest['fixture'] / scenario['fixture'])
        fixture_checksums[scenario['fixture']] = hashlib.sha256(fixture_path.read_bytes()).hexdigest()
        calibration_by_case = {}
        comparison_by_case = {}
        calibration_details = {}
        pi_contracts = {}
        pisharp_contracts = {}
        provenance_checks = {}
        unmatched_by_case = {}
        captures = {}
        comparison_evidence = {}

        for dimension in dimensions:
            case_id = dimension['id']
            stem = scenario['fixture'].removesuffix('.json')
            calibration = load_gzip_json(artifacts / 'calibration' / stem /
                                         f'{case_id}.assertions.json.gz')
            comparison = load_gzip_json(artifacts / 'comparison' / stem /
                                        f'{case_id}.assertions.json.gz')
            captures[case_id] = capture_bundle(calibration, comparison)
            calibration_diff = calibration_differences(calibration['pi'], calibration['pisharp'],
                                                       scenario)
            calibration_by_case[case_id] = bool(calibration_diff)
            calibration_details[case_id] = calibration_diff
            diffs = compare_products(comparison['pi'], comparison['pisharp'], scenario)
            for diff in diffs:
                diff['findingId'] = match_finding(scenario_id, scenario, diff)
            unmatched_by_case[case_id] = [diff for diff in diffs if not diff.get('findingId')]
            comparison_by_case[case_id] = diffs
            comparison_evidence[case_id] = comparison
            pi_contracts[case_id] = evaluate_contract(scenario_id, scenario, comparison['pi'])
            pisharp_contracts[case_id] = evaluate_contract(scenario_id, scenario,
                                                           comparison['pisharp'])
        provenance = scenario_provenance_checks(captures)
        provenance_errors.extend(f'{scenario_id}: {error}' for error in provenance['errors'])
        for case_id, capture in captures.items():
            provenance_checks[case_id] = dict(
                consistent=provenance['consistent'],
                errors=provenance['errors'],
                calibration=capture['calibration'],
                comparison=capture['comparison'],
            )
        for value in provenance['pi']:
            all_pi_states.add(provenance_json(value))
        for value in provenance['pisharp']:
            all_pisharp_states.add(provenance_json(value))

        result = score_scenario(scenario_id, scenario, comparison_by_case, calibration_by_case,
                                calibration_details, pi_contracts, pisharp_contracts,
                                provenance_checks, unmatched_by_case)
        total_unmatched += sum(len(value) for value in unmatched_by_case.values())
        if not result['assessable']:
            unassessable.append(dict(scenario=scenario_id, reason='; '.join(result['validationErrors']),
                                     cases={case: [dict(checkpoint=item['checkpoint'],
                                                        path=item['path'])
                                                   for item in items[:10]]
                                            for case, items in calibration_details.items() if items}))
            validation_errors.append(dict(scenario=scenario_id,
                                          errors=result['validationErrors']))
        else:
            total += result['subtotal']
            assessed_maximum += result['maximum']
            category_totals['functional'] += result['functional']
            category_totals['temporal'] += result['temporal']
            category_totals['visual'] += result['visual']
            category_totals['cleanup'] += result['cleanup'] or 0
            for finding_id in result['findings']:
                spec = FINDINGS.get(finding_id)
                if spec is None:
                    raise RuntimeError(f'Missing finding specification: {finding_id}')
                matches = [diff for diffs in comparison_by_case.values() for diff in diffs
                           if diff.get('findingId') == finding_id]
                all_findings.setdefault(finding_id, dict(
                    id=finding_id,
                    scenario=scenario_id,
                    cases=sorted({case for case, diffs in comparison_by_case.items()
                                  if any(diff.get('findingId') == finding_id for diff in diffs)}),
                    checkpoints=sorted({diff['checkpoint'] for diff in matches}),
                    differenceCount=len(matches),
                    samplePaths=sorted({diff['path'] for diff in matches})[:20],
                    **spec,
                ))

        scenario_reports.append(result)
        comparison_report[scenario_id] = dict(
            scenario=scenario['title'], fixture=scenario['fixture'],
            captureProvenance=captures,
            cases={case: dict(
                differences=len(diffs),
                byCategory={category: sum(1 for diff in diffs if diff['category'] == category)
                            for category in CATEGORY_ORDER},
                findingIds=sorted({diff['findingId'] for diff in diffs if diff.get('findingId')}),
                unmatchedDifferenceCount=len(unmatched_by_case[case]),
                samples=diffs[:40],
            ) for case, diffs in comparison_by_case.items()})

        ui_cases = {}
        for dimension in dimensions:
            case_id = dimension['id']
            comparison = comparison_evidence[case_id]
            comparison_frames = by_checkpoint(comparison['pisharp'])
            pi_frames = by_checkpoint(comparison['pi'])
            checkpoints = []
            for checkpoint in scenario['checkpoints']:
                pi_text = state_text(normalized_state(pi_frames[checkpoint])) \
                    if checkpoint in pi_frames else ''
                ps_text = state_text(normalized_state(comparison_frames[checkpoint])) \
                    if checkpoint in comparison_frames else ''
                checkpoints.append(dict(id=checkpoint, pi=pi_text, pisharp=ps_text))
            ui_cases[case_id] = dict(
                visual=result['visualBySize'][case_id],
                checkpoints=checkpoints,
            )
        ui_scenarios.append(dict(id=scenario_id, title=scenario['title'], cases=ui_cases))

    provenance_consistent = (len(all_pi_states) == 1 and len(all_pisharp_states) == 1)
    capture_provenance = dict(
        consistent=provenance_consistent and not provenance_errors and not any(
            not item['consistent'] for scenario in scenario_reports
            for item in scenario['captures'].values()),
        errors=provenance_errors,
        pi=sorted(json.loads(value) for value in all_pi_states),
        pisharp=sorted(json.loads(value) for value in all_pisharp_states),
    )
    report = dict(
        schema=2,
        fixture=manifest['fixture'],
        revisions=revisions,
        reference=manifest.get('piReference'),
        dimensions=dimensions,
        normalization=manifest['normalization'],
        weights=dict(functional=40, temporal=20, visual=30, cleanup=10,
                     perScenario=SCENARIO_POINTS, cleanupScenarios=sorted(CLEANUP_SCENARIOS)),
        captureProvenance=capture_provenance,
        scenarios=scenario_reports,
        categoryTotals=category_totals,
        total=total,
        assessedMaximum=assessed_maximum,
        availableMaximum=100,
        coverage=dict(assessedScenarios=sum(1 for item in scenario_reports if item['assessable']),
                      totalScenarios=len(scenario_reports),
                      assessedPoints=assessed_maximum, availablePoints=100,
                      percent=round(assessed_maximum, 1)),
        unassessable=unassessable,
        validation=dict(passed=not validation_errors and total_unmatched == 0
                        and provenance_consistent and not provenance_errors,
                        errors=validation_errors,
                        unmatchedDifferenceCount=total_unmatched,
                        provenanceConsistent=provenance_consistent,
                        provenanceErrors=provenance_errors,
                        piContractsPass=all(item['passes']
                                            for scenario in scenario_reports
                                            for item in scenario['contracts']['pi'].values())),
        findings=sorted(all_findings.values(), key=lambda finding: finding['id']),
        fixtureChecksums=fixture_checksums,
    )
    comparison = dict(fixture=manifest['fixture'], revisions=revisions,
                      dimensions=dimensions, captureProvenance=capture_provenance,
                      scenarios=comparison_report)
    ui_report = dict(fixture=manifest['fixture'], revisions=revisions,
                     dimensions=dimensions, normalization=manifest['normalization'],
                     captureProvenance=capture_provenance,
                     scenarios=ui_scenarios, findings=report['findings'])
    return report, comparison, ui_report


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--manifest', type=Path,
                        default=Path(__file__).with_name('scenarios.json'))
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--revisions', type=Path, required=True,
                        help='JSON with piReference, baseMain, branch and captureSource')
    args = parser.parse_args()

    manifest = json.loads(args.manifest.read_text())
    revisions = json.loads(args.revisions.read_text())
    report, comparison, ui_report = build_evidence(manifest, args.artifacts, revisions)
    args.evidence.mkdir(parents=True, exist_ok=True)
    (args.evidence / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
    (args.evidence / 'comparison.json').write_text(json.dumps(comparison, indent=2) + '\n')
    (args.evidence / 'ui-report.json').write_text(json.dumps(ui_report, indent=2) + '\n')
    print(json.dumps(dict(total=report['total'], maximum=report['assessedMaximum'],
                          assessment=report['categoryTotals'],
                          validation=report['validation'],
                          unassessable=len(report['unassessable'])), indent=2))
    return 0 if report['validation']['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
