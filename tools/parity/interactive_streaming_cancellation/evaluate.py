#!/usr/bin/env python3
"""Score the interactive streaming/cancellation fixture from captured evidence.

The evaluator deliberately reads only the compact ``*.assertions.json.gz``
companions produced by ``tools/parity/terminal/run.py``. It does not launch a
product, contact a provider, or read wall-clock timings. Every point in the
report is derived from a named checkpoint, a fixture gate assertion, or the
deterministic fixture HTTP trace.
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


def load_gzip_json(path):
    with gzip.open(path, 'rt', encoding='utf-8') as stream:
        return json.load(stream)


def state_text(state, buffer_name=None):
    """Render the visible characters of a normalized terminal state."""
    if buffer_name is None:
        lines = state.get('viewport', [])
    else:
        lines = state.get('buffers', {}).get(buffer_name, {}).get('lines', [])
    rendered = []
    for line in lines:
        chars = ''.join(' ' if (cell.get('chars') if isinstance(cell, dict) else None) in BLANKS
                        else cell.get('chars', '')
                        for cell in line.get('cells', []) if isinstance(cell, dict))
        rendered.append(chars.rstrip())
    return '\n'.join(rendered).strip('\n')


def collapsed(text):
    return re.sub(r'\s+', ' ', text).strip()


def mask_dynamic(lines, marker_width=16):
    """Mask wall-clock durations and partial-stream usage percentages.

    The replacement keeps a fixed number of cells so two runs that print values
    of different character width (``0.6s`` and ``0.98s``) still line up. The
    prefix and suffix cells, including their styles, are preserved; only the
    measured value becomes a canonical marker.
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
        messages = request.get('body', {}).get('messages', [])
        for message in messages:
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


def process_summaries(evidence):
    return {checkpoint: process_summary(processes)
            for checkpoint, processes in process_by_checkpoint(evidence).items()}


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
    first_processes = process_summaries(first_evidence)
    second_processes = process_summaries(second_evidence)
    if first_processes != second_processes:
        found.append(dict(checkpoint='process', path='$.processByFrame',
                          left=first_processes, right=second_processes))
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


def cleanup_check(evidence, scenario):
    processes = process_by_checkpoint(evidence)
    cleanup = processes.get(scenario.get('cleanupCheckpoint'), {})
    initial = processes.get(scenario.get('processCheckpoint'), {})
    exit_state = by_checkpoint(evidence).get('exit', {})
    normal_restored = exit_state.get('activeScreen') == 'normal'
    cursor_visible = bool(exit_state.get('cursor', {}).get('visible'))
    sync_off = not exit_state.get('modes', {}).get('private', {}).get('2026', False)
    applies = 'cleanupCheckpoint' in scenario
    return dict(
        initialProcesses=process_summary(initial),
        cleanupProcesses=process_summary(cleanup),
        processesGone=(bool(cleanup) and all(entry.get('state') is None
                                             for entry in cleanup.values())) if applies else None,
        normalScreenRestored=normal_restored,
        cursorVisible=cursor_visible,
        synchronizedOutputOff=sync_off,
    )


