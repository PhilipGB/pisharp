#!/usr/bin/env python3
"""Compare tool-result contract observables with pinned Pi and the PiSharp matrix evidence."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--evidence', type=Path,
                    help='Existing PiSharp matrix evidence directory. Generated when omitted.')
args = parser.parse_args()

pi_sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[3]
probes = Path(__file__).resolve().parent

CASES = [f'T{index:02d}' for index in range(1, 13)] + [
    'E02', 'E03', 'E04', 'E05', 'E06', 'E07', 'H01', 'H02', 'H03', 'H04', 'H05',
    'R01', 'R02', 'R03', 'R04', 'P01',
]
TEXT_IGNORED = {'E04'}
CLASSIFIED_DIFFERENCES = {
    'E05': {
        'classification': 'intentional_safety_strengthening',
        'reason': 'Pinned Pi treats outputSchema as declarative and accepts invalid structured output; '
                  'PiSharp validates the declared schema and records a visible returned error.',
    },
}


def run_command(command, *, cwd, env, timeout):
    result = subprocess.run(command, cwd=cwd, env=env, timeout=timeout,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if result.returncode != 0:
        raise RuntimeError(f'command failed: {" ".join(map(str, command))}\n{result.stdout[-8000:]}')


def matrix_evidence(directory):
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    env['PISHARP_TOOL_RESULT_EVIDENCE_DIR'] = str(directory)
    env['PISHARP_IMPLEMENTATION_SHA'] = subprocess.check_output(
        ['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
    directory.mkdir(parents=True)
    run_command(['dotnet', 'test', str(repo / 'tests/PiSharp.Tests/PiSharp.Tests.csproj'),
                 '--filter', 'FullyQualifiedName~ToolResultContractMatrixTests', '--nologo', '-v:quiet'],
                cwd=repo, env=env, timeout=900)


def normalize_json(value):
    return value


def normalize_details(value):
    return None if value in (None, {}) else value


def case_view(case, pi, evidence):
    observation = evidence[case]['observations']
    if case == 'E07':
        views = {}
        for name in ('single', 'mixed'):
            views[name] = compare_per_case(pi[case][name], observation[name])
        diffs = []
        for name, (pi_view, sharp_view, case_diffs) in views.items():
            diffs.extend(f'{name}:{item}' for item in case_diffs)
        return {'status': 'match' if not diffs else 'differ', 'diffs': diffs,
                'pi': {name: views[name][0] for name in views},
                'pisharp': {name: views[name][1] for name in views}}
    pi_view, sharp_view, diffs = compare_per_case(pi[case], observation)
    classification = CLASSIFIED_DIFFERENCES.get(case) if diffs else None
    return {'status': 'match' if not diffs else 'differ', 'diffs': diffs,
            'classification': classification,
            'pi': pi_view, 'pisharp': sharp_view}


def compare_per_case(pi, observation):
    pi_view = {
        'requests': pi['requests'],
        'executions': {name: count for name, count in pi['executed'].items() if count},
        'results': [{
            'callId': item['callId'],
            'text': item['text'],
            'isError': bool(item.get('isError')),
            'terminate': bool(item.get('terminate')),
            'structured': normalize_json(item.get('structured')),
            'details': normalize_details(item.get('details')),
            'usage': item.get('usage'),
            'images': item.get('images', 0),
        } for item in pi['results']],
    }
    observed = observation
    sharp_view = {
        'requests': observed['requests'],
        'executions': {name: count for name, count in observed['executions'].items() if count},
        'results': [{
            'callId': item['callId'],
            'text': item['text'],
            'isError': bool(item.get('isError')),
            'terminate': bool(item.get('terminate')),
            'structured': json.loads(item['structured']) if item.get('structured') else None,
            'details': normalize_details(json.loads(item['details']) if item.get('details') else None),
            'usage': item.get('usage'),
            'images': item.get('images') or 0,
        } for item in observed['canonical']],
    }
    if pi_view['results'] and observation['caseId'] in TEXT_IGNORED:
        for result in pi_view['results']:
            result.pop('text', None)
        for result in sharp_view['results']:
            result.pop('text', None)
    diffs = differences(pi_view, sharp_view)
    if 'T11' in observation.get('detail', {}) or 'executionOrder' in observation.get('detail', {}):
        pi_order = pi.get('order', [])
        sharp_order = observed.get('order', [])
        if pi_order and pi_order != sharp_order:
            diffs.append('/order')
    if 'lifecycle' in observation.get('detail', {}):
        pi_updates = pi.get('updates', [])
        sharp_updates = [item.split(':', 1)[1] for item in observed['detail']['lifecycle']
                         if item.startswith('tool_execution_update:')]
        if pi_updates != sharp_updates:
            diffs.append('/updates')
    if observation['caseId'] in ('P01',):
        provider_ids = [item['callId'] for item in observed['providerRequests'][1]['toolResults']]
        if [item['callId'] for item in pi_view['results']] != provider_ids:
            diffs.append('/providerOrder')
    return pi_view, sharp_view, diffs


def differences(left, right, path=''):
    if type(left) is not type(right) and not (isinstance(left, (int, float)) and isinstance(right, (int, float))):
        return [path or '/']
    if isinstance(left, dict):
        output = []
        for key in sorted(left.keys() | right.keys()):
            item = f'{path}/{key}'
            if key not in left or key not in right:
                output.append(item)
            else:
                output.extend(differences(left[key], right[key], item))
        return output
    if isinstance(left, list):
        output = [f'{path}/length'] if len(left) != len(right) else []
        for index, (a, b) in enumerate(zip(left, right)):
            output.extend(differences(a, b, f'{path}/{index}'))
        return output
    return [] if left == right else [path or '/']


with tempfile.TemporaryDirectory(prefix='pisharp-tool-result-differential-') as directory:
    work = Path(directory)
    pi_output = work / 'pi.json'
    evidence_dir = args.evidence or (work / 'evidence')
    if args.evidence is None:
        matrix_evidence(evidence_dir)

    oracle = args.pi.resolve() / 'packages/agent/test/pisharp-tool-result-oracle.test.ts'
    if oracle.exists():
        raise RuntimeError(f'Refusing to overwrite {oracle}')
    shutil.copyfile(probes / 'pi-oracle.test.ts', oracle)
    try:
        env = dict(os.environ, PISHARP_TOOL_RESULT_PI_OUTPUT=str(pi_output),
                   GITHUB_ACTIONS='')
        run_command(['node', str(args.pi.resolve() / 'node_modules/vitest/vitest.mjs'), 'run',
                     '--config', str(args.pi.resolve() / 'packages/agent/vitest.config.ts'),
                     'test/pisharp-tool-result-oracle.test.ts'],
                    cwd=args.pi.resolve() / 'packages/agent', env=env, timeout=300)
    finally:
        oracle.unlink()

    pi = json.loads(pi_output.read_text())
    evidence = {}
    for case in CASES:
        payload = json.loads((evidence_dir / f'{case}.json').read_text())
        evidence[case] = payload
        if payload.get('implementationSha') != subprocess.check_output(
                ['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip():
            raise RuntimeError(f'{case}: evidence was produced from another implementation head')

    cases = {case: case_view(case, pi, evidence) for case in CASES}
    intentional = {case: item for case, item in cases.items()
                   if item['status'] == 'differ' and item.get('classification')}
    unresolved = {case: item for case, item in cases.items()
                  if item['status'] == 'differ' and not item.get('classification')}
    summary = {
        'total': len(cases),
        'match': sum(1 for item in cases.values() if item['status'] == 'match'),
        'differ': sum(1 for item in cases.values() if item['status'] == 'differ'),
        'intentionalSafety': len(intentional),
        'unresolved': len(unresolved),
    }
    result = {
        'schema': 1,
        'piSha': pi_sha,
        'pisharpSha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip(),
        'normalization': (
            'Compares provider request counts, executed tool counts, canonical result call IDs, model text, '
            'error state, termination hints, structured content, details, usage, image counts and result order. '
            'Sequential event order is compared for T11; parallel completion order is not an ordering claim. '
            'Pi records a thrown tool failure as an error toolResult; PiSharp additionally keeps a thrown '
            'exception marker in canonical history and M.E.AI supplies generic provider text for that in-run '
            'request, so the canonical result text and error state are compared instead of that provider-only text.'
        ),
        'classification': {
            'E03': 'canonical text/error match; PiSharp provider-only text is generic M.E.AI text for thrown '
                   'exceptions while canonical history retains the Pi error text and a distinct exception marker.',
        },
        'summary': summary,
        'cases': cases,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    if unresolved:
        print(json.dumps({case: item['diffs'] for case, item in unresolved.items()}, indent=2))
        raise SystemExit(1)
    print(f'Matched {summary["match"]}/{summary["total"]} tool-result differential cases at Pi {pi_sha}; '
          f'{summary["intentionalSafety"]} classified difference(s).')
