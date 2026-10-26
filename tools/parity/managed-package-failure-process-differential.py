#!/usr/bin/env python3
"""Compare install failure cleanup for pinned Pi and PiSharp using disposable local fixtures."""

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

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/parity'))
from pi_reference import verify_pi_checkout


def load_module(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f'Unable to load fixture helper {path}')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


package_cli = load_module(Path(__file__).with_name('package-cli-process-differential.py'), 'failure_package_cli')
npm_update = load_module(Path(__file__).with_name('managed-npm-update-process-differential.py'), 'failure_npm_update')

REQUIRED_CASE_IDS = (
    'failure.registry', 'failure.git-remote', 'failure.invalid-archive',
    'failure.lifecycle-script', 'failure.dependency-install',
    'failure.settings-publication', 'failure.permission', 'failure.cancelled-install',
    'failure.cancelled-update', 'failure.interrupted-staging', 'failure.restart-recovery',
)


def product(profile: Path, case: str, name: str) -> dict:
    item = npm_update.make_product(profile / case, name)
    item['env'].update(npm_config_audit='false', npm_config_fund='false')
    return item


def pack_package(root: Path, name: str, version: str, manifest: dict, files: dict[str, str], env: dict) -> Path:
    directory = root / f'{name}-{version}'
    directory.mkdir(parents=True, exist_ok=True)
    (directory / 'package.json').write_text(json.dumps({'name': name, 'version': version, **manifest}), encoding='utf-8')
    for relative, content in files.items():
        path = directory / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding='utf-8')
    result = subprocess.run(['npm', 'pack', '--ignore-scripts', '--pack-destination', str(root), '--json'],
                            cwd=directory, env=env, check=True, capture_output=True, text=True, timeout=60)
    return root / json.loads(result.stdout)[0]['filename']


def installed_root(item: dict, name: str) -> Path:
    return item['agent'] / 'npm/node_modules' / name


def state(item: dict, source: str, command: dict, before: dict, name: str) -> dict:
    settings = package_cli.read_settings(item['agent'] / 'settings.json')
    configured = source in package_cli.package_sources(settings)
    if source.startswith('git:'):
        git_root = item['agent'] / 'git'
        manifests = list(git_root.rglob('package.json')) if git_root.is_dir() else []
        package_root_present = any((manifest.parent / '.git').exists() for manifest in manifests)
        version = None
    else:
        package_root = installed_root(item, name)
        manifest = package_root / 'package.json'
        package_root_present = package_root.is_dir()
        version = json.loads(manifest.read_text(encoding='utf-8')).get('version') if manifest.is_file() else None
    after = package_cli.setting_snapshot(item['agent'], item['cwd'])
    return {
        'commandFailed': command['exitCode'] != 0,
        'sourceConfigured': configured,
        'settingsUnchanged': before == after,
        'packageRootPresent': package_root_present,
        'installedVersion': version,
        'reaped': True,
    }


def run_cli_case(profile: Path, case_id: str, pi: Path, pisharp: Path, source: str,
                 package_name: str, *, registry: str | None = None, read_only_install_root: bool = False) -> dict:
    products = {}
    for name in ('pi', 'pisharp'):
        item = product(profile, case_id, name)
        if registry is not None:
            item['env'].update(npm_config_registry=registry, npm_config_offline='false',
                               npm_config_fetch_retries='0', npm_config_fetch_timeout='3000')
        install_root = item['agent'] / 'npm'
        if read_only_install_root:
            install_root.mkdir(parents=True, exist_ok=True)
            install_root.chmod(0o555)
        before = package_cli.setting_snapshot(item['agent'], item['cwd'])
        result = package_cli.run_cli(name, pi, pisharp, item['cwd'], item['env'], ['install', source])
        observed = state(item, source, result, before, package_name)
        products[name] = {'state': observed, 'command': package_cli.normalized_command(result, item['root'], item['agent'])}
        if read_only_install_root:
            install_root.chmod(0o755)
    return products


