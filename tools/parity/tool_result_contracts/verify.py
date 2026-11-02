#!/usr/bin/env python3
"""Verify the committed tool-result-contracts acceptance manifest and evidence."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

REQUIRED_IDS = ([f'T{index:02d}' for index in range(1, 13)] +
                [f'E{index:02d}' for index in range(1, 8)] +
                [f'R{index:02d}' for index in range(1, 6)] +
                [f'H{index:02d}' for index in range(1, 6)] +
                [f'P{index:02d}' for index in range(1, 6)])
ALLOWED_EVIDENCE_COMMITS = (
    'docs/parity/fixtures/tool-result-contracts/',
    'tools/parity/tool_result_contracts/',
    'docs/parity/execution-ledger.json',
    'docs/continuation.md',
)


def load(path):
    with path.open(encoding='utf-8') as handle:
        return json.load(handle)


def git(repo, *args):
    return subprocess.check_output(['git', *args], cwd=repo, text=True).strip()


def state_list(observation):
    return [{'callId': item['callId'], 'isError': bool(item['isError']),
             'terminate': bool(item.get('terminate'))} for item in observation['canonical']]


def verify_observation(label, observation, expected, failures):
    if observation.get('requests') != expected['requests']:
        failures.append(f'{label}: provider requests {observation.get("requests")} != {expected["requests"]}')
    executions = {name: count for name, count in observation.get('executions', {}).items() if count}
    if executions != expected['executions']:
        failures.append(f'{label}: tool executions {executions} != {expected["executions"]}')
    states = state_list(observation)
    if states != expected['resultStates']:
        failures.append(f'{label}: canonical result states {states} != {expected["resultStates"]}')
    jsonl_states = [{'callId': item['callId'], 'isError': bool(item['isError']),
                     'terminate': bool(item.get('terminate'))}
                    for item in observation.get('piJsonl', [])]
    if len(jsonl_states) != len(expected['resultStates']):
        failures.append(f'{label}: Pi JSONL result count {len(jsonl_states)} != '
                        f'{len(expected["resultStates"])}')
    else:
        for observed, wanted in zip(jsonl_states, expected['resultStates']):
            if observed['callId'] != wanted['callId']:
                failures.append(f'{label}: Pi JSONL call order {observed["callId"]} != {wanted["callId"]}')
            if observed['isError'] != wanted['isError']:
                failures.append(f'{label}: Pi JSONL isError differs for {wanted["callId"]}')
            if observed['terminate'] != wanted['terminate']:
                failures.append(f'{label}: Pi JSONL terminate differs for {wanted["callId"]}')


def verify(repo, manifest_path):
    repo = Path(repo)
    manifest = load(manifest_path)
    failures = []
    cases = manifest.get('cases', [])
    ids = [case.get('id') for case in cases]
    if sorted(ids) != sorted(REQUIRED_IDS):
        failures.append(f'case ids differ: missing={sorted(set(REQUIRED_IDS) - set(ids))} '
                        f'extra={sorted(set(ids) - set(REQUIRED_IDS))} duplicate={len(ids) - len(set(ids))}')

    evidence_root = repo / manifest['evidenceDirectory']
    test_source = (repo / 'tests/PiSharp.Tests/Runtime/Tools/ToolResultContractMatrixTests.cs').read_text()
    covered_criteria = set()
    for case in cases:
        case_id = case['id']
        covered_criteria.update(case.get('criteria', []))
        if case.get('status') != 'passed':
            failures.append(f'{case_id}: status {case.get("status")} is not passed')
        if not case.get('test') or case['test'] not in test_source:
            failures.append(f'{case_id}: executable test reference {case.get("test")!r} not found')
        evidence_path = evidence_root / f'{case_id}.json'
        if not evidence_path.exists():
            failures.append(f'{case_id}: evidence missing at {evidence_path}')
            continue
        evidence = load(evidence_path)
        if evidence.get('case') != case_id:
            failures.append(f'{case_id}: evidence case id {evidence.get("case")}')
        if evidence.get('status') != 'passed':
            failures.append(f'{case_id}: evidence status {evidence.get("status")} (skipped or failed)')
        if evidence.get('implementationSha') != manifest['implementationSha']:
            failures.append(f'{case_id}: evidence implementation head {evidence.get("implementationSha")} '
                            f'!= manifest {manifest["implementationSha"]}')
        observation = evidence.get('observations', {})
        if case_id == 'E07':
            for name in ('single', 'mixed'):
                expected = case['expected']
                sub_expected = {
                    'requests': 1 if name == 'single' else expected['requests'],
                    'executions': {'tool0': 1} if name == 'single'
                    else {'tool0': 1, 'tool1': 1},
                    'resultStates': [{'callId': 'call-0', 'isError': True, 'terminate': name == 'single'}]
                    if name == 'single' else
                    [{'callId': 'call-0', 'isError': True, 'terminate': True},
                     {'callId': 'call-1', 'isError': False, 'terminate': False}],
                }
                verify_observation(f'E07-{name}', observation[name], sub_expected, failures)
        else:
            verify_observation(case_id, observation, case['expected'], failures)

    if covered_criteria != {item['id'] for item in manifest['ledgerCriteria']}:
        failures.append(f'manifest criteria mapping {sorted(covered_criteria)} does not cover every ledger criterion')

    ledger = load(repo / 'docs/parity/execution-ledger.json')
    task = next((item for item in ledger.get('tasks', []) if item.get('id') == 'tool-result-contracts'), None)
    if task is None:
        failures.append('execution ledger has no tool-result-contracts task')
    else:
        if task.get('status') != 'verified':
            failures.append(f'ledger status is {task.get("status")}, expected verified')
        ledger_criteria = set(task.get('acceptance', []))
        manifest_criteria = {item['text'] for item in manifest['ledgerCriteria']}
        if ledger_criteria != manifest_criteria:
            failures.append('manifest ledger criteria do not exactly match the ledger acceptance list')

    differential_path = repo / manifest['differentialEvidence']
    if not differential_path.exists():
        failures.append(f'differential evidence missing at {differential_path}')
    else:
        differential = load(differential_path)
        if differential.get('piSha') != manifest['piSha']:
            failures.append(f'differential Pi SHA {differential.get("piSha")} != manifest {manifest["piSha"]}')
        if differential.get('pisharpSha') != manifest['implementationSha']:
            failures.append(f'differential implementation SHA {differential.get("pisharpSha")} '
                            f'!= manifest {manifest["implementationSha"]}')
        if differential.get('summary', {}).get('unresolved') not in (0, None):
            failures.append(f'differential has unresolved cases: {differential["summary"]["unresolved"]}')
        observed = differential.get('cases', {})
        for case in cases:
            comparison = case.get('differential', {})
            status = comparison.get('status')
            target = comparison.get('case')
            if status == 'match':
                if target not in observed:
                    failures.append(f'{case["id"]}: differential case {target} missing')
                elif observed[target].get('status') != 'match':
                    failures.append(f'{case["id"]}: required differential {target} is '
                                    f'{observed[target].get("status")}')
            elif status == 'intentional-difference':
                if target not in observed:
                    failures.append(f'{case["id"]}: differential case {target} missing')
                elif not observed[target].get('classification'):
                    failures.append(f'{case["id"]}: differential {target} differs without classification')
                elif not comparison.get('reason'):
                    failures.append(f'{case["id"]}: intentional difference lacks a reason')
            elif status == 'covered-by':
                if not comparison.get('cases'):
                    failures.append(f'{case["id"]}: covered-by has no cases')
                for covered in comparison.get('cases', []):
                    if covered not in observed or observed[covered].get('status') != 'match':
                        failures.append(f'{case["id"]}: covering differential {covered} is not a match')
            elif status == 'not-applicable':
                if not comparison.get('reason'):
                    failures.append(f'{case["id"]}: not-applicable differential lacks a reason')
            else:
                failures.append(f'{case["id"]}: unknown differential status {status!r}')

    head = git(repo, 'rev-parse', 'HEAD')
    implementation = manifest['implementationSha']
    if head != implementation:
        try:
            subprocess.run(['git', 'merge-base', '--is-ancestor', implementation, head], cwd=repo,
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except subprocess.CalledProcessError:
            failures.append(f'implementation head {implementation} is not an ancestor of final head {head}')
        else:
            changed = subprocess.check_output(
                ['git', 'diff', '--name-only', f'{implementation}..{head}'], cwd=repo, text=True).splitlines()
            unexpected = [path for path in changed
                          if not any(path == allowed or path.startswith(allowed)
                                     for allowed in ALLOWED_EVIDENCE_COMMITS)]
            if unexpected:
                failures.append(f'implementation head {implementation} is historical but later commits touch '
                                f'non-evidence paths: {unexpected}')
            if not manifest.get('evidenceHeadPolicy'):
                failures.append('historical implementation head lacks an explicit evidenceHeadPolicy')

    summary = manifest.get('summary', {})
    if summary.get('total') != len(REQUIRED_IDS) or summary.get('passed') != len(REQUIRED_IDS):
        failures.append(f'manifest summary {summary} does not claim {len(REQUIRED_IDS)} passed cases')
    return failures


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument('--manifest', type=Path,
                        default=Path(__file__).resolve().parents[3] /
                        'docs/parity/fixtures/tool-result-contracts/acceptance-manifest.json')
    args = parser.parse_args(argv)
    failures = verify(args.repo, args.manifest)
    if failures:
        print(f'ACCEPTANCE FAILED ({len(failures)} violation(s)):')
        for failure in failures:
            print(f'  - {failure}')
        return 1
    print(f'ACCEPTANCE PASSED: {len(REQUIRED_IDS)} cases, ledger criteria and differential evidence verified.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
