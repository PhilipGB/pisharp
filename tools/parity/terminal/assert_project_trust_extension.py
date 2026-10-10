#!/usr/bin/env python3
"""Summarize current-Pi project_trust extension behavior from paired PTY evidence."""
import argparse
import gzip
import json
from pathlib import Path


def chat_requests(run):
    return [request for request in run['http']
            if request['method'] == 'POST' and request['path'].endswith('/chat/completions')]


def project_skill_present(request):
    system = next((message.get('content', '') for message in request['body'].get('messages', [])
                   if message.get('role') == 'system'), '')
    return ('<name>trusted-check</name>' in system and
            '<description>Trust extension fixture skill</description>' in system)


def inspect_product(run):
    if run['scenarioError'] is not None:
        raise AssertionError(f"Terminal scenario failed: {run['scenarioError']}")
    requests = chat_requests(run)
    if len(requests) != 1:
        raise AssertionError(f"Expected one provider request, received {len(requests)}")
    return dict(chatRequests=1, trustedProjectSkillPresent=project_skill_present(requests[0]),
                scenarioError=None)


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
    pi = inspect_product(capture['pi'])
    pisharp = inspect_product(capture['pisharp'])
    calibration = json.loads(args.calibration_report.read_text())['cases'][0]
    if not calibration['match'] or not calibration['terminalMatch'] or not calibration['httpMatch']:
        raise AssertionError('Same-Pi calibration did not match terminal state and HTTP')
    extension_trace = args.extension_log.read_text().splitlines()
    expected_trace = ['configured', 'trust-undecided:true', 'trust-decision:true']
    if extension_trace != expected_trace:
        raise AssertionError(f'Expected one pre-trust extension load and ordered handlers; received {extension_trace}')
    case = report['cases'][0]
    result = dict(
        fixture=report['fixture'], piSha=report['piSha'], pisharpSha=report['pisharpSha'],
        paired=dict(pi=pi, pisharp=pisharp, skillTrustDifference=(pi['trustedProjectSkillPresent'] !=
                                                                  pisharp['trustedProjectSkillPresent']),
                    fullTerminalMatch=case['terminalMatch'], fullHttpMatch=case['httpMatch'],
                    stateDifferences=case['differences'], renderDifferences=case['renderDifferences'],
                    rawByteMatch=case['rawMatch'], scenarioErrors=case['scenarioErrors']),
        calibration=dict(match=calibration['match'], terminalMatch=calibration['terminalMatch'],
                         httpMatch=calibration['httpMatch'], renderDifferences=calibration['renderDifferences']),
        pisharpExtensionTrace=extension_trace)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
