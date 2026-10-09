#!/usr/bin/env python3
"""Assert that trusted-project legacy skills paths and command completion reload live."""
import argparse
import gzip
import json
from pathlib import Path


def chat_requests(run):
    return [request for request in run['http']
            if request['method'] == 'POST' and request['path'].endswith('/chat/completions')]


def user_text(request):
    sections = []
    for message in request['body'].get('messages', []):
        if message.get('role') != 'user':
            continue
        content = message.get('content')
        if isinstance(content, str):
            sections.append(content)
        elif isinstance(content, list):
            sections.extend(part.get('text', '') for part in content if isinstance(part, dict))
    return '\n'.join(sections)


def screen_text(frame):
    state = frame['state']
    buffer = state['buffers'][state['activeScreen']]
    return '\n'.join(''.join(cell.get('chars', '') for cell in line['cells'])
                     for line in buffer['lines'])


def completion_rows(frame):
    state = frame['state']
    buffer = state['buffers'][state['activeScreen']]
    return [''.join(cell.get('chars', '') for cell in line['cells']).rstrip()
            for line in buffer['lines']
            if 'skill:' in ''.join(cell.get('chars', '') for cell in line['cells'])
            and not ''.join(cell.get('chars', '') for cell in line['cells']).lstrip().startswith('/skill:')]


def inspect_product(run):
    if run['scenarioError'] is not None:
        raise AssertionError(f"Terminal scenario failed: {run['scenarioError']}")
    before = next(frame for frame in run['frames'] if frame['id'] == 'completion-disabled-before-reload')
    if 'initial-legacy-skill' in screen_text(before):
        raise AssertionError('The disabled legacy skill command appeared in autocomplete before reload')
    if completion_rows(before):
        raise AssertionError('Slash skill autocomplete appeared before the legacy setting was enabled')
    after = next(frame for frame in run['frames'] if frame['id'] == 'completion-enabled-after-reload')
    if 'reloaded-legacy-skill' not in screen_text(after):
        raise AssertionError('The reloaded legacy skill command did not appear in autocomplete')
    visible_suggestions = completion_rows(after)
    if not visible_suggestions:
        raise AssertionError('The reloaded skill autocomplete menu had no visible entries')
    requests = chat_requests(run)
    if len(requests) != 2:
        raise AssertionError(f'Expected two provider requests, received {len(requests)}')
    first, second = map(user_text, requests)
    for marker in ('Initial legacy skill body marker.', 'first-argument'):
        if marker not in first:
            raise AssertionError(f'Initial legacy skill projection is missing: {marker}')
    for marker in ('Reloaded legacy skill body marker.', 'second-argument'):
        if marker not in second:
            raise AssertionError(f'Reloaded legacy skill projection is missing: {marker}')
    return dict(chatRequests=2, disabledCompletionBeforeReload=True,
                reloadedCompletionEnabled=True, initialSkillExpanded=True,
                reloadedSkillExpanded=True, visibleSuggestionRows=visible_suggestions,
                scenarioError=None)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--capture', type=Path, required=True)
    parser.add_argument('--calibration-report', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.report.read_text())
    with gzip.open(args.capture, 'rt', encoding='utf-8') as stream:
        capture = json.load(stream)
    behavior = {product: inspect_product(capture[product]) for product in ('pi', 'pisharp')}
    if behavior['pi'] != behavior['pisharp']:
        raise AssertionError('Pi and PiSharp legacy skill reload behavior differs')
    calibration = None
    if args.calibration_report:
        case = json.loads(args.calibration_report.read_text())['cases'][0]
        calibration = dict(terminalMatch=case['terminalMatch'], httpMatch=case['httpMatch'],
                           renderDifferences=case['renderDifferences'], scenarioErrors=case['scenarioErrors'])
    case = report['cases'][0]
    result = dict(piSha=report['piSha'], pisharpSha=report['pisharpSha'],
                  fixture=report['fixture'], behaviorMatch=True, behavior=behavior,
                  fullTerminalMatch=case['terminalMatch'], fullHttpMatch=case['httpMatch'],
                  rawMatch=case['rawMatch'], terminalDifferences=case['differences'],
                  renderDifferences=case['renderDifferences'],
                  controlBoundaryDifferences=case['controlBoundaryDifferenceCount'],
                  scenarioErrors=case['scenarioErrors'], calibration=calibration,
                  evidence=dict(report=str(args.report), capture=str(args.capture),
                                calibrationReport=str(args.calibration_report) if args.calibration_report else None))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