def finding_for(scenario_id, difference):
    category = difference['category']
    path = str(difference.get('path', ''))
    checkpoint = difference.get('checkpoint')
    if scenario_id == 'S01':
        if category == 'style':
            return 'S01-code-syntax-builtin-highlight'
        return 'S01-unclassified'
    if scenario_id == 'S02':
        if '.attributes.italic' in path:
            return 'S02-reasoning-italic-style'
        if checkpoint == 's02-01-before-reasoning' and category == 'style':
            return 'S02-reasoning-status-style'
        return 'S02-reasoning-block-layout'
    if scenario_id == 'S05':
        if category == 'text':
            return 'S05-tool-cancel-error-wording'
        if category == 'style':
            return 'S05-tool-cancel-error-style'
        return 'S05-unclassified'
    return f'{scenario_id}-{category}-unclassified'


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
        evidence='comparison.json S01 80x24/120x40 style cells for print; ui-report.json checkpoint text'),
    'S02-reasoning-block-layout': dict(
        severity='P1', category='layout/text',
        piObservation='Pi keeps one reasoning section, renders one Thinking... placeholder while hidden, and restores the joined reasoning block without extra rows.',
        pisharpObservation='PiSharp splits reasoning chunks around toggle status lines, shows two Thinking... placeholders, and leaves extra blank/indented rows; the final transcript is shifted by three rows.',
        source='src/PiSharp.Cli/Tui/Rendering/TerminalConversationPresentation.cs',
        correction='Collapse streamed reasoning into one block, keep a single hidden placeholder, and re-render the block in place when visibility changes.',
        reproduction='python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi '
                     '--pisharp src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll '
                     '--fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/s02-reasoning-streaming.json '
                     '--case 80x24-dark-fullscreen --output artifacts/parity/repro-s02',
        evidence='comparison.json S02 text/layout differences from s02-02 through exit; ui-report.json checkpoint text'),
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
                   calibration_details, evidence_by_case):
    errors = [evidence.get('scenarioError') for evidence in evidence_by_case.values()]
    orders = [checkpoint_order(evidence) for evidence in evidence_by_case.values()]
    expected_order = scenario['checkpoints']
    complete = all(not error for error in errors) and all(order == expected_order for order in orders)

    absences = [absence_failures(evidence, scenario) for evidence in evidence_by_case.values()]
    transitions = complete and all(not failures for failures in absences)
    content_checks = [content_check(evidence, scenario) for evidence in evidence_by_case.values()]
    content_ok = all(not check['missing'] and not check['duplicates'] for check in content_checks)
    request_checks = [request_check(evidence, scenario) for evidence in evidence_by_case.values()]
    requests_ok = all(check['countMatch'] and check.get('toolResultExact', True)
                      and not check['duplicateToolCall'] for check in request_checks)

    idle_by_case = {}
    for case_id, evidence in evidence_by_case.items():
        state = by_checkpoint(evidence).get(scenario['idleCheckpoint'])
        idle_by_case[case_id] = bool(state) and 'Working' in state_text(normalized_state(state))
    idle_ok = not any(idle_by_case.values())

    cleanup_by_case = {case_id: cleanup_check(evidence, scenario)
                       for case_id, evidence in evidence_by_case.items()}
    process_ok = all(item['processesGone'] is True for item in cleanup_by_case.values()) \
        if 'cleanupCheckpoint' in scenario else True

    subsequent_by_case = {}
    for case_id, evidence in evidence_by_case.items():
        if 'secondPromptResponse' in scenario:
            subsequent_by_case[case_id] = collapsed(scenario['secondPromptResponse']) in \
                collapsed(final_transcript_text(evidence))
        else:
            exit_state = final_state(evidence)
            subsequent_by_case[case_id] = (exit_state.get('exitCode') == 0
                                           and bool(exit_state.get('cursor', {}).get('visible')))
    subsequent_ok = all(subsequent_by_case.values())

    functional_checks = dict(transitions=transitions, content=content_ok,
                             finalState=transitions and requests_ok and idle_ok and process_ok,
                             subsequent=subsequent_ok)
    functional = sum(2 for value in functional_checks.values() if value)
    temporal_checks = dict(complete=complete, absence=transitions, idle=idle_ok,
                           requests=requests_ok)
    temporal = sum(1 for value in temporal_checks.values() if value)

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

    cleanup = None
    cleanup_checks = None
    cancelled_has_working = {}
    if scenario_id in CLEANUP_SCENARIOS:
        cleanup_checks = dict(cancellationSettled=True, noLateOutput=True,
                              terminalRestored=True, cleanupTarget=True,
                              subsequentPrompt=subsequent_ok)
        for case_id, evidence in evidence_by_case.items():
            frames = by_checkpoint(evidence)
            cancelled = frames.get(scenario['cancelledCheckpoint'])
            cancelled_text = state_text(normalized_state(cancelled)) if cancelled else ''
            cancelled_has_working[case_id] = (not cancelled) or 'Working' in cancelled_text
            cleanup_checks['cancellationSettled'] &= not cancelled_has_working[case_id]
            cleanup_checks['noLateOutput'] &= all(
                token not in all_visible_text(evidence)
                for token in scenario.get('absenceEverywhere', []))
            item = cleanup_by_case[case_id]
            cleanup_checks['terminalRestored'] &= bool(item['normalScreenRestored']
                                                       and item['cursorVisible']
                                                       and item['synchronizedOutputOff'])
            if 'cleanupCheckpoint' in scenario:
                cleanup_checks['cleanupTarget'] &= item['processesGone'] is True
            else:
                cleanup_checks['cleanupTarget'] &= not idle_by_case[case_id]
        cleanup = sum(1 for value in cleanup_checks.values() if value)

    calibrates = all(not value for value in calibration_by_case.values())
    assessable = calibrates and complete
    maximum = SCENARIO_POINTS['functional'] + SCENARIO_POINTS['temporal'] + \
        SCENARIO_POINTS['visual'] + (SCENARIO_POINTS['cleanup'] if cleanup is not None else 0)
    subtotal = functional + temporal + visual + (cleanup or 0) if assessable else None

    finding_ids = sorted({finding_for(scenario_id, diff)
                          for case_diffs in comparison_by_case.values() for diff in case_diffs})
    result = dict(
        id=scenario_id, title=scenario['title'], fixture=scenario['fixture'],
        calibrates=calibrates, assessable=assessable,
        functional=functional if assessable else None,
        temporal=temporal if assessable else None,
        visual=visual if assessable else None,
        visualBySize=visual_by_size if assessable else None,
        cleanup=cleanup if assessable else None,
        subtotal=subtotal,
        maximum=maximum,
        calibration=calibration_by_case,
        calibrationDifferences=calibration_details,
        scoreInputs=dict(
            complete=complete,
            functionalChecks=functional_checks,
            temporalChecks=temporal_checks,
            visualBySize=visual_by_size,
            cleanupChecks=cleanup_checks,
            cancelledHasWorking=cancelled_has_working,
            idleHasWorking=idle_by_case,
            subsequentPrompt=subsequent_by_case,
            processOk=process_ok,
        ),
        assertions=dict(
            scenarioErrors=errors,
            checkpointOrder=orders,
            absenceFailures=absences,
            content=content_checks,
            requests=request_checks,
            cleanup=cleanup_by_case,
        ),
        findings=finding_ids,
    )
    return result


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

    for scenario_id, scenario in manifest['scenarios'].items():
        scenario = dict(scenario)
        scenario['dims'] = dimensions
        fixture_path = (Path(__file__).resolve().parents[1] / 'terminal' / 'fixtures'
                        / manifest['fixture'] / scenario['fixture'])
        fixture_checksums[scenario['fixture']] = hashlib.sha256(fixture_path.read_bytes()).hexdigest()
        calibration_by_case = {}
        comparison_by_case = {}
        evidence_by_case = {}
        calibration_details = {}
        for dimension in dimensions:
            case_id = dimension['id']
            stem = scenario['fixture'].removesuffix('.json')
            calibration = load_gzip_json(artifacts / 'calibration' / stem /
                                         f'{case_id}.assertions.json.gz')
            comparison = load_gzip_json(artifacts / 'comparison' / stem /
                                        f'{case_id}.assertions.json.gz')
            calibration_diff = calibration_differences(calibration['pi'], calibration['pisharp'],
                                                       scenario)
            calibration_by_case[case_id] = bool(calibration_diff)
            calibration_details[case_id] = calibration_diff
            comparison_by_case[case_id] = compare_products(comparison['pi'], comparison['pisharp'],
                                                           scenario)
            evidence_by_case[case_id] = comparison['pisharp']
            for diff in comparison_by_case[case_id]:
                diff['findingId'] = finding_for(scenario_id, diff)

        result = score_scenario(scenario_id, scenario, comparison_by_case,
                                calibration_by_case, calibration_details, evidence_by_case)
        if not result['assessable']:
            if not result['calibrates']:
                reason = 'same-Pi calibration failed'
            else:
                reason = 'scenario did not reach every named checkpoint'
            unassessable.append(dict(
                scenario=scenario_id, reason=reason,
                cases={case: [dict(checkpoint=item['checkpoint'], path=item['path'])
                              for item in items[:10]]
                       for case, items in calibration_details.items() if items},
            ))
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
                    spec = dict(severity='Inconclusive', category='unclassified',
                                piObservation='Difference was not matched to a documented finding.',
                                pisharpObservation='Difference was not matched to a documented finding.',
                                source='unknown', correction='Investigate before scoring.',
                                reproduction='Inspect comparison.json for this scenario.',
                                evidence='comparison.json')
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
            cases={case: dict(
                differences=len(diffs),
                byCategory={category: sum(1 for diff in diffs if diff['category'] == category)
                            for category in CATEGORY_ORDER},
                findingIds=sorted({diff['findingId'] for diff in diffs}),
                samples=diffs[:40],
            ) for case, diffs in comparison_by_case.items()})
    for scenario_id, scenario in manifest['scenarios'].items():
        scenario = dict(scenario)
        result = next(item for item in scenario_reports if item['id'] == scenario_id)
        cases = {}
        for dimension in dimensions:
            case_id = dimension['id']
            stem = scenario['fixture'].removesuffix('.json')
            calibration = load_gzip_json(artifacts / 'calibration' / stem /
                                         f'{case_id}.assertions.json.gz')
            comparison = load_gzip_json(artifacts / 'comparison' / stem /
                                        f'{case_id}.assertions.json.gz')
            calibration_frames = by_checkpoint(calibration['pi'])
            comparison_frames = by_checkpoint(comparison['pisharp'])
            checkpoints = []
            for checkpoint in scenario['checkpoints']:
                pi_text = state_text(normalized_state(calibration_frames[checkpoint])) \
                    if checkpoint in calibration_frames else ''
                ps_text = state_text(normalized_state(comparison_frames[checkpoint])) \
                    if checkpoint in comparison_frames else ''
                checkpoints.append(dict(id=checkpoint, pi=pi_text, pisharp=ps_text))
            cases[case_id] = dict(
                visual=result['visualBySize'][case_id] if result['visualBySize'] else None,
                checkpoints=checkpoints,
            )
        ui_scenarios.append(dict(id=scenario_id, title=scenario['title'], cases=cases))

    report = dict(
        schema=1,
        fixture=manifest['fixture'],
        revisions=revisions,
        reference=manifest.get('piReference'),
        dimensions=dimensions,
        normalization=manifest['normalization'],
        weights=dict(functional=40, temporal=20, visual=30, cleanup=10,
                     perScenario=SCENARIO_POINTS),
        scenarios=scenario_reports,
        categoryTotals=category_totals,
        total=total,
        assessedMaximum=assessed_maximum,
        availableMaximum=100,
        coverage=dict(assessedScenarios=sum(1 for item in scenario_reports if item['assessable']),
                      totalScenarios=len(scenario_reports),
                      assessedPoints=assessed_maximum, availablePoints=100,
                      percent=round(100 * assessed_maximum / 100, 1)),
        unassessable=unassessable,
        findings=sorted(all_findings.values(), key=lambda finding: finding['id']),
        fixtureChecksums=fixture_checksums,
    )
    comparison = dict(fixture=manifest['fixture'], revisions=revisions,
                      dimensions=dimensions, scenarios=comparison_report)
    ui_report = dict(fixture=manifest['fixture'], revisions=revisions,
                     dimensions=dimensions, normalization=manifest['normalization'],
                     scenarios=ui_scenarios, findings=report['findings'])
    return report, comparison, ui_report


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--manifest', type=Path,
                        default=Path(__file__).with_name('scenarios.json'))
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--revisions', type=Path, required=True,
                        help='JSON with piSha, pisharpSha, branch and fixture checksums')
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
                          unassessable=len(report['unassessable'])), indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
