#!/usr/bin/env python3
"""Compare pinned Pi and PiSharp Git package update behavior in isolated processes."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/parity'))
from pi_reference import verify_pi_checkout

installer_path = Path(__file__).with_name('managed-git-install-process-differential.py')
installer_spec = importlib.util.spec_from_file_location('managed_git_install_process', installer_path)
if installer_spec is None or installer_spec.loader is None:
    raise RuntimeError(f'Could not load managed Git fixture helpers from {installer_path}')
installer = importlib.util.module_from_spec(installer_spec)
installer_spec.loader.exec_module(installer)
cli = installer.cli
resource = installer.resource


def set_remote_branch(fixture: dict, head: str | None) -> None:
    args = ['git', '--git-dir', str(fixture['repository'])]
    if head is None:
        subprocess.run([*args, 'update-ref', '-d', 'refs/heads/main'], check=True, capture_output=True, text=True)
    else:
        subprocess.run([*args, 'update-ref', 'refs/heads/main', head], check=True, capture_output=True, text=True)
        subprocess.run([*args, 'symbolic-ref', 'HEAD', 'refs/heads/main'], check=True, capture_output=True, text=True)


def source_for(fixture: dict, ref: str | None) -> str:
    source = fixture['unpinned_source']
    return f'{source}@{ref}' if ref else source


def new_products(profile: Path, scratch: Path, case_id: str) -> dict[str, dict]:
    products = {}
    for product in ('pi', 'pisharp'):
        products[product] = installer.create_product(profile / case_id, scratch / case_id / product, product)
    return products


def install_products(products: dict[str, dict], pi: Path, pisharp: Path, source: str,
                     expected_head: str) -> dict[str, dict]:
    results = {}
    for product, item in products.items():
        result = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], ['install', source])
        package_root = cli.find_git_package(item['root'], 'managed-git-package')
        head = cli.git_head(package_root, item['env'])
        if result['exitCode'] != 0 or head != expected_head:
            raise RuntimeError(f'{product} could not establish the Git update starting state: '
                               f'exit={result["exitCode"]}, head={head}, expected={expected_head}, '
                               f'stderr={result["stderr"]}')
        results[product] = {
            'packageRoot': package_root,
            'head': head,
            'content': cli.content_tree_digest(package_root),
            'settings': cli.setting_snapshot(item['agent'], item['cwd']),
            'sourceConfigured': source in cli.package_sources(cli.read_settings(item['agent'] / 'settings.json')),
        }
    return results


def add_git_shim(item: dict, mode: str, marker: Path | None = None) -> None:
    shim_dir = item['root'] / 'shim-bin'
    shim_dir.mkdir(parents=True, exist_ok=True)
    real_git = shutil.which('git')
    real_sleep = shutil.which('sleep') or '/usr/bin/sleep'
    if real_git is None:
        raise RuntimeError('git is unavailable')
    script = (
        '#!/bin/sh\n'
        'if [ "$PISHARP_TEST_GIT_UPDATE_MODE" = "fail-checkout" ]; then\n'
        '  if [ "$1" = "reset" ] || [ "$1" = "clone" ]; then exit 83; fi\n'
        'fi\n'
        'if [ "$PISHARP_TEST_GIT_UPDATE_MODE" = "fail-fetch" ]; then\n'
        '  if [ "$1" = "fetch" ] || [ "$1" = "clone" ]; then exit 89; fi\n'
        'fi\n'
        'if [ "$PISHARP_TEST_GIT_UPDATE_MODE" = "block" ]; then\n'
        '  if [ "$1" = "fetch" ] || [ "$1" = "clone" ]; then\n'
        '    echo started > "$PISHARP_TEST_GIT_UPDATE_MARKER"\n'
        f'    exec "{real_sleep}" 60\n'
        '  fi\n'
        'fi\n'
        f'exec "{real_git}" "$@"\n'
    )
    git_path = shim_dir / 'git'
    git_path.write_text(script, encoding='utf-8')
    git_path.chmod(0o755)
    item['env']['PATH'] = str(shim_dir) + os.pathsep + item['env'].get('PATH', '')
    item['env']['PISHARP_TEST_GIT_UPDATE_MODE'] = mode
    if marker is not None:
        item['env']['PISHARP_TEST_GIT_UPDATE_MARKER'] = str(marker)


def add_npm_failure_shim(item: dict) -> None:
    shim_dir = item['root'] / 'shim-bin'
    shim_dir.mkdir(parents=True, exist_ok=True)
    real_npm = shutil.which('npm')
    if real_npm is None:
        raise RuntimeError('npm is unavailable')
    script = (
        '#!/bin/sh\n'
        'if [ "$PISHARP_TEST_GIT_UPDATE_MODE" = "fail-npm" ] && [ "$1" = "install" ]; then exit 87; fi\n'
        f'exec "{real_npm}" "$@"\n'
    )
    npm_path = shim_dir / 'npm'
    npm_path.write_text(script, encoding='utf-8')
    npm_path.chmod(0o755)
    item['env']['PATH'] = str(shim_dir) + os.pathsep + item['env'].get('PATH', '')
    item['env']['PISHARP_TEST_GIT_UPDATE_MODE'] = 'fail-npm'


def run_cancelled_update(product: str, pi: Path, pisharp: Path, item: dict, source: str, marker: Path) -> dict:
    command = cli.command_for(product, pi, pisharp, ['update', '--extension', source, '--approve'])
    with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
        process = subprocess.Popen(command, cwd=item['cwd'], env=item['env'], stdout=stdout, stderr=stderr,
                                   start_new_session=True)
        deadline = time.monotonic() + 30
        started = False
        while time.monotonic() < deadline and process.poll() is None:
            if marker.is_file():
                started = True
                break
            time.sleep(0.05)
        if started and process.poll() is None:
            os.killpg(process.pid, signal.SIGINT)
        reaped = True
        try:
            exit_code = process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=10)
            exit_code, reaped = process.returncode, False
        stdout.seek(0)
        stderr.seek(0)
        return {'exitCode': exit_code, 'started': started, 'processReaped': reaped,
                'stdout': stdout.read().decode(errors='replace'),
                'stderr': stderr.read().decode(errors='replace')}


def capture_after_update(item: dict, source: str, initial: dict, command: dict,
                         extra: dict | None = None) -> dict:
    package_root = cli.find_git_package(item['root'], 'managed-git-package')
    settings_after = cli.setting_snapshot(item['agent'], item['cwd'])
    configured = source in cli.package_sources(cli.read_settings(item['agent'] / 'settings.json'))
    content = cli.content_tree_digest(package_root) if package_root else None
    head = cli.git_head(package_root, item['env']) if package_root else None
    revision_path = package_root / 'revision.txt' if package_root else None
    revision = revision_path.read_text(encoding='utf-8').strip() if revision_path and revision_path.is_file() else None
    state = {
        'updateExitCode': 0 if command.get('exitCode') == 0 else 1,
        'sourceConfigured': configured,
        'settingsUnchanged': initial['settings'] == settings_after,
        'initialHead': initial['head'],
        'head': head,
        'revision': revision,
        'contentUnchanged': initial['content'] == content,
        'packageExists': package_root is not None,
    }
    if extra:
        state.update(extra)
    return state


def apply_case_mutation(case_id: str, fixture: dict, products: dict[str, dict], initial: dict[str, dict],
                        source: str, pi: Path, pisharp: Path, root: Path, port: int) -> dict[str, dict]:
    if case_id == 'git.update-moving' or case_id == 'git.update-failed-checkout' \
            or case_id == 'git.update-dependency-failure' or case_id == 'git.update-cancelled' \
            or case_id == 'git.update-recovery':
        set_remote_branch(fixture, fixture['v2_head'])
    elif case_id == 'git.update-changed-remote':
        set_remote_branch(fixture, fixture['v1_head'])
    elif case_id == 'git.update-no-change':
        set_remote_branch(fixture, fixture['v1_head'])
    elif case_id in ('git.update-pinned', 'git.update-pinned-commit', 'git.update-deleted-branch'):
        set_remote_branch(fixture, None if case_id == 'git.update-deleted-branch' else fixture['v2_head'])
    elif case_id == 'git.update-pinned-tag':
        set_remote_branch(fixture, fixture['v2_head'])
        subprocess.run(['git', '--git-dir', str(fixture['repository']), 'update-ref', '-d', 'refs/tags/v1'],
                       check=True, capture_output=True, text=True)
        subprocess.run(['git', '--git-dir', str(fixture['repository']), 'update-ref', 'refs/tags/v1',
                        fixture['v2_head']], check=True, capture_output=True, text=True)
    elif case_id == 'git.update-failed-fetch':
        set_remote_branch(fixture, fixture['v2_head'])
    else:
        raise ValueError(f'Unsupported Git update case: {case_id}')

    if case_id == 'git.update-failed-checkout':
        for item in products.values():
            add_git_shim(item, 'fail-checkout')
    elif case_id == 'git.update-failed-fetch':
        for item in products.values():
            add_git_shim(item, 'fail-fetch')
    elif case_id in ('git.update-dependency-failure', 'git.update-recovery'):
        for item in products.values():
            add_npm_failure_shim(item)
    elif case_id == 'git.update-cancelled':
        for product, item in products.items():
            marker = item['root'] / 'update-blocked.marker'
            add_git_shim(item, 'block', marker)
    elif case_id == 'git.update-changed-remote':
        alternate = fixture['repository'].parent / 'alternate.git'
        source_tree = root / 'managed-git-source'
        subprocess.run(['git', 'clone', '--bare', str(source_tree), str(alternate)],
                       check=True, capture_output=True, text=True)
        subprocess.run(['git', '--git-dir', str(alternate), 'update-ref', 'refs/heads/main', fixture['v2_head']],
                       check=True, capture_output=True, text=True)
        subprocess.run(['git', '--git-dir', str(alternate), 'symbolic-ref', 'HEAD', 'refs/heads/main'],
                       check=True, capture_output=True, text=True)
        set_remote_branch({'repository': alternate}, fixture['v2_head'])
        changed_url = f'git://127.0.0.1:{port}/fixture/alternate.git'
        for product, item in products.items():
            package_root = initial[product]['packageRoot']
            subprocess.run(['git', '-C', str(package_root), 'remote', 'set-url', 'origin', changed_url],
                           env=item['env'], check=True, capture_output=True, text=True)

    outcomes = {}
    for product, item in products.items():
        if case_id == 'git.update-cancelled':
            marker = Path(item['env']['PISHARP_TEST_GIT_UPDATE_MARKER'])
            command = run_cancelled_update(product, pi, pisharp, item, source, marker)
        else:
            command = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'],
                                  ['update', '--extension', source, '--approve'])

        if case_id == 'git.update-recovery':
            first_exit = 0 if command['exitCode'] == 0 else 1
            item['env'].pop('PISHARP_TEST_GIT_UPDATE_MODE', None)
            second = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'],
                                 ['update', '--extension', source, '--approve'])
            outcomes[product] = capture_after_update(item, source, initial[product], second,
                {'firstUpdateExitCode': first_exit, 'recoveryExitCode': 0 if second['exitCode'] == 0 else 1})
        else:
            extra = None
            if case_id == 'git.update-cancelled':
                extra = {'operationStarted': command.get('started') is True,
                         'processReaped': command.get('processReaped') is True}
            outcomes[product] = capture_after_update(item, source, initial[product], command, extra)
    return outcomes


def expected_head_for(case_id: str, fixture: dict) -> str:
    return fixture['v2_head'] if case_id in ('git.update-moving', 'git.update-pinned-tag',
                                              'git.update-changed-remote', 'git.update-recovery') else fixture['v1_head']


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.pi, args.pisharp = args.pi.resolve(), args.pisharp.resolve()
    args.profile, args.output = args.profile.resolve(), args.output.resolve()
    args.profile.mkdir(parents=True, exist_ok=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    pi_sha = verify_pi_checkout(args.pi)
    case_ids = [
        'git.update-moving', 'git.update-no-change', 'git.update-pinned', 'git.update-failed-fetch',
        'git.update-failed-checkout', 'git.update-dependency-failure', 'git.update-cancelled',
        'git.update-recovery', 'git.update-pinned-tag', 'git.update-pinned-commit',
        'git.update-changed-remote', 'git.update-deleted-branch',
    ]
    cases = {}
    with tempfile.TemporaryDirectory(prefix='managed-git-update-', dir=str(args.profile)) as scratch_name:
        scratch = Path(scratch_name)
        fixture_setup = installer.create_product(args.profile / 'fixture-profile', scratch / 'fixture-env', 'pi')
        fixture = resource.create_managed_git_repository(scratch, args.pi, fixture_setup['env'])
        fixture['daemon'] = cli.register_daemon(fixture['daemon'])
        port = int(fixture['unpinned_source'].rsplit(':', 1)[1].split('/')[0])
        changed_source_tree = scratch / 'managed-git-source'

        for index, case_id in enumerate(case_ids):
            set_remote_branch(fixture, fixture['v1_head'])
            if case_id in ('git.update-pinned', 'git.update-pinned-tag', 'git.update-pinned-commit'):
                ref = 'v1' if case_id in ('git.update-pinned', 'git.update-pinned-tag') else fixture['v1_head']
            else:
                ref = None
            source = source_for(fixture, ref)
            case_root = scratch / 'case-roots' / f'{index}-{case_id}'
            products = new_products(args.profile, case_root, case_id)
            initial = install_products(products, args.pi, args.pisharp, source, fixture['v1_head'])
            outcomes = apply_case_mutation(case_id, fixture, products, initial, source,
                                           args.pi, args.pisharp, scratch, port)
            equal = outcomes['pi'] == outcomes['pisharp']
            expected_head = expected_head_for(case_id, fixture)
            for product in ('pi', 'pisharp'):
                state = outcomes[product]
                success_cases = {'git.update-moving', 'git.update-pinned-tag',
                                 'git.update-changed-remote', 'git.update-recovery'}
                if case_id in success_cases:
                    state['contractSatisfied'] = (state['updateExitCode'] == 0 and state['head'] == expected_head
                                                  and state['sourceConfigured'] and state['settingsUnchanged'])
                elif case_id == 'git.update-no-change':
                    state['contractSatisfied'] = (state['updateExitCode'] == 0 and state['head'] == expected_head
                                                  and state['contentUnchanged'] and state['settingsUnchanged'])
                elif case_id in ('git.update-pinned', 'git.update-pinned-commit'):
                    state['contractSatisfied'] = (state['updateExitCode'] == 0 and state['head'] == expected_head
                                                  and state['settingsUnchanged'])
                elif case_id == 'git.update-cancelled':
                    state['contractSatisfied'] = (state['updateExitCode'] != 0 and state['head'] == expected_head
                                                  and state['operationStarted'] and state['processReaped']
                                                  and state['settingsUnchanged'])
                elif case_id == 'git.update-dependency-failure':
                    if product == 'pi':
                        state['contractSatisfied'] = (state['updateExitCode'] != 0
                                                      and state['head'] == fixture['v2_head']
                                                      and not state['contentUnchanged']
                                                      and state['settingsUnchanged'])
                    else:
                        state['contractSatisfied'] = (state['updateExitCode'] != 0
                                                      and state['head'] == fixture['v1_head']
                                                      and state['contentUnchanged']
                                                      and state['settingsUnchanged'])
                else:
                    state['contractSatisfied'] = (state['updateExitCode'] != 0 and state['head'] == expected_head
                                                  and state['contentUnchanged'] and state['settingsUnchanged'])
            safety_difference_accepted = False
            if case_id == 'git.update-dependency-failure':
                pi_state, pisharp_state = outcomes['pi'], outcomes['pisharp']
                safety_difference_accepted = (
                    not equal
                    and pi_state['updateExitCode'] != 0 and pisharp_state['updateExitCode'] != 0
                    and pi_state['head'] == fixture['v2_head']
                    and pisharp_state['head'] == fixture['v1_head']
                    and pi_state['contentUnchanged'] is False
                    and pisharp_state['contentUnchanged'] is True
                    and pi_state['settingsUnchanged'] is True
                    and pisharp_state['settingsUnchanged'] is True
                )
            cases[case_id] = {'oracle': outcomes['pi'], 'actual': outcomes['pisharp'], 'equal': equal,
                              'safetyDifferenceAccepted': safety_difference_accepted}

        cli.stop_daemon(fixture['daemon'])

    match = all((case['equal'] or case.get('safetyDifferenceAccepted') is True)
                and case['oracle']['contractSatisfied'] and case['actual']['contractSatisfied']
                for case in cases.values())
    report = {
        'piSha': pi_sha,
        'piSharpBaseSha': subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT,
                                         check=True, capture_output=True, text=True).stdout.strip(),
        'fixtureVersion': 'managed-git-update-v1',
        'scenario': 'paired-managed-git-update-local-daemon',
        'match': match,
        'cases': cases,
    }
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    print(json.dumps({'cases': len(cases), 'match': match, 'output': str(args.output)}, sort_keys=True))
    return 0 if match else 1


if __name__ == '__main__':
    raise SystemExit(main())
