#!/usr/bin/env python3
"""Compare pinned Pi and PiSharp configured package-manager process behavior."""

import argparse
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
sys.path.insert(0, str(ROOT))
import importlib.util

package_cli_path = Path(__file__).with_name('package-cli-process-differential.py')
package_cli_spec = importlib.util.spec_from_file_location('package_cli_process', package_cli_path)
package_cli = importlib.util.module_from_spec(package_cli_spec)
package_cli_spec.loader.exec_module(package_cli)


def make_manager_shims(root):
    bin_root = root / 'manager-bin'
    bin_root.mkdir(parents=True)
    custom_root = root / 'custom manager tools with spaces'
    custom_root.mkdir(parents=True)
    script = r'''#!/bin/sh
printf '%s\0' "$@" >> "$PISHARP_MANAGER_ARGV_LOG"
if [ "${PISHARP_MANAGER_MODE:-success}" = "cancel" ]; then
    mkdir -p "$PISHARP_MANAGER_CANCEL_PACKAGE"
    printf '{"name":"pm-cancel-fixture","version":"1.0.0"}\n' > "$PISHARP_MANAGER_CANCEL_PACKAGE/package.json"
    : > "$PISHARP_MANAGER_CANCEL_MARKER"
    while :; do sleep 5; done
fi
if [ "${PISHARP_MANAGER_MODE:-success}" = "failure" ]; then exit 37; fi
if [ "${PISHARP_MANAGER_MODE:-success}" = "real" ]; then
    exec "$PISHARP_MANAGER_REAL_NPM" "$@"
fi
exit 0
'''
    for manager in ('npm', 'pnpm', 'yarn', 'bun'):
        path = bin_root / manager
        path.write_text(script, encoding='utf-8')
        path.chmod(0o755)
    custom = custom_root / 'npm manager wrapper'
    custom.write_text(script, encoding='utf-8')
    custom.chmod(0o755)
    return {'bin': bin_root, 'custom': custom}


def setup_product(profile, product, bin_root, case_id, npm_command, source, mode='success'):
    local = profile / case_id / product
    cwd = local / 'work'
    agent = local / 'agent'
    markers = local / 'markers'
    trace = local / 'extension.log'
    cwd.mkdir(parents=True)
    (cwd / '.pi').mkdir()
    agent.mkdir()
    (cwd / 'settings.json').parent.mkdir(exist_ok=True)
    settings_path = agent / 'settings.json'
    settings = {'packages': []}
    if npm_command is not None:
        settings['npmCommand'] = npm_command
    settings_path.write_text(json.dumps(settings) + '\n', encoding='utf-8')
    (cwd / '.pi/settings.json').write_text('{}\n', encoding='utf-8')
    env = package_cli.base_environment(profile / case_id, product, agent, cwd, markers, trace)
    env['PATH'] = str(bin_root) + os.pathsep + env.get('PATH', '')
    env['PISHARP_MANAGER_REAL_NPM'] = shutil.which('npm') or 'npm'
    env['PISHARP_MANAGER_ARGV_LOG'] = str(local / 'manager-argv.bin')
    env['PISHARP_MANAGER_MODE'] = mode
    return {'cwd': cwd, 'agent': agent, 'settings': settings_path, 'env': env,
            'log': Path(env['PISHARP_MANAGER_ARGV_LOG']), 'source': source,
            'installRoot': agent / 'npm', 'productRoot': local,
            'initialSettings': settings, 'configuredCommand': npm_command}


def configure_case(case_id, custom_shim):
    if case_id == 'pm.default':
        return None, 'npm:pm-default-fixture@file:{tarball}', 'available-real-managers', 'real'
    if case_id == 'pm.npm':
        return ['npm'], 'npm:pm-npm-fixture@file:{tarball}', 'available-real-managers', 'real'
    if case_id == 'pm.pnpm':
        return ['pnpm'], 'npm:pm-pnpm-fixture@1.0.0', 'command-shims', 'success'
    if case_id == 'pm.yarn':
        return ['yarn'], 'npm:pm-yarn-fixture@1.0.0', 'command-shims', 'success'
    if case_id == 'pm.bun':
        return ['bun'], 'npm:pm-bun-fixture@1.0.0', 'command-shims', 'success'
    if case_id == 'pm.custom-arguments':
        return [str(custom_shim), 'exec', 'node@20', '--', 'npm'], \
            'npm:pm-custom-fixture@1.0.0', 'command-shims', 'success'
    if case_id == 'pm.missing-executable':
        return ['/no/such/package-manager-with-spaces/npm-missing'], \
            'npm:pm-missing-fixture@1.0.0', 'command-shims', 'success'
    if case_id == 'pm.failure':
        return ['npm'], 'npm:pm-failure-fixture@1.0.0', 'command-shims', 'failure'
    if case_id == 'pm.cancellation':
        return ['npm'], 'npm:pm-cancel-fixture@1.0.0', 'command-shims', 'cancel'
    raise ValueError(f'Unknown package-manager case: {case_id}')


