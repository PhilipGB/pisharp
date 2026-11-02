#!/usr/bin/env python3
"""Verify the keys-editor-mutation acceptance manifest and its executable evidence."""
import argparse
import copy
import json
import os
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
REQUIRED_CASES = [f'M{index:02d}' for index in range(1, 16)]
TRX_NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


class VerificationError(RuntimeError):
    pass


def load_json(path):
    try:
        return json.loads(Path(path).read_text(encoding='utf-8'))
    except (OSError, ValueError) as error:
        raise VerificationError(f'Cannot read {path}: {error}') from error


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def validate_manifest(manifest, implementation):
    require(manifest.get('schema') == 1, 'Manifest schema must be 1.')
    require(manifest.get('capability') == 'keys-editor-mutation', 'Manifest capability must be keys-editor-mutation.')
    require(len(manifest.get('piSha', '')) == 40, 'Manifest must record the pinned Pi SHA.')
    recorded = manifest.get('implementationSha')
    require(len(recorded or '') == 40, 'Manifest must record a 40-character implementation SHA.')
    require(recorded == implementation,
            f'Stale implementation SHA: manifest has {recorded!r}, tested head is {implementation!r}.')
    cases = manifest.get('cases') or []
    ids = [case.get('id') for case in cases]
    require(len(ids) == len(set(ids)), 'Manifest contains duplicate case ids.')
    require(ids == REQUIRED_CASES, f'Manifest cases must be {REQUIRED_CASES}, found {ids}.')
    for case in cases:
        require(case.get('required') is True, f"{case['id']} must be required.")
        require(case.get('status') == 'pass',
                f"{case['id']} is {case.get('status')!r}; required cases may only be pass.")
        require(any(case.get(key) for key in ('oracle', 'tests', 'pty')),
                f"{case['id']} has no executable evidence.")
    comparisons = manifest.get('requiredComparisons') or []
    require(comparisons, 'Manifest has no required comparisons.')
    for comparison in comparisons:
        require(comparison.get('required') is True, f"Comparison {comparison.get('id')} must be required.")
        require(comparison.get('status') == 'match',
                f"Unresolved required comparison {comparison.get('id')}: {comparison.get('status')!r}.")
    for finding in manifest.get('findings') or []:
        require(finding.get('required') is False,
                f"Finding {finding.get('id')} expands the required contract.")
    tests = manifest.get('requiredTests') or {}
    require(tests.get('theories') and tests.get('facts') and tests.get('ptyEvidence'),
            'Manifest must list required theories, facts and PTY evidence ids.')


