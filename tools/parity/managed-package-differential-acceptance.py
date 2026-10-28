#!/usr/bin/env python3
"""Calibrate pinned Pi package fixtures and exercise the differential comparator."""

from __future__ import annotations

import argparse
import copy
import datetime as dt
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/parity'))
from pi_reference import verify_pi_checkout

FIXTURE_VERSION = 'managed-package-d0-v1'
TEMP_DIRECTORY_PATTERN = re.compile(r'(package-cli|managed-npm|managed-git-install)-[A-Za-z0-9_.-]+')
LOOPBACK_PORT_PATTERN = re.compile(r'(127\.0\.0\.1:|localhost:)[0-9]+')
NPM_LOG_TIMESTAMP_PATTERN = re.compile(r'(_logs/)[^/]+-debug(?:-[0-9]+)?\.log')


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode('utf-8')


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def normalize(value: Any, roots: list[tuple[str, str]]) -> Any:
    if isinstance(value, dict):
        return {
            key: ('<port>' if key == 'gitDaemonPort'
                  else ({nested_key: ('<timing-dependent cancellation diagnostics>'
                                      if nested_key == 'stderr'
                                      else normalize(nested_value, roots))
                        for nested_key, nested_value in item.items()}
                       if key == 'npmInstallCancelledDiagnostics' and isinstance(item, dict)
                       else normalize(item, roots)))
            for key, item in sorted(value.items())
        }
    if isinstance(value, list):
        return [normalize(item, roots) for item in value]
    if isinstance(value, str):
        for source, replacement in roots:
            value = value.replace(source, replacement)
            app_normalized = re.sub(r'\bpisharp\b', '<app>', source)
            value = value.replace(app_normalized, replacement)
        value = TEMP_DIRECTORY_PATTERN.sub(lambda match: match.group(1) + '-<run>', value)
        value = LOOPBACK_PORT_PATTERN.sub(lambda match: match.group(1) + '<port>', value)
        return NPM_LOG_TIMESTAMP_PATTERN.sub(r'\1<timestamp>-debug.log', value)
    return value


def git_identity_aliases(name: str, report: dict[str, Any]) -> dict[str, str]:
    aliases: dict[str, str] = {}
    if name == 'package-cli':
        oracle = report.get('results', {}).get('pi', {})
        v1, v2 = oracle.get('gitHeadBeforeUpdate'), oracle.get('gitHeadAfterUpdate')
    else:
        cases = report.get('cases', {})
        v1 = cases.get('git.install-branch', {}).get('oracle', {}).get('expectedHead')
        v2 = cases.get('git.install-commit', {}).get('oracle', {}).get('expectedHead')
    for identity, label in ((v1, '<fixture-v1-head>'), (v2, '<fixture-v2-head>')):
        if isinstance(identity, str) and re.fullmatch(r'[0-9a-f]{40}', identity):
            aliases[identity] = label
            aliases[identity[:7]] = label.replace('>', '-short>')
    return aliases


def replace_git_identities(value: Any, aliases: dict[str, str]) -> Any:
    if isinstance(value, dict):
        return {key: replace_git_identities(item, aliases) for key, item in value.items()}
    if isinstance(value, list):
        return [replace_git_identities(item, aliases) for item in value]
    if isinstance(value, str):
        for identity, label in sorted(aliases.items(), key=lambda item: len(item[0]), reverse=True):
            value = value.replace(identity, label)
    return value


def read_report(path: Path, expected_pi_sha: str) -> dict[str, Any]:
    try:
        report = json.loads(path.read_text(encoding='utf-8'))
    except (OSError, json.JSONDecodeError) as error:
        raise RuntimeError(f'Cannot read runner report {path}: {error}') from error
    if report.get('piSha') != expected_pi_sha:
        raise RuntimeError(f'Runner report {path} has a stale or mismatched Pi SHA')
    return report