def run_cancelled_install(profile: Path, case_id: str, pi: Path, pisharp: Path,
                          source: str, package_name: str, registry) -> dict:
    products = {}
    for name in ('pi', 'pisharp'):
        item = product(profile, case_id, name)
        item['env'].update(npm_config_registry=registry.url, npm_config_offline='false',
                           npm_config_fetch_retries='0', npm_config_fetch_timeout='3000')
        before = package_cli.setting_snapshot(item['agent'], item['cwd'])
        argv = package_cli.command_for(name, pi, pisharp, ['install', source])
        with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
            process = subprocess.Popen(argv, cwd=item['cwd'], env=item['env'], stdout=stdout,
                                       stderr=stderr, start_new_session=True)
            deadline = package_cli.time.monotonic() + 60
            while package_cli.time.monotonic() < deadline and process.poll() is None \
                    and not registry.tarball_requested.is_set():
                package_cli.time.sleep(0.05)
            started = registry.tarball_requested.is_set()
            if started and process.poll() is None:
                os.killpg(process.pid, signal.SIGINT)
            try:
                exit_code = process.wait(timeout=30)
                reaped = True
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                exit_code = process.wait(timeout=15)
                reaped = False
            finally:
                registry.release_tarball.set()
            stdout.seek(0)
            stderr.seek(0)
            command = {'exitCode': exit_code, 'stdout': stdout.read().decode(errors='replace'),
                       'stderr': stderr.read().decode(errors='replace')}
        observed = state(item, source, command, before, package_name)
        observed.update({'downloadStarted': started, 'reaped': reaped})
        products[name] = {'state': observed,
                          'command': package_cli.normalized_command(command, item['root'], item['agent'])}
    return products


