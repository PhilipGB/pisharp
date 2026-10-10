#!/usr/bin/env python3
"""Assert trusted project tools and skill behavior in paired terminal captures."""
import argparse
import hashlib
import gzip
import json
import re
from pathlib import Path


def chat_requests(run):
    return [request for request in run['http']
            if request['method'] == 'POST' and request['path'].endswith('/chat/completions')]


def tool_names(request):
    return [tool.get('function', {}).get('name') for tool in request['body'].get('tools', [])]


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


def skill_prompt(request):
    system = next((message.get('content', '') for message in request['body'].get('messages', [])
                   if message.get('role') == 'system'), '')
    start = system.find('<skills>')
    end = system.find('</skills>', start)
    return system[start:end + len('</skills>')] if start >= 0 and end >= 0 else None


def skill_prompt_summary(request):
    prompt = skill_prompt(request)
    if prompt is None:
        return dict(present=False, reader=None, skillNames=[], sha256=None)
    reader = ('read' if 'Use the read tool to load a skill' in prompt else
              'bash' if 'Use bash to load a skill' in prompt else
              'indirect' if 'Load a skill\'s file when' in prompt else None)
    return dict(present=True, reader=reader,
                skillNames=re.findall(r'<name>(.*?)</name>', prompt),
                sha256=hashlib.sha256(prompt.encode('utf-8')).hexdigest())


def inspect_product(run):
    if run['scenarioError'] is not None:
        raise AssertionError(f"Terminal scenario failed: {run['scenarioError']}")
    requests = chat_requests(run)
    if len(requests) != 2:
        raise AssertionError(f'Expected two provider requests, received {len(requests)}')
    names = [tool_names(request) for request in requests]
    if names != [['read'], ['read', 'bash']]:
        raise AssertionError(f'Unexpected tools before/after reload: {names}')
    before = user_text(requests[0])
    after = user_text(requests[1])
    if 'Use the initial skill content.' not in before:
        raise AssertionError('The startup project skill was not expanded into the first prompt')
    if 'Use the reloaded skill content.' not in after or 'second argument' not in after:
        raise AssertionError('The newly discovered project skill was not expanded after reload')
    return dict(chatRequests=len(requests), toolNamesByRequest=names,
                skillPromptByRequest=[skill_prompt_summary(request) for request in requests],
                initialSkillInvokedBeforeReload=True, reloadedSkillInvokedAfterReload=True,
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
    behavioral = {product: inspect_product(capture[product]) for product in ('pi', 'pisharp')}
    if behavioral['pi']['toolNamesByRequest'] != behavioral['pisharp']['toolNamesByRequest']:
        raise AssertionError('Pi and PiSharp active tool names differ across reload')
    skill_prompt_match = (behavioral['pi']['skillPromptByRequest'] ==
                          behavioral['pisharp']['skillPromptByRequest'])
    calibration = None
    if args.calibration_report:
        calibration_report = json.loads(args.calibration_report.read_text())
        case = calibration_report['cases'][0]
        calibration = dict(match=case['match'], terminalMatch=case['terminalMatch'],
                           httpMatch=case['httpMatch'], rawMatch=case['rawMatch'],
                           renderDifferences=case['renderDifferences'],
                           firstDifferences=case['firstDifferences'][:4])
    case = report['cases'][0]
    result = dict(
        piSha=report['piSha'],
        pisharpSha=report['pisharpSha'],
        fixture=report['fixture'],
        behaviorMatch=True,
        behavior=behavioral,
        skillPromptMatch=skill_prompt_match,
        fullTerminalMatch=case['terminalMatch'],
        fullHttpMatch=case['httpMatch'],
        rawMatch=case['rawMatch'],
        terminalDifferences=case['differences'],
        renderDifferences=case['renderDifferences'],
        controlBoundaryDifferences=case['controlBoundaryDifferenceCount'],
        scenarioErrors=case['scenarioErrors'],
        calibration=calibration,
        evidence=dict(report=str(args.report), capture=str(args.capture),
                      calibrationReport=str(args.calibration_report) if args.calibration_report else None),
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