def read_argv(log, install_root):
    if not log.is_file():
        return []
    values = [value for value in log.read_bytes().decode().split('\0') if value]
    return [value.replace(str(install_root), '<install-root>') for value in values]


def run_case(profile, shims, real_npm, pi, pisharp, case_id, tarballs):
    npm_command, source_template, fixture_family, mode = configure_case(case_id, shims['custom'])
    source = source_template
    if '{tarball}' in source_template:
        package = source_template.split(':')[1].split('@')[0]
        source = source_template.format(tarball=tarballs[package])
    products = {}
    for product in ('pi', 'pisharp'):
        products[product] = setup_product(profile, product, shims['bin'], case_id,
                                          npm_command, source, mode)

    is_cancel = case_id == 'pm.cancellation'
    commands = {}
    for product, item in products.items():
        if is_cancel:
            marker = item['productRoot'] / 'manager-started.marker'
            package_root = item['installRoot'] / 'node_modules' / 'pm-cancel-fixture'
            item['env']['PISHARP_MANAGER_CANCEL_PACKAGE'] = str(package_root)
            item['env']['PISHARP_MANAGER_CANCEL_MARKER'] = str(marker)
            command = package_cli.command_for(product, pi, pisharp, ['install', source])
            with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
                process = subprocess.Popen(command, cwd=item['cwd'], env=item['env'], stdout=stdout, stderr=stderr,
                                           start_new_session=True)
                deadline = time.monotonic() + 45
                while time.monotonic() < deadline and process.poll() is None and not marker.is_file():
                    time.sleep(0.05)
                started = marker.is_file()
                if not started:
                    if process.poll() is None:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait(timeout=10)
                    stdout.seek(0)
                    stderr.seek(0)
                    raise AssertionError({'case': case_id, 'product': product, 'managerStarted': False,
                                          'processExitCode': process.poll(),
                                          'stdout': stdout.read().decode(errors='replace'),
                                          'stderr': stderr.read().decode(errors='replace')})
                os.killpg(process.pid, signal.SIGINT)
                try:
                    process_exit_code = process.wait(timeout=25)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=10)
                    raise AssertionError(f'{product} did not stop after manager cancellation')
                exit_code = 128 + signal.SIGINT if process_exit_code == -signal.SIGINT else process_exit_code
                stdout.seek(0)
                stderr.seek(0)
                commands[product] = {'exitCode': exit_code,
                                     'processExitCode': process_exit_code,
                                     'stdout': stdout.read().decode(errors='replace'),
                                     'stderr': stderr.read().decode(errors='replace'),
                                     'managerStarted': started}
        else:
            commands[product] = package_cli.run_cli(
                product, pi, pisharp, item['cwd'], item['env'], ['install', source])

    state = {}
    for product, item in products.items():
        after = json.loads(item['settings'].read_text(encoding='utf-8'))
        package_root = item['installRoot'] / 'node_modules' / source.split(':', 1)[1].split('@', 1)[0]
        if case_id in ('pm.default', 'pm.npm'):
            package_root = item['installRoot'] / 'node_modules' / (
                'pm-default-fixture' if case_id == 'pm.default' else 'pm-npm-fixture')
        state[product] = {
            'exitCode': commands[product]['exitCode'],
            'argv': read_argv(item['log'], item['installRoot']),
            'sourceConfigured': source in package_cli.package_sources(after),
            'packageManagerConfig': after.get('npmCommand'),
            'npmCommandPreserved': after.get('npmCommand') == item['configuredCommand'],
            'settingsUnchanged': after == item['initialSettings'],
            'packageDirectoryExists': package_root.is_dir(),
            'managerStarted': commands[product].get('managerStarted', True),
            'cancelled': commands[product]['exitCode'] != 0 if is_cancel else None,
        }

    if case_id in ('pm.default', 'pm.npm', 'pm.pnpm', 'pm.yarn', 'pm.bun', 'pm.custom-arguments'):
        manager = {'pm.default': 'npm', 'pm.npm': 'npm', 'pm.pnpm': 'pnpm',
                   'pm.yarn': 'yarn', 'pm.bun': 'bun', 'pm.custom-arguments': 'npm'}[case_id]
        spec = source.split(':', 1)[1]
        if manager == 'bun':
            expected_argv = ['install', spec, '--cwd', '<install-root>', '--omit=peer']
        elif manager == 'pnpm':
            expected_argv = ['install', spec, '--prefix', '<install-root>',
                             '--config.auto-install-peers=false', '--config.strict-peer-dependencies=false',
                             '--config.strict-dep-builds=false']
        else:
            expected_argv = ['install', spec, '--prefix', '<install-root>', '--legacy-peer-deps']
        if case_id == 'pm.custom-arguments':
            expected_argv = ['exec', 'node@20', '--', 'npm', *expected_argv]
        for product in state:
            state[product]['expectedArgv'] = expected_argv
        passed = all(
            item['exitCode'] == 0 and item['argv'] == expected_argv and item['sourceConfigured']
            and item['npmCommandPreserved'] and item['managerStarted'] for item in state.values()) \
            and state['pi'] == state['pisharp']
        classification = 'strict'
    elif case_id in ('pm.missing-executable', 'pm.failure'):
        expected_failure = all(item['exitCode'] != 0 and not item['sourceConfigured']
                               and item['settingsUnchanged'] and item['npmCommandPreserved']
                               and not item['packageDirectoryExists'] for item in state.values())
        passed = expected_failure and state['pi'] == state['pisharp']
        classification = 'strict'
    else:
        pi_state, actual_state = state['pi'], state['pisharp']
        same = pi_state == actual_state
        pisharp_safe = (actual_state['cancelled'] is True and not actual_state['sourceConfigured']
                        and actual_state['settingsUnchanged'] and actual_state['npmCommandPreserved']
                        and not actual_state['packageDirectoryExists'])
        pi_state_valid = (pi_state['cancelled'] is True and not pi_state['sourceConfigured']
                          and pi_state['settingsUnchanged'] and pi_state['npmCommandPreserved'])
        safety_only = (pi_state['packageDirectoryExists'] and not actual_state['packageDirectoryExists']
                       and {key: value for key, value in pi_state.items() if key != 'packageDirectoryExists'}
                       == {key: value for key, value in actual_state.items() if key != 'packageDirectoryExists'})
        passed = pisharp_safe and pi_state_valid and (same or safety_only)
        classification = 'strict' if same else 'intentional-safety-difference'

    return {
        'expected': {
            'manager': {'pm.default': 'npm', 'pm.npm': 'npm', 'pm.pnpm': 'pnpm', 'pm.yarn': 'yarn',
                        'pm.bun': 'bun', 'pm.custom-arguments': 'npm', 'pm.failure': 'npm',
                        'pm.cancellation': 'npm'}.get(case_id),
            'installSpec': source.split(':', 1)[1],
        },
        'oracle': state['pi'], 'actual': state['pisharp'], 'classification': classification,
        'passed': passed, 'diagnostics': commands,
        'fixtureFamily': fixture_family,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    # The aggregate verifier passes a repository-relative profile path. Child processes
    # change their working directory, so every path exported through their environment
    # must be absolute before the fixtures are created.
    args.profile = args.profile.resolve()
    args.output = args.output.resolve()
    args.profile.mkdir(parents=True, exist_ok=True)
    (args.profile / 'tmp').mkdir(exist_ok=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    real_npm = shutil.which('npm')
    if not real_npm:
        raise RuntimeError('npm is unavailable; real-manager verification cannot run')
    pi_sha = package_cli.verify_pi_checkout(args.pi.resolve())

    results = {}
    with tempfile.TemporaryDirectory(prefix='package-managers-', dir=str(args.profile / 'tmp')) as scratch:
        root = Path(scratch)
        shims = make_manager_shims(root)
        package_root = root / 'fixture-tarballs'
        package_root.mkdir()
        env = dict(os.environ)
        pack_cache = root / 'npm-pack-cache'
        pack_cache.mkdir()
        env.update(npm_config_cache=str(pack_cache), npm_config_offline='true',
                   npm_config_audit='false', npm_config_fund='false', npm_config_logs_max='0')
        tarballs = {}
        for package_name in ('pm-default-fixture', 'pm-npm-fixture'):
            tarballs[package_name] = package_cli.pack_registry_package(package_root, package_name, '1.0.0', env)
        case_ids = ['pm.default', 'pm.npm', 'pm.pnpm', 'pm.yarn', 'pm.bun', 'pm.custom-arguments',
                    'pm.missing-executable', 'pm.failure', 'pm.cancellation']
        for case_id in case_ids:
            results[case_id] = run_case(args.profile, shims, real_npm, args.pi.resolve(), args.pisharp.resolve(),
                                        case_id, tarballs)
    evidence = {
        'piSha': pi_sha,
        'pisharpSha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        'scenario': 'configured-package-manager-processes',
        'fixtureFamily': ['command-shims', 'available-real-managers'],
        'availableRealManagers': {'npm': subprocess.check_output([real_npm, '--version'], text=True).strip()},
        'cases': results,
        'match': all(item['passed'] for item in results.values()),
        'limitations': ['pnpm, Yarn and Bun are validated through exact command shims because they are not installed in this environment.'],
    }
    args.output.write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')
    print(f"Recorded {len(results)} configured package-manager process cases: {args.output}")
    return 0 if evidence['match'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
