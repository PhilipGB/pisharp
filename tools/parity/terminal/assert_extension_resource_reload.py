#!/usr/bin/env python3
"""Assert extension-contributed skills and prompts survive reload differentially."""
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


def latest_user_message(request):
    messages = []
    for message in request['body'].get('messages', []):
        if message.get('role') != 'user':
            continue
        content = message.get('content')
        if isinstance(content, str):
            messages.append(content)
        elif isinstance(content, list):
            messages.append(''.join(part.get('text', '') for part in content if isinstance(part, dict)))
    return messages[-1] if messages else None


def frame_text(run, frame_id):
    frame = next(frame for frame in run['frames'] if frame['id'] == frame_id)
    return '\n'.join(''.join(cell.get('chars', '') for cell in line.get('cells', []))
                     for line in frame['state']['viewport'])


def inspect_product(run):
    if run['scenarioError'] is not None:
        raise AssertionError(f"Terminal scenario failed: {run['scenarioError']}")
    requests = chat_requests(run)
    if len(requests) != 4:
        raise AssertionError(f'Expected four provider requests, received {len(requests)}')
    prompts = [user_text(request) for request in requests]
    expected = [
        ('Use the startup extension skill.', 'initial-argument'),
        ('Review the startup extension template: initial-argument',),
        ('Use the reloaded extension skill.', 'reload-argument'),
        ('Review the reloaded extension template: reload-argument',),
    ]
    for index, markers in enumerate(expected):
        missing = [marker for marker in markers if marker not in prompts[index]]
        if missing:
            raise AssertionError(f'Request {index + 1} did not expand extension resources: {missing}')
    startup_screen = frame_text(run, 'startup')
    reload_screen = frame_text(run, 'reload')
    if '/.pi/extension-resources/startup/themes' not in startup_screen or 'theme path does not exist' not in startup_screen:
        raise AssertionError('Startup extension theme diagnostics were not visible')
    if '/.pi/extension-resources/reloaded/themes' not in reload_screen or 'theme path does not exist' not in reload_screen:
        raise AssertionError('Reloaded extension theme diagnostics were not visible')
    if 'Reloaded keybindings, extensions, skills, prompts, themes, and context files' not in reload_screen:
        raise AssertionError('The Pi-compatible /reload status was not visible')
    return dict(chatRequests=len(requests), startupSkillAndPromptExpanded=True,
                reloadedSkillAndPromptExpanded=True,
                latestUserMessages=[latest_user_message(request) for request in requests],
                startupThemeDiagnosticVisible=True, reloadedThemeDiagnosticVisible=True,
                reloadStatusVisible=True,
                scenarioError=None)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--capture', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.report.read_text())
    with gzip.open(args.capture, 'rt', encoding='utf-8') as stream:
        capture = json.load(stream)
    behavior = {product: inspect_product(capture[product]) for product in ('pi', 'pisharp')}
    message_differences = [dict(request=index + 1, pi=pi_message, pisharp=pisharp_message)
                           for index, (pi_message, pisharp_message) in enumerate(zip(
                               behavior['pi']['latestUserMessages'], behavior['pisharp']['latestUserMessages']))
                           if pi_message != pisharp_message]
    behavior_match = not message_differences
    result = dict(piSha=report['piSha'], pisharpSha=report['pisharpSha'], fixture=report['fixture'],
                  behaviorMatch=behavior_match, requestMessageDifferences=message_differences, behavior=behavior,
                  fullTerminalMatch=report['cases'][0]['terminalMatch'],
                  fullHttpMatch=report['cases'][0]['httpMatch'], rawMatch=report['cases'][0]['rawMatch'],
                  terminalDifferences=report['cases'][0]['differences'],
                  renderDifferences=report['cases'][0]['renderDifferences'],
                  scenarioErrors=report['cases'][0]['scenarioErrors'],
                  evidence=dict(report=str(args.report), capture=str(args.capture)))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))
    if not behavior_match:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