def contract_pass(case_id: str, oracle: dict, actual: dict) -> tuple[bool, str]:
    def base(state_value: dict) -> bool:
        return (state_value.get('commandFailed') is True and state_value.get('sourceConfigured') is False
                and state_value.get('settingsUnchanged') is True and state_value.get('reaped') is True)

    if case_id in ('failure.registry', 'failure.git-remote', 'failure.invalid-archive', 'failure.permission'):
        expected = base(oracle) and oracle.get('packageRootPresent') is False
        safe = base(actual) and actual.get('packageRootPresent') is False
        return expected and safe and oracle == actual, 'strict' if oracle == actual else 'mismatch'
    if case_id in ('failure.lifecycle-script', 'failure.dependency-install'):
        expected = base(oracle)
        safe = base(actual) and actual.get('packageRootPresent') is False
        if not expected or not safe:
            return False, 'mismatch'
        return True, 'strict' if oracle == actual else 'intentional-safety-difference'
    if case_id == 'failure.cancelled-install':
        expected = (oracle.get('downloadStarted') is True and oracle.get('commandFailed') is True
                    and oracle.get('sourceConfigured') is False and oracle.get('settingsUnchanged') is True
                    and oracle.get('reaped') is True)
        safe = (actual.get('downloadStarted') is True and actual.get('commandFailed') is True
                and actual.get('sourceConfigured') is False and actual.get('settingsUnchanged') is True
                and actual.get('reaped') is True and actual.get('packageRootPresent') is False)
        return expected and safe, 'strict' if oracle == actual else 'intentional-safety-difference'
    return False, 'mismatch'


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.pi, args.pisharp, args.profile, args.output = (args.pi.resolve(), args.pisharp.resolve(),
                                                        args.profile.resolve(), args.output.resolve())
    pi_sha = verify_pi_checkout(args.pi)
    args.profile.mkdir(parents=True, exist_ok=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    scratch = Path(tempfile.mkdtemp(prefix='package-failure-fixtures-', dir=args.profile))
    pack_root = scratch / 'packed'
    pack_root.mkdir()
    pack_home = scratch / 'pack-home'
    pack_home.mkdir()
    pack_env = {'PATH': os.environ.get('PATH', ''), 'HOME': str(pack_home), 'TMPDIR': str(scratch),
                'npm_config_cache': str(scratch / 'pack-cache'), 'npm_config_offline': 'true'}
    package_name = 'failure-fixture'
    lifecycle_name = 'failure-lifecycle'
    dependency_name = 'failure-dependency-main'
    dependency_package_name = 'failure-dependency'
    lifecycle_tarball = pack_package(pack_root, lifecycle_name, '1.0.0',
        {'scripts': {'postinstall': 'node fail.cjs'}}, {'fail.cjs': 'process.exit(37);\n'}, pack_env)
    dependency_tarball = pack_package(pack_root, dependency_package_name, '1.0.0',
        {'scripts': {'postinstall': 'node fail.cjs'}}, {'fail.cjs': 'process.exit(38);\n'}, pack_env)
    dependency_main_tarball = pack_package(pack_root, dependency_name, '1.0.0',
        {'dependencies': {dependency_package_name: 'file:' + str(dependency_tarball)}}, {}, pack_env)
    cancelled_tarball = package_cli.pack_registry_package(pack_root, 'failure-cancelled', '1.0.0', pack_env)
    permission_tarball = package_cli.pack_registry_package(pack_root, 'failure-permission', '1.0.0', pack_env)
    invalid_tarball = pack_root / 'failure-invalid-archive-1.0.0.tgz'
    invalid_tarball.write_bytes(b'not a gzip or tar archive\n')

    failure_cases = {}
    try:
        for case_id, source, name, registry, read_only in [
            ('failure.registry', f'npm:{package_name}@1.0.0', package_name, 'http://127.0.0.1:1/', False),
            ('failure.git-remote', 'git:git://127.0.0.1:1/fixture/unreachable.git@main', 'unreachable', None, False),
            ('failure.invalid-archive', f'npm:failure-invalid-archive@file:{invalid_tarball}',
             'failure-invalid-archive', None, False),
            ('failure.lifecycle-script', f'npm:{lifecycle_name}@file:{lifecycle_tarball}', lifecycle_name, None, False),
            ('failure.dependency-install', f'npm:{dependency_name}@file:{dependency_main_tarball}',
             dependency_name, None, False),
            ('failure.permission', f'npm:failure-permission@file:{permission_tarball}',
             'failure-permission', None, True),
        ]:
            outcomes = run_cli_case(args.profile, case_id, args.pi, args.pisharp, source, name,
                                    registry=registry, read_only_install_root=read_only)
            oracle, actual = outcomes['pi']['state'], outcomes['pisharp']['state']
            passed, classification = contract_pass(case_id, oracle, actual)
            failure_cases[case_id] = {'pi': oracle, 'pisharp': actual, 'commands': outcomes,
                                      'match': oracle == actual, 'classification': classification,
                                      'contractSatisfied': passed}

        registry = package_cli.LocalNpmRegistry({'failure-cancelled': {
            'versions': {'1.0.0': cancelled_tarball}, 'tags': {'latest': '1.0.0'},
        }}, blocked_tarball='failure-cancelled-1.0.0.tgz')
        try:
            source = 'npm:failure-cancelled@1.0.0'
            outcomes = run_cancelled_install(args.profile, 'failure.cancelled-install', args.pi,
                                             args.pisharp, source, 'failure-cancelled', registry)
            oracle, actual = outcomes['pi']['state'], outcomes['pisharp']['state']
            passed, classification = contract_pass('failure.cancelled-install', oracle, actual)
            failure_cases['failure.cancelled-install'] = {
                'pi': oracle, 'pisharp': actual, 'commands': outcomes,
                'match': oracle == actual, 'classification': classification, 'contractSatisfied': passed,
            }
        finally:
            registry.close()
    finally:
        shutil = __import__('shutil')
        shutil.rmtree(scratch, ignore_errors=True)

    source_sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    evidence = {'fixtureVersion': 'managed-package-install-failures-v1', 'piSha': pi_sha,
                'pisharpSourceSha': source_sha, 'cases': failure_cases,
                'match': all(case['contractSatisfied'] for case in failure_cases.values())}
    args.output.write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'piSha': pi_sha, 'cases': len(failure_cases), 'match': evidence['match'],
                      'output': str(args.output)}))
    return 0 if evidence['match'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
