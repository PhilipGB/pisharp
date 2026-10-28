#!/usr/bin/env python3
"""Compare bulk package update selection and failure behavior in Pi and PiSharp processes."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/parity'))
from pi_reference import verify_pi_checkout

git_update_path = Path(__file__).with_name('managed-git-update-process-differential.py')
git_update_spec = importlib.util.spec_from_file_location('managed_git_update_process', git_update_path)
if git_update_spec is None or git_update_spec.loader is None:
    raise RuntimeError(f'Could not load managed Git update fixture helpers from {git_update_path}')
git_update = importlib.util.module_from_spec(git_update_spec)
git_update_spec.loader.exec_module(git_update)
cli = git_update.cli
installer = git_update.installer
resource = git_update.resource


def load_module(path: Path, module_name: str):
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f'Could not load fixture helpers from {path}')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


package_update = load_module(Path(__file__).with_name('managed-npm-update-process-differential.py'),
                             'managed_npm_update_process')
package_cli = load_module(Path(__file__).with_name('package-cli-process-differential.py'),
                          'package_cli_process_for_bulk')


def set_remote_branch(fixture: dict, head: str | None) -> None:
    git_update.set_remote_branch(fixture, head)


def set_remote_tag(fixture: dict, tag: str, head: str) -> None:
    args = ['git', '--git-dir', str(fixture['repository'])]
    subprocess.run([*args, 'update-ref', '-d', f'refs/tags/{tag}'], check=True,
                   capture_output=True, text=True)
    subprocess.run([*args, 'update-ref', f'refs/tags/{tag}', head], check=True,
                   capture_output=True, text=True)


def npm_install_root(item: dict, package_name: str, scope: str) -> Path:
    if scope == 'project':
        return item['cwd'] / '.pi/npm/node_modules' / package_name
    return item['agent'] / 'npm/node_modules' / package_name


def npm_version(item: dict, package_name: str, scope: str) -> str | None:
    manifest = npm_install_root(item, package_name, scope) / 'package.json'
    if not manifest.is_file():
        return None
    return json.loads(manifest.read_text(encoding='utf-8')).get('version')


def git_install_root(item: dict, scope: str) -> Path | None:
    search_root = item['cwd'] / '.pi' if scope == 'project' else item['agent']
    for manifest in search_root.rglob('package.json'):
        try:
            if json.loads(manifest.read_text(encoding='utf-8')).get('name') == 'managed-git-package' \
                    and (manifest.parent / '.git').exists():
                return manifest.parent
        except (OSError, json.JSONDecodeError):
            continue
    return None


def package_revision(item: dict, package: dict) -> str | None:
    if package['kind'] == 'npm':
        return npm_version(item, package['name'], package['scope'])
    path = git_install_root(item, package['scope'])
    if path is None:
        return None
    revision = path / 'revision.txt'
    return revision.read_text(encoding='utf-8').strip() if revision.is_file() else 'v1'


def new_products(profile: Path, scratch: Path, case: dict, packages: list[dict],
                 pi: Path, pisharp: Path, registry, git_fixture: dict) -> dict[str, dict]:
    products = {}
    for product in ('pi', 'pisharp'):
        item = installer.create_product(profile / case['caseId'] / product,
                                        scratch / case['caseId'] / product, product)
        item['env'].update(
            npm_config_registry=registry.url,
            npm_config_offline='false', npm_config_audit='false', npm_config_fund='false',
            npm_config_fetch_retries='0', npm_config_fetch_timeout='5000',
            npm_config_prefer_online='true', npm_config_cache_min='0',
        )
        (item['cwd'] / '.pi/extension-resources').mkdir(parents=True, exist_ok=True)
        for package in packages:
            args = ['install']
            if package['scope'] == 'project':
                args.extend(['--local', '--approve'])
            args.append(package['source'])
            result = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], args)
            if result['exitCode'] != 0:
                raise RuntimeError(f'{product} could not install {package["source"]}: {result["stderr"]}')
        products[product] = item
    return products


def capture_state(item: dict, packages: list[dict], before_settings: dict, command: dict,
                  expected: dict, *, cancelled: bool = False, started: bool = True,
                  process_reaped: bool = True, trusted: bool = True) -> dict:
    revisions = {package['key']: package_revision(item, package) for package in packages}
    sources = {
        'user': cli.package_sources(cli.read_settings(item['agent'] / 'settings.json')),
        'project': cli.package_sources(cli.read_settings(item['cwd'] / '.pi/settings.json')),
    }
    state = {
        'updateExitCode': (130 if cancelled and command['exitCode'] != 0
                           else 0 if command['exitCode'] == 0 else 1),
        'revisions': revisions,
        'sources': sources,
        'settingsUnchanged': before_settings == cli.setting_snapshot(item['agent'], item['cwd']),
        'projectTrusted': trusted,
        'operationStarted': started,
        'processReaped': process_reaped,
    }
    state['contractSatisfied'] = expected(state)
    return state


def run_update(product: str, pi: Path, pisharp: Path, item: dict, args: list[str]) -> dict:
    return cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], ['update', *args])


def run_cancelled_update(product: str, pi: Path, pisharp: Path, item: dict,
                         blocked_registry, args: list[str]) -> tuple[dict, bool, bool]:
    item['env']['npm_config_registry'] = blocked_registry.url
    command = cli.command_for(product, pi, pisharp, ['update', *args])
    with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
        process = subprocess.Popen(command, cwd=item['cwd'], env=item['env'], stdout=stdout,
                                   stderr=stderr, start_new_session=True)
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline and process.poll() is None \
                and not blocked_registry.tarball_requested.is_set():
            time.sleep(0.05)
        started = blocked_registry.tarball_requested.is_set()
        if started and process.poll() is None:
            os.killpg(process.pid, signal.SIGINT)
        reaped = True
        try:
            exit_code = process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            exit_code = process.wait(timeout=15)
            reaped = False
        finally:
            blocked_registry.release_tarball.set()
        stdout.seek(0)
        stderr.seek(0)
        result = {'exitCode': exit_code,
                  'stdout': stdout.read().decode(errors='replace'),
                  'stderr': stderr.read().decode(errors='replace')}
    return result, started, reaped


def case_specs(fixture: dict) -> list[dict]:
    a = lambda case: f'bulk-{case}-a'
    b = lambda case: f'bulk-{case}-b'
    return [
        {'caseId': 'bulk.selective', 'packages': [
            {'key': 'a', 'kind': 'npm', 'name': a('selective'), 'scope': 'user', 'version': 'range'},
            {'key': 'b', 'kind': 'npm', 'name': b('selective'), 'scope': 'user', 'version': 'range'},
            {'key': 'git', 'kind': 'git', 'scope': 'user', 'version': 'floating'},
        ], 'target': ['npm:' + a('selective') + '@^1.0.0'], 'advanceGit': True,
           'advancePackages': ['a', 'b'], 'expect': 'selective'},
        {'caseId': 'bulk.extensions', 'packages': [
            {'key': 'a', 'kind': 'npm', 'name': a('extensions'), 'scope': 'user', 'version': 'range'},
            {'key': 'git', 'kind': 'git', 'scope': 'user', 'version': 'floating'},
        ], 'target': ['--extensions'], 'advanceGit': True, 'advancePackages': ['a'], 'expect': 'all'},
        {'caseId': 'bulk.mixed-sources', 'packages': [
            {'key': 'a', 'kind': 'npm', 'name': a('mixed'), 'scope': 'user', 'version': 'range'},
            {'key': 'b', 'kind': 'npm', 'name': b('mixed'), 'scope': 'project', 'version': 'range'},
            {'key': 'git', 'kind': 'git', 'scope': 'project', 'version': 'floating'},
        ], 'target': ['--extensions', '--approve'], 'advanceGit': True,
           'advancePackages': ['a', 'b'], 'expect': 'all'},
        {'caseId': 'bulk.pinned', 'packages': [
            {'key': 'a', 'kind': 'npm', 'name': a('pinned'), 'scope': 'user', 'version': 'exact'},
            {'key': 'git', 'kind': 'git', 'scope': 'user', 'version': 'tag'},
        ], 'target': ['--extensions'], 'advanceGit': True, 'advanceTag': True,
           'advancePackages': ['a'], 'expect': 'pinned'},
        {'caseId': 'bulk.partial-failure', 'packages': [
            {'key': 'a', 'kind': 'npm', 'name': a('partial'), 'scope': 'user', 'version': 'range'},
            {'key': 'git', 'kind': 'git', 'scope': 'user', 'version': 'floating'},
        ], 'target': ['--extensions'], 'advancePackages': ['a'], 'failGit': True,
           'expect': 'partial-failure'},
        {'caseId': 'bulk.cancelled', 'packages': [
            {'key': 'a', 'kind': 'npm', 'name': a('cancelled'), 'scope': 'user', 'version': 'range'},
        ], 'target': ['--extensions'], 'advancePackages': ['a'], 'expect': 'cancelled'},
        {'caseId': 'bulk.trust', 'packages': [
            {'key': 'a', 'kind': 'npm', 'name': a('trust'), 'scope': 'user', 'version': 'range'},
            {'key': 'b', 'kind': 'npm', 'name': b('trust'), 'scope': 'project', 'version': 'range'},
        ], 'target': ['--extensions'], 'advancePackages': ['a', 'b'], 'trust': False,
           'expect': 'trust'},
    ]


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
    scratch = Path(tempfile.mkdtemp(prefix='managed-bulk-updates-', dir=args.profile))
    fixture_setup = installer.create_product(args.profile / 'fixture-profile', scratch / 'fixture-env', 'pi')
    git_fixture = resource.create_managed_git_repository(scratch, args.pi, fixture_setup['env'])
    git_fixture['daemon'] = cli.register_daemon(git_fixture['daemon'])
    pack_root = scratch / 'npm-packages'
    pack_root.mkdir()
    pack_env = {'PATH': os.environ.get('PATH', ''), 'HOME': str(scratch / 'pack-home'),
                'TMPDIR': str(scratch), 'npm_config_cache': str(scratch / 'pack-cache'),
                'npm_config_offline': 'true'}
    Path(pack_env['HOME']).mkdir()
    package_tarballs = {}
    specs = case_specs(git_fixture)
    for case in specs:
        for package in case['packages']:
            if package['kind'] != 'npm':
                continue
            versions = {}
            for version in ('1.0.0', '1.1.0'):
                versions[version] = package_cli.pack_registry_package(
                    pack_root, package['name'], version, pack_env)
            package['tarballs'] = versions
            package_tarballs[package['name']] = versions

    cases = {}
    try:
        for case in specs:
            set_remote_branch(git_fixture, git_fixture['v1_head'])
            registry_packages = {}
            for package in case['packages']:
                if package['kind'] != 'npm':
                    continue
                package['source'] = f"npm:{package['name']}@{'1.0.0' if package['version'] == 'exact' else '^1.0.0'}"
                registry_packages[package['name']] = {
                    'versions': {'1.0.0': package['tarballs']['1.0.0']}, 'tags': {'latest': '1.0.0'}}
            registry = package_cli.LocalNpmRegistry(registry_packages)
            for package in case['packages']:
                if package['kind'] == 'git':
                    package['source'] = (git_fixture['unpinned_source'] + '@v1'
                                         if package['version'] == 'tag' else git_fixture['unpinned_source'])
                    if package['version'] == 'tag':
                        package['version'] = 'tag'
            products = new_products(args.profile, scratch / 'products', case,
                                    case['packages'], args.pi, args.pisharp, registry, git_fixture)
            expected_before = {product: cli.setting_snapshot(item['agent'], item['cwd'])
                               for product, item in products.items()}

            for package in case['packages']:
                if package['kind'] == 'npm' and package['key'] in case.get('advancePackages', []):
                    registry.set_versions(package['name'], package['tarballs'], {'latest': '1.1.0'})
            if case.get('advanceGit'):
                set_remote_branch(git_fixture, git_fixture['v2_head'])
            if case.get('advanceTag'):
                set_remote_tag(git_fixture, 'v1', git_fixture['v2_head'])
            if case.get('failGit'):
                for product, item in products.items():
                    package_root = git_install_root(item, 'user')
                    subprocess.run(['git', '-C', str(package_root), 'remote', 'set-url', 'origin',
                                    'git://127.0.0.1:1/fixture/unavailable.git'], env=item['env'],
                                   check=True, capture_output=True, text=True)

            expected_kind = case['expect']
            states = {}
            if expected_kind == 'cancelled':
                cancellation_states = {}
                for product, item in products.items():
                    package = case['packages'][0]
                    blocked = package_cli.LocalNpmRegistry({package['name']: {
                        'versions': package['tarballs'], 'tags': {'latest': '1.1.0'}}},
                        blocked_tarball=f"{package['name']}-1.1.0.tgz")
                    try:
                        command, started, reaped = run_cancelled_update(
                            product, args.pi, args.pisharp, item, blocked, case['target'])
                        state = capture_state(item, case['packages'], expected_before[product], command,
                                               lambda value: (value['updateExitCode'] != 0
                                                   and value['operationStarted'] and value['processReaped']
                                                   and value['settingsUnchanged']),
                                               cancelled=True, started=started, process_reaped=reaped)
                        cancellation_states[product] = state
                    finally:
                        blocked.close()
                oracle, actual = cancellation_states['pi'], cancellation_states['pisharp']
                safety_difference = (oracle['updateExitCode'] != 0 and actual['updateExitCode'] != 0
                    and oracle['operationStarted'] and actual['operationStarted']
                    and oracle['processReaped'] and actual['processReaped']
                    and oracle['settingsUnchanged'] and actual['settingsUnchanged']
                    and actual['revisions']['a'] == '1.0.0')
                equal = oracle == actual
                cases[case['caseId']] = {'oracle': oracle, 'actual': actual, 'equal': equal,
                                         'safetyDifferenceAccepted': safety_difference}
                registry.close()
                continue

            for product, item in products.items():
                if expected_kind == 'trust' and case.get('trust') is False:
                    (item['agent'] / 'trust.json').write_text(
                        json.dumps({str(item['cwd'].resolve()): False}) + '\n', encoding='utf-8')
                if case.get('failGit'):
                    pass
                command = run_update(product, args.pi, args.pisharp, item, case['target'])
                if expected_kind == 'partial-failure':
                    def expected(state):
                        return (state['updateExitCode'] != 0 and state['revisions']['a'] == '1.1.0'
                                and state['revisions']['git'] == 'v1' and state['settingsUnchanged'])
                elif expected_kind == 'selective':
                    def expected(state):
                        return (state['updateExitCode'] == 0 and state['revisions'] ==
                                {'a': '1.1.0', 'b': '1.0.0', 'git': 'v1'} and state['settingsUnchanged'])
                elif expected_kind == 'pinned':
                    def expected(state):
                        return (state['updateExitCode'] == 0 and state['revisions'] ==
                                {'a': '1.0.0', 'git': 'v2'} and state['settingsUnchanged'])
                elif expected_kind == 'trust':
                    def expected(state):
                        return (state['updateExitCode'] == 0 and state['revisions'] ==
                                {'a': '1.1.0', 'b': '1.0.0'} and state['projectTrusted'] is False
                                and state['settingsUnchanged'])
                else:
                    def expected(state):
                        return (state['updateExitCode'] == 0 and state['settingsUnchanged']
                                and all(value == ('1.1.0' if key != 'git' else 'v2')
                                        for key, value in state['revisions'].items()))
                states[product] = capture_state(item, case['packages'], expected_before[product], command,
                                                expected, trusted=case.get('trust', True))
            oracle, actual = states['pi'], states['pisharp']
            equal = oracle == actual
            accepted = (expected_kind == 'partial-failure' and not equal
                        and oracle['contractSatisfied'] and actual['contractSatisfied'])
            cases[case['caseId']] = {'oracle': oracle, 'actual': actual, 'equal': equal,
                                     'safetyDifferenceAccepted': accepted}
            registry.close()
    finally:
        cli.stop_daemon(git_fixture['daemon'])

    for case_id, result in cases.items():
        accepted = result.get('safetyDifferenceAccepted') is True
        result['contractSatisfied'] = (result['oracle'].get('contractSatisfied') is True
                                       and result['actual'].get('contractSatisfied') is True
                                       and (result['equal'] or accepted))
    match = all(result['contractSatisfied'] for result in cases.values())
    report = {
        'piSha': pi_sha,
        'piSharpBaseSha': subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, check=True,
                                         capture_output=True, text=True).stdout.strip(),
        'fixtureVersion': 'managed-package-bulk-update-v1',
        'scenario': 'paired-mixed-source-bulk-update-processes',
        'match': match,
        'cases': cases,
        'limitations': ['The package matrix uses local npm registries and a loopback Git daemon.'],
    }
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    print(json.dumps({'cases': len(cases), 'match': match, 'output': str(args.output)}, sort_keys=True))
    return 0 if match else 1


if __name__ == '__main__':
    raise SystemExit(main())
