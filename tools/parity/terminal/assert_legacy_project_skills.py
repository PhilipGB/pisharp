#!/usr/bin/env python3
"""Assert migration of trusted-project legacy skill paths and autocomplete settings."""
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


def inspect_product(run):
    if run['scenarioError'] is not None:
        raise AssertionError(f"Terminal scenario failed: {run['scenarioError']}")
    completion_frame = next(frame for frame in run['frames'] if frame['id'] == 'skill-completion-disabled')
    if 'legacy-project-skill' in screen_text(completion_frame):
        raise AssertionError('The disabled legacy skill command appeared in autocomplete')
    requests = chat_requests(run)
    if len(requests) != 1:
        raise AssertionError(f'Expected one provider request, received {len(requests)}')
    prompt = user_text(requests[0])
    for marker in ('Legacy project skill body marker.', 'legacy-argument'):
        if marker not in prompt:
            raise AssertionError(f'Migrated skill content is missing from the provider prompt: {marker}')
    return dict(chatRequests=len(requests),
                legacySkillExpanded=True,
                legacyArgumentPreserved=True,
                skillCompletionDisabled=True,
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
        raise AssertionError('Pi and PiSharp migrated skill behavior differs')
    calibration = None
    if args.calibration_report:
        calibration_case = json.loads(args.calibration_report.read_text())['cases'][0]
        calibration = dict(terminalMatch=calibration_case['terminalMatch'],
                           httpMatch=calibration_case['httpMatch'],
                           renderDifferences=calibration_case['renderDifferences'],
                           scenarioErrors=calibration_case['scenarioErrors'])
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
