#!/usr/bin/env python3
"""Assert pre-trust project extension dialogs and trust outcome in paired PTY captures."""
import argparse
import gzip
import hashlib
import json
import re
from pathlib import Path


def inspect_product(run):
    if run['scenarioError'] is not None:
        raise AssertionError(f"Terminal scenario failed: {run['scenarioError']}")
    requests = [request for request in run['http']
                if request['method'] == 'POST' and request['path'].endswith('/chat/completions')]
    if len(requests) != 1:
        raise AssertionError(f"Expected one provider request, received {len(requests)}")
    system = next((message.get('content', '') for message in requests[0]['body'].get('messages', [])
                   if message.get('role') == 'system'), '')
    trusted = ('<name>trusted-check</name>' in system and
               '<description>Trust extension fixture skill</description>' in system)
    if not trusted:
        raise AssertionError('The extension UI result did not trust and load the project skill')
    normalized_system = normalize_system_prompt(system)
    return dict(chatRequests=1, trustedProjectSkillPresent=True,
                systemPromptSectionOrder=re.findall(r'<(tools|rules|docs|addendum|project_context|skills|cwd)>', system),
                normalizedSystemPromptSha256=hashlib.sha256(normalized_system.encode('utf-8')).hexdigest(),
                scenarioError=None)


def normalize_system_prompt(prompt):
    # Pi and PiSharp install their documentation beside different package roots.
    # The generated guidance matches while these absolute paths remain product-specific.
    return re.sub(r'(?m)^(- Main documentation: |- Additional docs: |- Examples: ).+$',
                  lambda match: match.group(1) + '<documentation-path>', prompt)


def difference_paths(left, right, path='$'):
    if isinstance(left, dict) and isinstance(right, dict):
        paths = []
        for key in sorted(left.keys() | right.keys()):
            child = f'{path}.{key}'
            if key not in left or key not in right:
                paths.append(child)
            else:
                paths.extend(difference_paths(left[key], right[key], child))
        return paths
    if isinstance(left, list) and isinstance(right, list):
        paths = [] if len(left) == len(right) else [f'{path}.length']
        for index, (left_item, right_item) in enumerate(zip(left, right)):
            paths.extend(difference_paths(left_item, right_item, f'{path}[{index}]'))
        return paths
    return [] if left == right else [path]


def inspect_startup_dialog_frames(capture):
    expected_ids = ['trust-select', 'trust-select-option', 'trust-confirm']
    products = {name: capture[name]['frames'] for name in ('pi', 'pisharp')}
    matches = []
    for index, expected_id in enumerate(expected_ids):
        frames = {name: product_frames[index] if len(product_frames) > index else None
                  for name, product_frames in products.items()}
        if any(frame is None or frame.get('id') != expected_id for frame in frames.values()):
            raise AssertionError(f'Expected startup dialog frame {expected_id} at position {index}: {frames}')
        if frames['pi']['state'] != frames['pisharp']['state']:
            paths = difference_paths(frames['pi']['state'], frames['pisharp']['state'])
            raise AssertionError(f'{expected_id} terminal snapshots differ at {paths[:20]}')
        matches.append(dict(id=expected_id, stateMatched=True,
                            activeScreen=frames['pi']['state']['activeScreen']))
    return matches


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--capture', type=Path, required=True)
    parser.add_argument('--calibration-report', type=Path, required=True)
    parser.add_argument('--extension-log', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.report.read_text())
    with gzip.open(args.capture, 'rt', encoding='utf-8') as stream:
        capture = json.load(stream)
    startup_dialog_frames = inspect_startup_dialog_frames(capture)
    pi = inspect_product(capture['pi'])
    pisharp = inspect_product(capture['pisharp'])
    pi_request = next(request for request in capture['pi']['http']
                      if request['method'] == 'POST' and request['path'].endswith('/chat/completions'))
    pisharp_request = next(request for request in capture['pisharp']['http']
                           if request['method'] == 'POST' and request['path'].endswith('/chat/completions'))
    pi_system = next(message.get('content', '') for message in pi_request['body'].get('messages', [])
                     if message.get('role') == 'system')
    pisharp_system = next(message.get('content', '') for message in pisharp_request['body'].get('messages', [])
                          if message.get('role') == 'system')
    normalized_system_prompt_match = normalize_system_prompt(pi_system) == normalize_system_prompt(pisharp_system)
    if not normalized_system_prompt_match:
        raise AssertionError('Pi and PiSharp trusted-project system prompts differ beyond documentation install paths')
    request_difference_paths = difference_paths(pi_request['body'], pisharp_request['body'])
    calibration = json.loads(args.calibration_report.read_text())['cases'][0]
    if not calibration['match'] or not calibration['terminalMatch'] or not calibration['httpMatch']:
        raise AssertionError('Same-Pi calibration did not match terminal state and HTTP')
    extension_trace = args.extension_log.read_text().splitlines()
    expected_trace = [
        'configured',
        'trust-undecided:true',
        'trust-decision:true',
        'trust-ui:tui:true:Continue:true:approved',
    ]
    if extension_trace != expected_trace:
        raise AssertionError(f'Expected all four ordered startup UI operations; received {extension_trace}')
    case = report['cases'][0]
    result = dict(
        fixture=report['fixture'], piSha=report['piSha'], pisharpSha=report['pisharpSha'],
        paired=dict(pi=pi, pisharp=pisharp, terminalMatch=case['terminalMatch'],
                    httpMatch=case['httpMatch'], stateDifferences=case['differences'],
                    renderDifferences=case['renderDifferences'], rawByteMatch=case['rawMatch'],
                    startupDialogFrames=startup_dialog_frames,
                    scenarioErrors=case['scenarioErrors'],
                    normalizedSystemPromptMatch=normalized_system_prompt_match,
                    providerRequestDifferenceCount=len(request_difference_paths),
                    providerRequestDifferencePaths=request_difference_paths),
        calibration=dict(match=calibration['match'], terminalMatch=calibration['terminalMatch'],
                         httpMatch=calibration['httpMatch'], renderDifferences=calibration['renderDifferences']),
        pisharpExtensionTrace=extension_trace)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
