#!/usr/bin/env python3
"""Compare pinned Pi and PiSharp npm update behavior against mutable loopback registries."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/parity'))
from pi_reference import verify_pi_checkout

package_cli_path = Path(__file__).with_name('package-cli-process-differential.py')
package_cli_spec = importlib.util.spec_from_file_location('package_cli_process', package_cli_path)
package_cli = importlib.util.module_from_spec(package_cli_spec)
package_cli_spec.loader.exec_module(package_cli)


def package_version(agent, package_name):
    manifest = agent / 'npm/node_modules' / package_name / 'package.json'
    if not manifest.is_file():
        return None
    return json.loads(manifest.read_text()).get('version')


def make_product(profile, product):
    product_root = profile / product
    cwd = product_root / 'work'
    agent = product_root / 'agent'
    markers = product_root / 'markers'
    trace = product_root / 'extension.log'
    cwd.mkdir(parents=True)
    (cwd / '.pi').mkdir()
    agent.mkdir()
    (agent / 'settings.json').write_text('{"packages":[]}\n')
    (cwd / '.pi/settings.json').write_text('{}\n')
    env = package_cli.base_environment(profile, product, agent, cwd, markers, trace)
    env.update(
        npm_config_offline='false', npm_config_audit='false', npm_config_fund='false',
        npm_config_fetch_retries='0', npm_config_fetch_timeout='5000',
        npm_config_prefer_online='true', npm_config_cache_min='0',
    )
    return {'product': product, 'root': product_root, 'cwd': cwd, 'agent': agent, 'env': env}


def run_command(product, pi, pisharp, item, args):
    result = package_cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], args)
    return {
        'exitCode': result['exitCode'],
        'stdout': package_cli.strip_ansi(result['stdout']).strip(),
        'stderr': package_cli.strip_ansi(result['stderr']).strip(),
    }


def run_interrupted_update(profile, product, pi, pisharp, source, initial_registry, blocking_registry,
                           signal_number):
    scenario = 'cancelled' if signal_number == signal.SIGINT else 'interrupted'
    item = make_product(profile / f'npm.update-{scenario}', product)
    item['env']['npm_config_registry'] = initial_registry.url
    initial_install = run_command(product, pi, pisharp, item, ['install', source])
    if initial_install['exitCode'] != 0:
        raise AssertionError({'product': product, 'initialInstall': initial_install})
    initial_version = package_version(item['agent'], 'update-interruption-fixture')
    settings_before = json.loads((item['agent'] / 'settings.json').read_text()).get('packages', [])
    item['env']['npm_config_registry'] = blocking_registry.url
    with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
        command = package_cli.command_for(product, pi, pisharp, ['update', '--extensions'])
        process = subprocess.Popen(command, cwd=item['cwd'], env=item['env'], stdout=stdout, stderr=stderr,
                                   start_new_session=True)
        deadline = time.monotonic() + 90
        try:
            while time.monotonic() < deadline and process.poll() is None \
                    and not blocking_registry.tarball_requested.is_set():
                time.sleep(0.05)
            if not blocking_registry.tarball_requested.is_set():
                stdout.seek(0)
                stderr.seek(0)
                raise AssertionError({
                    'product': product, 'processExitCode': process.poll(),
                    'stdout': stdout.read().decode(errors='replace'),
                    'stderr': stderr.read().decode(errors='replace'),
                })
            os.killpg(process.pid, signal_number)
            try:
                exit_code = process.wait(timeout=45)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                exit_code = process.wait(timeout=15)
        except Exception:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
            raise
        finally:
            blocking_registry.release_tarball.set()
        stdout.seek(0)
        stderr.seek(0)
        diagnostics = {'exitCode': exit_code,
                       'stdout': stdout.read().decode(errors='replace'),
                       'stderr': stderr.read().decode(errors='replace')}
    settings_after = json.loads((item['agent'] / 'settings.json').read_text()).get('packages', [])
    final_version = package_version(item['agent'], 'update-interruption-fixture')
    restart = run_command(product, pi, pisharp, item, ['list'])
    version_after_restart = package_version(item['agent'], 'update-interruption-fixture')
    state = {
        'cancelled': exit_code != 0,
        'initialVersion': initial_version,
        'finalVersion': final_version,
        'restartExitCode': restart['exitCode'],
        'versionAfterRestart': version_after_restart,
        'sourceConfigured': source in settings_after,
        'settingsUnchanged': settings_before == settings_after,
        'downloadStarted': blocking_registry.tarball_requested.is_set(),
    }
    return {'state': state, 'diagnostics': diagnostics, 'restart': restart}


def run_case(profile, registry_root, registries, pi, pisharp, case):
    name = case['name']
    package_data = registries[name]
    primary_registry = package_data['registry']
    products = {product: make_product(profile / case['caseId'], product)
                for product in ('pi', 'pisharp')}
    for item in products.values():
        item['env']['npm_config_registry'] = primary_registry.url

    source = case['source']
    commands = {product: {'install': run_command(product, pi, pisharp, item, ['install', source])}
                for product, item in products.items()}
    initial = {product: package_version(item['agent'], name) for product, item in products.items()}
    if any(commands[product]['install']['exitCode'] != 0 for product in products):
        return {'pi': {'initialVersion': initial['pi'], 'commands': commands['pi']},
                'pisharp': {'initialVersion': initial['pisharp'], 'commands': commands['pisharp']},
                'match': False}

    for product, item in products.items():
        item['initialSettings'] = json.loads((item['agent'] / 'settings.json').read_text()).get('packages', [])

    if case.get('registryChange'):
        for product, item in products.items():
            item['env']['npm_config_registry'] = case['secondaryRegistry'].url
    elif case.get('registryFailure'):
        for item in products.values():
            item['env']['npm_config_registry'] = 'http://127.0.0.1:1/'
    elif 'finalVersions' in case:
        visible_versions = {version: package_data['versions'][version] for version in case['finalVersions']}
        primary_registry.set_versions(name, visible_versions, case.get('finalTags', {}))

    states = {}
    for product, item in products.items():
        update = run_command(product, pi, pisharp, item, ['update', '--extensions'])
        repeat = None
        if case.get('repeat'):
            repeat = run_command(product, pi, pisharp, item, ['update', '--extensions'])
        settings = json.loads((item['agent'] / 'settings.json').read_text()).get('packages', [])
        state = {
            'initialVersion': initial[product],
            'finalVersion': package_version(item['agent'], name),
            'sourceConfigured': source in settings,
            'settingsUnchanged': settings == item['initialSettings'],
            'updateExitCode': update['exitCode'],
            'registryChanged': case.get('registryChange') is True,
            'registryUpdated': 'finalVersions' in case,
            'registryFailed': case.get('registryFailure') is True,
        }
        if repeat is not None:
            state['repeatExitCode'] = repeat['exitCode']
            state['versionAfterRepeat'] = package_version(item['agent'], name)
        commands[product]['update'] = update
        if repeat is not None:
            commands[product]['repeat'] = repeat
        states[product] = {'state': state, 'commands': commands[product]}

    expected = case['expected']

    def satisfies(state):
        if state['sourceConfigured'] is not True:
            return False
        if expected['updateExitCode'] == 'failure':
            if state['updateExitCode'] == 0 or state['finalVersion'] != state['initialVersion']:
                return False
        elif state['updateExitCode'] != 0 or state['finalVersion'] != expected['version']:
            return False
        if expected.get('settingsUnchanged') and not state['settingsUnchanged']:
            return False
        if case.get('repeat') and (state.get('repeatExitCode') != 0 or
                                   state.get('versionAfterRepeat') != expected['version']):
            return False
        return True

    states['match'] = (states['pi']['state'] == states['pisharp']['state']
                       and satisfies(states['pi']['state']) and satisfies(states['pisharp']['state']))
    return states


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.pi = args.pi.resolve()
    args.pisharp = args.pisharp.resolve()
    pi_sha = verify_pi_checkout(args.pi)
    profile = args.profile.resolve()
    profile.mkdir(parents=True, exist_ok=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)

    case_specs = [
        {'caseId': 'npm.update-pinned', 'name': 'update-pinned-fixture',
         'source': 'npm:update-pinned-fixture@1.0.0', 'versions': ['1.0.0', '1.1.0'],
         'initialVersions': ['1.0.0'], 'initialTags': {'latest': '1.0.0'},
         'finalVersions': ['1.0.0', '1.1.0'], 'finalTags': {'latest': '1.1.0'},
         'expected': {'version': '1.0.0', 'updateExitCode': 0, 'settingsUnchanged': True}},
        {'caseId': 'npm.update-range', 'name': 'update-range-fixture',
         'source': 'npm:update-range-fixture@^1.0.0', 'versions': ['1.0.0', '1.1.0'],
         'initialVersions': ['1.0.0'], 'initialTags': {'latest': '1.0.0'},
         'finalVersions': ['1.0.0', '1.1.0'], 'finalTags': {'latest': '1.1.0'},
         'expected': {'version': '1.1.0', 'updateExitCode': 0}},
        {'caseId': 'npm.update-latest', 'name': 'update-latest-fixture',
         'source': 'npm:update-latest-fixture@latest', 'versions': ['1.0.0', '1.1.0'],
         'initialVersions': ['1.0.0'], 'initialTags': {'latest': '1.0.0'},
         'finalVersions': ['1.0.0', '1.1.0'], 'finalTags': {'latest': '1.1.0'},
         'expected': {'version': '1.1.0', 'updateExitCode': 0}},
        {'caseId': 'npm.update-no-change', 'name': 'update-no-change-fixture',
         'source': 'npm:update-no-change-fixture@^1.0.0', 'versions': ['1.0.0', '1.1.0'],
         'initialVersions': ['1.0.0'], 'initialTags': {'latest': '1.0.0'},
         'expected': {'version': '1.0.0', 'updateExitCode': 0, 'settingsUnchanged': True}},
        {'caseId': 'npm.update-failure', 'name': 'update-failure-fixture',
         'source': 'npm:update-failure-fixture@^1.0.0', 'versions': ['1.0.0'],
         'initialVersions': ['1.0.0'], 'initialTags': {'latest': '1.0.0'},
         'registryFailure': True,
         'expected': {'version': '1.0.0', 'updateExitCode': 'failure', 'settingsUnchanged': True}},
        {'caseId': 'npm.update-repeat', 'name': 'update-repeat-fixture',
         'source': 'npm:update-repeat-fixture', 'versions': ['1.0.0', '1.1.0'],
         'initialVersions': ['1.0.0'], 'initialTags': {'latest': '1.0.0'},
         'finalVersions': ['1.0.0', '1.1.0'], 'finalTags': {'latest': '1.1.0'},
         'repeat': True, 'expected': {'version': '1.1.0', 'updateExitCode': 0}},
        {'caseId': 'npm.update-unpinned', 'name': 'update-unpinned-fixture',
         'source': 'npm:update-unpinned-fixture', 'versions': ['1.0.0', '1.1.0'],
         'initialVersions': ['1.0.0'], 'initialTags': {'latest': '1.0.0'},
         'finalVersions': ['1.0.0', '1.1.0'], 'finalTags': {'latest': '1.1.0'},
         'expected': {'version': '1.1.0', 'updateExitCode': 0}},
        {'caseId': 'npm.update-prerelease', 'name': 'update-prerelease-fixture',
         'source': 'npm:update-prerelease-fixture@^2.0.0-beta.1',
         'versions': ['2.0.0-beta.1', '2.0.0-beta.2'],
         'initialVersions': ['2.0.0-beta.1'], 'initialTags': {'latest': '2.0.0-beta.1'},
         'finalVersions': ['2.0.0-beta.1', '2.0.0-beta.2'], 'finalTags': {'latest': '2.0.0-beta.2'},
         'expected': {'version': '2.0.0-beta.2', 'updateExitCode': 0}},
    ]

    run_root = Path(tempfile.mkdtemp(prefix='managed-npm-update-', dir=profile))
    registry_root = run_root / 'registry'
    pack_home = run_root / 'pack-home'
    pack_home.mkdir()
    pack_env = {
        'PATH': os.environ.get('PATH', ''), 'HOME': str(pack_home), 'TMPDIR': str(run_root),
        'LANG': os.environ.get('LANG', 'C.UTF-8'), 'LC_ALL': os.environ.get('LC_ALL', 'C.UTF-8'),
        'npm_config_cache': str(run_root / 'npm-cache'), 'npm_config_offline': 'true',
    }
    registry_root.mkdir()
    registries = {}
    for case in case_specs:
        versions = {
            version: package_cli.pack_registry_package(registry_root, case['name'], version, pack_env)
            for version in case['versions']
        }
        registry = package_cli.LocalNpmRegistry({case['name']: {
            'versions': {version: versions[version] for version in case['initialVersions']},
            'tags': case['initialTags'],
        }})
        registries[case['name']] = {'registry': registry, 'versions': versions}
        if case.get('registryChange'):
            secondary = package_cli.LocalNpmRegistry({case['name']: {
                'versions': versions, 'tags': case['finalTags'],
            }})
            case['secondaryRegistry'] = secondary

    # A registry change is a separate local endpoint, with the same package identity and a newer tarball.
    registry_change = {
        'caseId': 'npm.update-registry-change', 'name': 'update-registry-change-fixture',
        'source': 'npm:update-registry-change-fixture@^1.0.0', 'versions': ['1.0.0', '1.1.0'],
        'initialVersions': ['1.0.0'], 'initialTags': {'latest': '1.0.0'}, 'registryChange': True,
        'finalTags': {'latest': '1.1.0'},
        'expected': {'version': '1.1.0', 'updateExitCode': 0},
    }
    change_versions = {
        version: package_cli.pack_registry_package(registry_root, registry_change['name'], version, pack_env)
        for version in registry_change['versions']
    }
    first_registry = package_cli.LocalNpmRegistry({registry_change['name']: {
        'versions': {'1.0.0': change_versions['1.0.0']}, 'tags': {'latest': '1.0.0'},
    }})
    second_registry = package_cli.LocalNpmRegistry({registry_change['name']: {
        'versions': change_versions, 'tags': {'latest': '1.1.0'},
    }})
    registries[registry_change['name']] = {'registry': first_registry, 'versions': change_versions}
    registry_change['secondaryRegistry'] = second_registry
    case_specs.append(registry_change)

    interrupted_name = 'update-interruption-fixture'
    interrupted_v1 = package_cli.pack_registry_package(registry_root, interrupted_name, '1.0.0', pack_env)
    interrupted_v2 = package_cli.pack_registry_package(registry_root, interrupted_name, '1.1.0', pack_env)
    interrupted_registry = package_cli.LocalNpmRegistry({interrupted_name: {
        'versions': {'1.0.0': interrupted_v1}, 'tags': {'latest': '1.0.0'},
    }})
    interrupted_source = f'npm:{interrupted_name}@^1.0.0'

    results = {}
    try:
        for case in case_specs:
            results[case['caseId']] = run_case(run_root / 'profiles', registry_root, registries,
                                               args.pi, args.pisharp, case)
        for case_id, process_signal in [
            ('npm.update-cancelled', signal.SIGINT),
            ('npm.update-interrupted', signal.SIGKILL),
        ]:
            per_product = {}
            for product in ('pi', 'pisharp'):
                blocked_registry = package_cli.LocalNpmRegistry({interrupted_name: {
                    'versions': {'1.0.0': interrupted_v1, '1.1.0': interrupted_v2},
                    'tags': {'latest': '1.1.0'},
                }}, blocked_tarball=f'{interrupted_name}-1.1.0.tgz')
                try:
                    per_product[product] = run_interrupted_update(
                        run_root / 'profiles', product, args.pi, args.pisharp, interrupted_source,
                        interrupted_registry, blocked_registry, process_signal)
                finally:
                    blocked_registry.close()
            left = per_product['pi']['state']
            right = per_product['pisharp']['state']
            oracle_reproduced = (left['cancelled'] and left['initialVersion'] == '1.0.0'
                and left['sourceConfigured'] and left['settingsUnchanged'] and left['downloadStarted'])
            safety_preserved = (right['cancelled'] and right['initialVersion'] == '1.0.0'
                and right['finalVersion'] == '1.0.0' and right['sourceConfigured']
                and right['settingsUnchanged'] and right['downloadStarted'])
            safety_difference = left != right and safety_preserved
            results[case_id] = {
                'pi': per_product['pi'], 'pisharp': per_product['pisharp'],
                'classification': ('intentional-safety-difference' if safety_difference else 'strict'),
                'difference': ('Pi removes the previous package during an interrupted update; '
                               'PiSharp stages updates and retains version 1.0.0.' if safety_difference else None),
                'match': oracle_reproduced and safety_preserved,
            }
    finally:
        for item in registries.values():
            item['registry'].close()
        second_registry.close()
        interrupted_registry.close()
        shutil.rmtree(run_root, ignore_errors=True)

    cases = {}
    for case in case_specs:
        case_id = case['caseId']
        cases[case_id] = {
            'expected': case['expected'],
            'oracle': results[case_id].get('pi', {}).get('state'),
            'actual': results[case_id].get('pisharp', {}).get('state'),
            'passed': results[case_id].get('match') is True,
            'diagnostics': {
                'pi': results[case_id].get('pi', {}).get('commands'),
                'pisharp': results[case_id].get('pisharp', {}).get('commands'),
            },
        }
    for case_id in ('npm.update-cancelled', 'npm.update-interrupted'):
        cases[case_id] = {
            'classification': results[case_id].get('classification', 'mismatch'),
            'difference': results[case_id].get('difference'),
            'expected': {'cancelled': True, 'initialVersion': '1.0.0', 'finalVersion': '1.0.0',
                         'sourceConfigured': True, 'settingsUnchanged': True, 'downloadStarted': True},
            'oracle': results[case_id]['pi']['state'],
            'actual': results[case_id]['pisharp']['state'],
            'passed': results[case_id].get('match') is True,
            'diagnostics': {
                'pi': results[case_id]['pi'].get('diagnostics'),
                'pisharp': results[case_id]['pisharp'].get('diagnostics'),
            },
        }
    evidence = {
        'piSha': pi_sha,
        'pisharpSha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        'scenario': 'mutable-local-npm-registry-update-processes',
        'fixtureFamily': 'mutable-local-npm-registry',
        'cases': cases,
        'results': results,
        'match': all(case['passed'] for case in cases.values()),
        'limitations': [
            'This runner uses local loopback registries and does not contact the public npm registry.',
        ],
    }
    args.output.write_text(json.dumps(evidence, indent=2) + '\n')
    print(f"Recorded {len(cases)} mutable-registry npm update differentials: {args.output}")
    if not evidence['match']:
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