def run_fixture(name: str, command: list[str], baseline_path: Path, repeat_output: Path,
                repeat_profile: Path, pi_path: Path, pisharp_path: Path,
                baseline_profile: Path, output_root: Path, expected_pi_sha: str) -> dict[str, Any]:
    repeat_output.parent.mkdir(parents=True, exist_ok=True)
    repeat_profile.mkdir(parents=True, exist_ok=True)
    started = dt.datetime.now(dt.timezone.utc).isoformat()
    try:
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=900)
        exit_code = completed.returncode
        stdout = completed.stdout
        stderr = completed.stderr
    except subprocess.TimeoutExpired as error:
        exit_code = 124
        stdout = error.stdout.decode(errors='replace') if isinstance(error.stdout, bytes) else (error.stdout or '')
        stderr = error.stderr.decode(errors='replace') if isinstance(error.stderr, bytes) else (error.stderr or '')
        stderr += '\nDifferential acceptance fixture timed out after 900 seconds.'
    except OSError as error:
        exit_code, stdout, stderr = 127, '', str(error)

    baseline = read_report(baseline_path, expected_pi_sha)
    repeat = read_report(repeat_output, expected_pi_sha) if repeat_output.is_file() else {}
    if name == 'package-cli':
        baseline_oracle = baseline.get('results', {}).get('pi')
        repeat_oracle = repeat.get('results', {}).get('pi')
    elif name == 'managed-npm':
        baseline_oracle = baseline.get('pi')
        repeat_oracle = repeat.get('pi')
    elif name == 'managed-git-install':
        baseline_oracle = {case_id: item.get('oracle') for case_id, item in baseline.get('cases', {}).items()}
        repeat_oracle = {case_id: item.get('oracle') for case_id, item in repeat.get('cases', {}).items()}
    else:
        raise RuntimeError(f'Unknown calibration fixture: {name}')

    roots = [
        (str(pi_path.resolve()), '<pinned-pi>'),
        (str(pisharp_path.resolve()), '<pisharp-binary>'),
        (str(baseline_profile.resolve()), '<fixture-profile>'),
        (str(repeat_profile.resolve()), '<fixture-profile>'),
        (str(output_root.resolve()), '<evidence-root>'),
        (str(ROOT.resolve()), '<repository>'),
    ]
    roots.sort(key=lambda item: len(item[0]), reverse=True)
    baseline_normalized = normalize(
        replace_git_identities(baseline_oracle, git_identity_aliases(name, baseline)), roots)
    repeat_normalized = normalize(
        replace_git_identities(repeat_oracle, git_identity_aliases(name, repeat)), roots)
    baseline_sha = hashlib.sha256(canonical_bytes(baseline_normalized)).hexdigest()
    repeat_sha = hashlib.sha256(canonical_bytes(repeat_normalized)).hexdigest()
    runner_ok = bool(exit_code == 0 and baseline.get('match') is True and repeat.get('match') is True
                     and isinstance(repeat_oracle, (dict, list)) and repeat_oracle)
    equal = baseline_normalized == repeat_normalized
    return {
        'fixture': name,
        'fixtureVersion': FIXTURE_VERSION,
        'commandIdentity': Path(command[1]).resolve().relative_to(ROOT).as_posix()
        if len(command) > 1 else command[0],
        'command': command,
        'startedAt': started,
        'exitCode': exit_code,
        'stdout': stdout,
        'stderr': stderr,
        'baselineReport': str(baseline_path.resolve()),
        'baselineReportSha256': sha256_file(baseline_path),
        'repeatReport': str(repeat_output.resolve()) if repeat_output.is_file() else None,
        'repeatReportSha256': sha256_file(repeat_output) if repeat_output.is_file() else None,
        'baselineNormalizedOracleSha256': baseline_sha,
        'repeatNormalizedOracleSha256': repeat_sha,
        'normalizedOracleEqual': equal,
        'runnerPassed': runner_ok,
        'baselineCaseCount': len(baseline_oracle) if isinstance(baseline_oracle, dict) else 0,
        'repeatCaseCount': len(repeat_oracle) if isinstance(repeat_oracle, dict) else 0,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output-root', type=Path, required=True)
    parser.add_argument('--baseline-profile-root', type=Path, required=True)
    parser.add_argument('--baseline-package-cli', type=Path, required=True)
    parser.add_argument('--baseline-managed-npm', type=Path, required=True)
    parser.add_argument('--baseline-managed-git', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()

    args.pi = args.pi.resolve()
    args.pisharp = args.pisharp.resolve()
    args.profile = args.profile.resolve()
    args.output_root = args.output_root.resolve()
    args.baseline_profile_root = args.baseline_profile_root.resolve()
    args.output = args.output.resolve()
    pi_sha = verify_pi_checkout(args.pi)
    args.profile.mkdir(parents=True, exist_ok=True)

    specifications = [
        ('package-cli', ROOT / 'tools/parity/package-cli-process-differential.py',
         args.baseline_package_cli, args.output_root / 'd0-repeat/package-cli.json',
         args.baseline_profile_root / 'cli',
         ['--pi', str(args.pi), '--pisharp', str(args.pisharp)]),
        ('managed-npm', ROOT / 'tools/parity/managed-npm-process-differential.py',
         args.baseline_managed_npm, args.output_root / 'd0-repeat/managed-npm.json',
         args.baseline_profile_root / 'npm', ['--pi', str(args.pi)]),
        ('managed-git-install', ROOT / 'tools/parity/managed-git-install-process-differential.py',
         args.baseline_managed_git, args.output_root / 'd0-repeat/managed-git-install.json',
         args.baseline_profile_root / 'managed-git-install',
         ['--pi', str(args.pi), '--pisharp', str(args.pisharp)]),
    ]

    calibrations = {}
    for name, runner, baseline_path, repeat_path, baseline_profile, args_for_runner in specifications:
        repeat_profile = Path(tempfile.mkdtemp(prefix=f'{name}-', dir=args.profile))
        command = [sys.executable, str(runner), *args_for_runner,
                   '--profile', str(repeat_profile), '--output', str(repeat_path)]
        calibrations[name] = run_fixture(
            name, command, baseline_path, repeat_path, repeat_profile,
            args.pi, args.pisharp, baseline_profile, args.output_root, pi_sha)

    git_report = read_report(args.output_root / 'd0-repeat/managed-git-install.json', pi_sha)
    git_case = git_report.get('cases', {}).get('git.install-branch', {})
    oracle = copy.deepcopy(git_case.get('oracle'))
    actual = copy.deepcopy(git_case.get('actual'))
    if not isinstance(oracle, dict) or not isinstance(actual, dict):
        negative_control = {'executed': False, 'detectedMismatch': False,
                            'reason': 'Git branch case did not produce both oracle and actual states'}
    else:
        original_equal = normalize(oracle, [(str(args.profile), '<profile>')]) == normalize(
            actual, [(str(args.profile), '<profile>')])
        original_head = actual.get('head')
        actual['head'] = '0' * 40 if original_head != '0' * 40 else '1' * 40
        mutated_equal = normalize(oracle, [(str(args.profile), '<profile>')]) == normalize(
            actual, [(str(args.profile), '<profile>')])
        negative_control = {
            'executed': True,
            'sourceCase': 'git.install-branch',
            'mutatedField': 'head',
            'originalHead': original_head,
            'mutatedHead': actual['head'],
            'originalComparisonEqual': original_equal,
            'mutatedComparisonEqual': mutated_equal,
            'detectedMismatch': original_equal and not mutated_equal,
        }

    command_status = {name: {
        'exitCode': item['exitCode'], 'runnerPassed': item['runnerPassed'],
        'normalizedOracleEqual': item['normalizedOracleEqual']}
        for name, item in calibrations.items()}
    match = all(item['runnerPassed'] and item['normalizedOracleEqual'] for item in calibrations.values()) \
        and negative_control.get('detectedMismatch') is True
    source = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, capture_output=True, text=True, check=True)
    report = {
        'schemaVersion': 1,
        'fixtureVersion': FIXTURE_VERSION,
        'scenario': 'independent-pinned-pi-calibration-and-negative-control',
        'piSha': pi_sha,
        'pisharpSourceSha': source.stdout.strip(),
        'generatedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
        'argv': sys.argv,
        'commandResults': command_status,
        'calibrations': calibrations,
        'normalizationRules': [
            'Independent disposable profile roots and random temporary-directory suffixes map to stable labels.',
            'Ephemeral loopback ports map to a stable marker.',
            'Fixture Git commit hashes map to v1/v2 roles while preserving branch and commit assertions.',
            'Npm debug-log filenames map their run timestamps to a stable marker.',
            'Only optional stderr text from the interrupted npm cancellation is normalized; exit code and stdout remain compared.',
        ],
        'negativeControl': negative_control,
        'match': match,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(canonical_bytes(report) + b'\n')
    print(json.dumps({'match': match, 'piSha': pi_sha, 'calibrations': command_status,
                      'negativeControl': negative_control}, sort_keys=True))
    return 0 if match else 1


if __name__ == '__main__':
    raise SystemExit(main())