def validate_oracle(oracle, manifest):
    require(oracle.get('piSha') == manifest.get('piSha'),
            f"Oracle pin is {oracle.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    scenarios = oracle.get('scenarios') or {}
    comparison = next((item for item in manifest.get('requiredComparisons', [])
                       if item.get('id') == 'editor-buffer-pinned-pi-oracle'), None)
    require(comparison is not None, 'Manifest is missing the editor-buffer oracle comparison.')
    require(len(scenarios) == comparison.get('scenarios'),
            f"Oracle has {len(scenarios)} scenarios, manifest claims {comparison.get('scenarios')}.")
    for name, scenario in scenarios.items():
        steps = scenario.get('steps') or []
        expected = scenario.get('expected') or []
        require(len(steps) == len(expected), f'Oracle scenario {name!r} has mismatched steps and expectations.')
        require(expected, f'Oracle scenario {name!r} has no expected state.')
    for finding in oracle.get('findings') or []:
        require(finding.get('required') is False, f"Oracle finding {finding.get('id')} is marked required.")


def trx_results(path):
    try:
        tree = ET.parse(path)
    except (OSError, ET.ParseError) as error:
        raise VerificationError(f'Cannot read TRX {path}: {error}') from error
    results = []
    for element in tree.getroot().iter('{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult'):
        results.append((element.get('testName') or '', element.get('outcome') or ''))
    require(results, f'TRX {path} contains no test results.')
    return results


def validate_trx(results, manifest, oracle_count):
    tests = manifest['requiredTests']
    required = list(tests['theories']) + list(tests['facts'])
    skipped = {'NotExecuted', 'Skipped', 'Inconclusive'}
    relevant = [(name, outcome) for name, outcome in results
                if 'EditorMutation' in name or name.startswith(tuple(required))]
    require(relevant, 'TRX contains no keys-editor-mutation results.')
    for name, outcome in relevant:
        require(outcome not in skipped, f'Required test was skipped: {name} ({outcome}).')
        require(outcome == 'Passed', f'Required test failed: {name} ({outcome}).')
    theory = tests['theories'][0]
    passed_theories = [name for name, outcome in results
                       if name.startswith(theory) and outcome == 'Passed']
    require(len(passed_theories) == oracle_count,
            f'Expected {oracle_count} passing pinned-Pi theory cases, found {len(passed_theories)}.')
    for method in tests['facts']:
        require(any(name.startswith(method) and outcome == 'Passed' for name, outcome in results),
                f'Required test did not run or did not pass: {method}')


def validate_full_trx(results):
    skipped = {'NotExecuted', 'Skipped', 'Inconclusive'}
    failures = [name for name, outcome in results if outcome not in ('Passed',) and outcome not in skipped]
    skips = [name for name, outcome in results if outcome in skipped]
    require(not failures, f'Full suite has failing tests: {failures[:5]}')
    require(not skips, f'Full suite has skipped tests: {skips[:5]}')


def validate_evidence(path, manifest, implementation):
    try:
        lines = [line for line in Path(path).read_text(encoding='utf-8').splitlines() if line.strip()]
    except OSError as error:
        raise VerificationError(f'Cannot read PTY evidence {path}: {error}') from error
    require(lines, 'PTY evidence file is empty; the real Linux PTY cases did not run.')
    records = [json.loads(line) for line in lines]
    expected = manifest['requiredTests']['ptyEvidence']
    ids = [record.get('id') for record in records]
    require(sorted(ids) == sorted(expected), f'PTY evidence ids are {sorted(ids)}, expected {sorted(expected)}.')
    for record in records:
        require(record.get('implementation') == implementation,
                f"PTY evidence {record.get('id')} has stale implementation {record.get('implementation')!r}.")
        require(record.get('platform') == 'linux',
                f"PTY evidence {record.get('id')} did not run on Linux.")
        details = record.get('details') or {}
        require(details.get('exitCode') == 0, f"PTY evidence {record.get('id')} recorded a nonzero exit.")
    provider = next(record for record in records if record.get('id') == 'pty-provider-lifecycle')
    details = provider.get('details') or {}
    require(details.get('providerText') == 'provider visible input',
            'Provider lifecycle evidence does not contain the expected provider-visible text.')
    require(details.get('requests') == 2, 'Provider lifecycle evidence did not capture both requests.')
    require(details.get('termiosRestored') is True, 'Provider lifecycle evidence did not restore termios.')
    for record in records:
        if record.get('id') in ('pty-provider-lifecycle', 'pty-ctrl-d-exit'):
            require((record.get('details') or {}).get('termiosRestored') is True,
                    f"PTY evidence {record.get('id')} did not record terminal restoration.")


def validate_report_data(report, manifest):
    require(report.get('piSha') == manifest.get('piSha'),
            f"Report pin is {report.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    require(report.get('fixture') == 'terminal-editor-mutation',
            'Report is not for the terminal-editor-mutation fixture.')
    cases = report.get('cases') or []
    require(cases, 'Report has no terminal cases.')
    for case in cases:
        errors = case.get('scenarioErrors') or {}
        require(not errors, f"{case.get('id')} has scenario errors: {errors}")
        require(case.get('httpMatch') is True, f"{case.get('id')} provider HTTP traces differ.")


def run(command, env=None, timeout=1200):
    result = subprocess.run(command, cwd=ROOT, env=env, text=True, capture_output=True, timeout=timeout)
    if result.returncode != 0:
        tail = (result.stdout[-2000:] + result.stderr[-2000:]).strip()
        raise VerificationError(f'Command failed: {" ".join(command)}\n{tail}')
    return result.stdout


def run_focused_tests(implementation):
    directory = tempfile.mkdtemp(prefix='pisharp-mutation-verify-')
    trx = Path(directory) / 'mutation.trx'
    evidence = Path(directory) / 'pty-evidence.jsonl'
    environment = dict(os.environ)
    environment['PISHARP_EDITOR_MUTATION_EVIDENCE'] = str(evidence)
    environment['PISHARP_IMPLEMENTATION_SHA'] = implementation
    run(['dotnet', 'test', 'tests/PiSharp.Tests/PiSharp.Tests.csproj',
         '--filter', 'FullyQualifiedName~EditorMutationParityTests|FullyQualifiedName~EditorMutationPtyTests|' +
         'FullyQualifiedName~TerminalInputTests.DecodesLegacyControlBytesForMutationKeybindings|' +
         'FullyQualifiedName~EditorKeymapTests.ApplicationShortcutTakesPrecedenceOverEditorBindingInTheEditorContext|' +
         'FullyQualifiedName~EditorKeymapTests.HistoryBindingsRemainAboveApplicationShortcuts',
         '--logger', 'trx;LogFileName=mutation.trx', '--results-directory', directory,
         '--nologo', '-v', 'q'], env=environment)
    return trx, evidence


def self_test(manifest, oracle):
    def rejects(call, label):
        try:
            call()
        except VerificationError:
            print(f'negative control rejected: {label}')
            return
        raise VerificationError(f'Negative control accepted an invalid case: {label}')

    missing = copy.deepcopy(manifest)
    missing['cases'] = [case for case in missing['cases'] if case['id'] != 'M15']
    rejects(lambda: validate_manifest(missing, manifest['implementationSha']), 'missing matrix case')

    failed = copy.deepcopy(manifest)
    failed['cases'][0]['status'] = 'failed'
    rejects(lambda: validate_manifest(failed, manifest['implementationSha']), 'failed matrix case')

    unsupported = copy.deepcopy(manifest)
    unsupported['cases'][1]['status'] = 'unsupported'
    rejects(lambda: validate_manifest(unsupported, manifest['implementationSha']), 'unsupported matrix case')

    stale = copy.deepcopy(manifest)
    stale['implementationSha'] = '0' * 40
    rejects(lambda: validate_manifest(stale, manifest['implementationSha']), 'stale implementation SHA')

    unresolved = copy.deepcopy(manifest)
    unresolved['requiredComparisons'][0]['status'] = 'differs'
    rejects(lambda: validate_manifest(unresolved, manifest['implementationSha']), 'unresolved required comparison')

    bad_oracle = copy.deepcopy(oracle)
    bad_oracle['scenarios']['backspace deletes previous grapheme']['expected'].pop()
    rejects(lambda: validate_oracle(bad_oracle, manifest), 'incomplete oracle scenario')

    with tempfile.TemporaryDirectory(prefix='pisharp-mutation-self-test-') as directory:
        skipped = Path(directory) / 'skipped.trx'
        skipped.write_text(_trx_document([
            ('PiSharp.Tests.EditorMutationParityTests.MatchesPinnedPi(name: "x")', 'NotExecuted'),
            ('PiSharp.Tests.EditorMutationParityTests.CtrlDExitsOnlyWhenTheEditorIsEmpty', 'Passed'),
        ]), encoding='utf-8')
        rejects(lambda: validate_trx(trx_results(skipped), manifest, 1), 'skipped test')

        failed_trx = Path(directory) / 'failed.trx'
        failed_trx.write_text(_trx_document([
            ('PiSharp.Tests.EditorMutationParityTests.MatchesPinnedPi(name: "x")', 'Failed'),
            ('PiSharp.Tests.EditorMutationParityTests.CtrlDExitsOnlyWhenTheEditorIsEmpty', 'Passed'),
        ]), encoding='utf-8')
        rejects(lambda: validate_trx(trx_results(failed_trx), manifest, 1), 'failed test')

        evidence = Path(directory) / 'evidence.jsonl'
        evidence.write_text('', encoding='utf-8')
        rejects(lambda: validate_evidence(evidence, manifest, manifest['implementationSha']), 'missing PTY evidence')

        evidence.write_text(json.dumps({'id': 'pty-mutation-keys',
                                        'implementation': '0' * 40, 'platform': 'linux',
                                        'details': {'exitCode': 0}}) + '\n', encoding='utf-8')
        rejects(lambda: validate_evidence(evidence, manifest, manifest['implementationSha']),
                'stale PTY evidence')

    bad_report = {'fixture': 'terminal-editor-mutation', 'piSha': manifest['piSha'],
                  'cases': [{'id': '100x30-dark-fullscreen', 'scenarioErrors': {'pi': 'timeout'},
                             'httpMatch': True}]}
    rejects(lambda: validate_report_data(bad_report, manifest), 'unresolved terminal scenario error')
    bad_http = {'fixture': 'terminal-editor-mutation', 'piSha': manifest['piSha'],
                'cases': [{'id': '100x30-dark-fullscreen', 'scenarioErrors': {}, 'httpMatch': False}]}
    rejects(lambda: validate_report_data(bad_http, manifest), 'provider HTTP mismatch')
    print('negative controls passed')


def _trx_document(results):
    rows = ''.join(f'<UnitTestResult testName="{name}" outcome="{outcome}" />' for name, outcome in results)
    return ('<?xml version="1.0" encoding="utf-8"?>'
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            f'<Results>{rows}</Results></TestRun>')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path,
                        default=ROOT / 'docs/parity/fixtures/keys-editor-mutation-acceptance-2026-11-02.json')
    parser.add_argument('--oracle', type=Path,
                        default=ROOT / 'docs/parity/fixtures/keys-editor-mutation-pi-oracle-2026-11-02.json')
    parser.add_argument('--report', type=Path,
                        default=ROOT / 'artifacts/parity/keys-editor-mutation-paired/report.json')
    parser.add_argument('--implementation')
    parser.add_argument('--pi', type=Path)
    parser.add_argument('--trx', type=Path)
    parser.add_argument('--evidence', type=Path)
    parser.add_argument('--full-trx', type=Path)
    parser.add_argument('--skip-tests', action='store_true')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()

    implementation = args.implementation or subprocess.check_output(
        ['git', 'log', '-1', '--format=%H', '--', 'src', 'tests'], cwd=ROOT, text=True).strip()
    latest_implementation = subprocess.check_output(
        ['git', 'log', '-1', '--format=%H', '--', 'src', 'tests'], cwd=ROOT, text=True).strip()
    if implementation != latest_implementation:
        raise VerificationError(
            f'Stale implementation SHA: requested {implementation!r}, latest src/tests commit is '
            f'{latest_implementation!r}.')
    manifest = load_json(args.manifest)
    oracle = load_json(args.oracle)
    if args.self_test:
        self_test(manifest, oracle)
        return
    validate_manifest(manifest, implementation)
    validate_oracle(oracle, manifest)
    if args.pi:
        run(['python3', 'tools/parity/pi_reference.py', 'verify', '--pi', str(args.pi)])
    if args.full_trx:
        validate_full_trx(trx_results(args.full_trx))
    if args.trx and args.evidence:
        trx, evidence = args.trx, args.evidence
    elif args.skip_tests:
        raise VerificationError('--skip-tests requires --trx and --evidence; required executable evidence is missing.')
    else:
        trx, evidence = run_focused_tests(implementation)
    validate_trx(trx_results(trx), manifest, len(oracle['scenarios']))
    validate_evidence(evidence, manifest, implementation)
    report = load_json(args.report)
    validate_report_data(report, manifest)
    run(['python3', str(ROOT / manifest['terminalAssertion']), str(args.report)])
    print(f"keys-editor-mutation verified at {implementation}: 15 cases, "
          f"{len(oracle['scenarios'])} pinned-Pi scenarios, 24 terminal checkpoints.")


if __name__ == '__main__':
    try:
        main()
    except (VerificationError, subprocess.SubprocessError, OSError, ValueError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
