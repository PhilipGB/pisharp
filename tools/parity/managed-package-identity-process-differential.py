#!/usr/bin/env python3
"""Run focused pinned-Pi and PiSharp tests for managed package identity behavior."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import subprocess
from pathlib import Path
import xml.etree.ElementTree as ET


CASES = {
    'identity.npm': {
        'piTest': 'should resolve autoload-disabled project package entries as deltas over global packages',
        'pisharpTest': 'ManagedNpmIdentityIgnoresVersionAndInstallSource',
        'behavior': 'npm identity ignores version and install source',
    },
    'identity.git': {
        'piTest': 'should generate correct package identity for protocol and git:-prefixed URLs',
        'pisharpTest': 'SupportedGitSyntaxUsesVersionIndependentRepositoryIdentity',
        'behavior': 'supported Git URL forms share version-independent repository identity',
    },
    'identity.local': {
        'piTest': 'should dedupe same local package in global and project (project wins)',
        'pisharpTest': 'LocalPackageAliasesDeduplicateWithinUserScope',
        'behavior': 'canonical local aliases resolve as one package',
    },
    'identity.duplicate': {
        'piTest': 'should dedupe same local package in global and project (project wins)',
        'pisharpTest': 'NpmOwnsConfiguredRangeChecksForCompatibleAndIncompatibleCachedVersions',
        'behavior': 'duplicate package identities resolve once',
    },
    'identity.scope-override': {
        'piTest': 'should dedupe same local package in global and project (project wins)',
        'pisharpTest': 'ProjectPackageDeclarationOverridesSameCanonicalUserIdentity',
        'behavior': 'project declaration takes precedence over matching user declaration',
    },
    'identity.autoload-delta': {
        'piTest': 'should resolve autoload-disabled project package entries as deltas over global packages',
        'pisharpTest': 'DisabledProjectPackageEntryWinsCanonicalIdentityBeforeEnabledPersonalAlias',
        'behavior': 'autoload-disabled project declaration acts as a delta',
    },
    'identity.user-project-duplicates': {
        'piTest': 'should dedupe same local package in global and project (project wins)',
        'pisharpTest': 'ProjectPackageDeclarationOverridesSameCanonicalUserIdentity',
        'behavior': 'matching user and project package declarations are deduplicated',
    },
    'identity.local-symlink': {
        'piTest': 'should resolve symlinked user and project resources once',
        'pisharpTest': 'DisabledProjectPackageEntryWinsCanonicalIdentityBeforeEnabledPersonalAlias',
        'behavior': 'symlinked aliases use canonical package identity',
    },
}


def run(argv: list[str], cwd: Path, timeout: int) -> dict:
    started = dt.datetime.now(dt.timezone.utc).isoformat()
    try:
        result = subprocess.run(argv, cwd=cwd, capture_output=True, text=True, timeout=timeout)
        return {'argv': argv, 'cwd': str(cwd), 'startedAt': started, 'exitCode': result.returncode,
                'stdout': result.stdout, 'stderr': result.stderr}
    except (OSError, subprocess.TimeoutExpired) as error:
        return {'argv': argv, 'cwd': str(cwd), 'startedAt': started, 'exitCode': 127,
                'stdout': '', 'stderr': str(error)}


def parse_trx(path: Path) -> dict[str, str]:
    if not path.is_file():
        return {}
    root = ET.parse(path).getroot()
    return {item.get('testName', ''): item.get('outcome', '')
            for item in root.findall('.//{*}UnitTestResult')}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', required=True, type=Path)
    parser.add_argument('--expected-pi-sha', required=True)
    parser.add_argument('--pisharp-tests', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()

    pi_root = args.pi.resolve()
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    report_dir = output.parent / (output.stem + '-pi-results')
    report_dir.mkdir(parents=True, exist_ok=True)

    pi_sha_run = run(['git', 'rev-parse', 'HEAD'], pi_root, 30)
    pi_sha = pi_sha_run['stdout'].strip() if pi_sha_run['exitCode'] == 0 else None
    pi_test_results = {}
    commands = [pi_sha_run]
    for case_id, case in CASES.items():
        report_path = report_dir / (case_id.replace('.', '-') + '.json')
        argv = [
            'npm', '--prefix', str(pi_root / 'packages/coding-agent'), 'test', '--',
            'test/package-manager.test.ts', '-t', re.escape(case['piTest']), '--reporter=json',
            f'--outputFile={report_path}',
        ]
        command = run(argv, pi_root, 180)
        commands.append(command)
        test_report = {}
        try:
            test_report = json.loads(report_path.read_text(encoding='utf-8'))
        except (OSError, UnicodeError, json.JSONDecodeError):
            pass
        matched = [item for suite in test_report.get('testResults', [])
                   for item in suite.get('assertionResults', [])
                   if item.get('title') == case['piTest']]
        pi_test_results[case_id] = {
            'title': case['piTest'], 'exitCode': command['exitCode'],
            'passed': command['exitCode'] == 0 and len(matched) == 1 and matched[0].get('status') == 'passed',
            'matchingTests': [{'status': item.get('status'), 'title': item.get('title')} for item in matched],
            'reportPath': str(report_path),
        }

    methods = sorted({case['pisharpTest'] for case in CASES.values()})
    filter_value = '|'.join('FullyQualifiedName~' + method for method in methods)
    trx_path = output.parent / (output.stem + '.trx')
    argv = ['dotnet', 'test', str(args.pisharp_tests.resolve()), '--no-build', '--no-restore',
            '--filter', filter_value, '--logger', f'trx;LogFileName={trx_path.name}',
            '--results-directory', str(output.parent)]
    dotnet_command = run(argv, Path.cwd(), 900)
    commands.append(dotnet_command)
    dotnet_outcomes = parse_trx(trx_path)
    pisharp_test_results = {}
    for case_id, case in CASES.items():
        method = case['pisharpTest']
        matching = {name: outcome for name, outcome in dotnet_outcomes.items()
                    if method in name and outcome == 'Passed'}
        pisharp_test_results[case_id] = {
            'method': method, 'exitCode': dotnet_command['exitCode'],
            'passed': dotnet_command['exitCode'] == 0 and bool(matching),
            'matchingTests': matching,
        }

    case_results = {}
    for case_id, case in CASES.items():
        pi_passed = pi_test_results[case_id]['passed']
        pisharp_passed = pisharp_test_results[case_id]['passed']
        case_results[case_id] = {
            'behavior': case['behavior'], 'piPassed': pi_passed, 'pisharpPassed': pisharp_passed,
            'passed': pi_passed and pisharp_passed,
        }

    result = {
        'piSha': pi_sha,
        'expectedPiSha': args.expected_pi_sha,
        'piShaMatches': pi_sha == args.expected_pi_sha,
        'piTests': pi_test_results,
        'pisharpTests': pisharp_test_results,
        'cases': case_results,
        'commands': commands,
        'trxPath': str(trx_path),
        'match': pi_sha == args.expected_pi_sha and all(item['passed'] for item in case_results.values()),
    }
    output.write_text(json.dumps(result, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    print(json.dumps({'cases': len(case_results), 'match': result['match'], 'output': str(output)}))
    return 0 if result['match'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
