#!/usr/bin/env python3
"""Compare managed package settings, trust and restart behavior in Pi and PiSharp processes."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/parity'))
from pi_reference import verify_pi_checkout

cli_spec = importlib.util.spec_from_file_location(
    'package_cli_process_for_configuration', Path(__file__).with_name('package-cli-process-differential.py'))
if cli_spec is None or cli_spec.loader is None:
    raise RuntimeError('Could not load package CLI helpers')
cli = importlib.util.module_from_spec(cli_spec)
cli_spec.loader.exec_module(cli)

npm_spec = importlib.util.spec_from_file_location(
    'managed_npm_update_for_configuration', Path(__file__).with_name('managed-npm-update-process-differential.py'))
if npm_spec is None or npm_spec.loader is None:
    raise RuntimeError('Could not load package update helpers')
npm = importlib.util.module_from_spec(npm_spec)
npm_spec.loader.exec_module(npm)


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def product_item(profile: Path, case_id: str, product: str) -> dict:
    item = npm.make_product(profile / case_id / product, product)
    item['env']['npm_config_offline'] = 'true'
    item['env']['npm_config_audit'] = 'false'
    item['env']['npm_config_fund'] = 'false'
    return item


def run(product: str, pi: Path, pisharp: Path, item: dict, args: list[str]) -> dict:
    return cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], args)


def package_scope(item: dict, source: str, scope: str) -> dict:
    path = item['agent'] / 'settings.json' if scope == 'user' else item['cwd'] / '.pi/settings.json'
    settings = cli.read_settings(path)
    sources = cli.package_sources(settings)
    return {'configured': source in sources, 'sourceCount': sources.count(source), 'sources': sources}


def resolves_to(path_value: str, base: Path, target: Path) -> bool:
    value = Path(path_value)
    return (value if value.is_absolute() else base / value).resolve() == target.resolve()


def project_trust(item: dict) -> bool | None:
    path = item['agent'] / 'trust.json'
    if not path.is_file():
        return None
    value = json.loads(path.read_text(encoding='utf-8'))
    return value.get(str(item['cwd'].resolve()))


def listed(item: dict, result: dict, source: str, scope: str) -> dict | None:
    entries = cli.list_entries(result, item['root'], item['agent'])
    return next((entry for entry in entries if entry['source'] == source and entry['scope'] == scope), None)


def installed_npm(item: dict, name: str, scope: str) -> bool:
    path = (item['cwd'] / '.pi/npm/node_modules' / name if scope == 'project'
            else item['agent'] / 'npm/node_modules' / name)
    return (path / 'package.json').is_file()


def state_for(item: dict, source: str, scope: str, install: dict | None,
              list_result: dict | None, *, expected: str, extra: dict | None = None) -> dict:
    configured = package_scope(item, source, scope)
    entry = listed(item, list_result, source, scope) if list_result is not None else None
    state = {
        'installExitCode': (0 if install is None or install['exitCode'] == 0 else 1),
        'listExitCode': (0 if list_result is None or list_result['exitCode'] == 0 else 1),
        'configured': configured['configured'],
        'sourceCount': configured['sourceCount'],
        'listed': entry is not None,
        'filtered': entry.get('filtered') if entry else None,
        'installedPathPresent': bool(entry and entry.get('installedPath')),
        'installed': installed_npm(item, source.split(':', 1)[-1].split('@', 1)[0], scope)
                     if source.startswith('npm:') else None,
        'projectTrust': project_trust(item),
    }
    if extra:
        state.update(extra)
    state['contractSatisfied'] = expected == 'ordinary' and (
        state['installExitCode'] == 0 and state['listExitCode'] == 0 and state['configured']
        and state['sourceCount'] == 1 and state['listed']
    )
    if expected == 'filtered':
        state['contractSatisfied'] = (state['listExitCode'] == 0 and state['configured']
                                      and state['listed'] and state['filtered'] is True)
    elif expected == 'disabled':
        state['contractSatisfied'] = (state['listExitCode'] == 0 and state['configured']
                                      and state['listed'] and state['filtered'] is True
                                      and state.get('autoloadDisabled') is True)
    elif expected == 'not-listed':
        state['contractSatisfied'] = (state['listExitCode'] == 0 and state['configured']
                                      and not state['listed'])
    elif expected == 'write-failed':
        state['contractSatisfied'] = state['configured'] is False
    elif expected == 'external-change':
        state['contractSatisfied'] = (state['installExitCode'] == 0 and state['configured']
                                      and state.get('externalChangePreserved') is True)
    return state


def run_install_list_pair(profile: Path, case_id: str, source: str, scope: str, pi: Path, pisharp: Path,
                          install_args: list[str], list_args: list[str], *, expected: str = 'ordinary',
                          initial_settings: dict | None = None, trust: bool | None = None,
                          extra_check=None) -> dict:
    outcomes = {}
    for product in ('pi', 'pisharp'):
        item = product_item(profile, case_id, product)
        if initial_settings is not None:
            path = item['agent'] / 'settings.json' if scope == 'user' else item['cwd'] / '.pi/settings.json'
            write_json(path, initial_settings)
        if trust is not None:
            write_json(item['agent'] / 'trust.json', {str(item['cwd'].resolve()): trust})
        install = run(product, pi, pisharp, item, [*install_args, source]) if install_args else None
        listed_result = run(product, pi, pisharp, item, list_args)
        extra = extra_check(item) if extra_check else None
        outcomes[product] = state_for(item, source, scope, install, listed_result, expected=expected, extra=extra)
    return outcomes


def pack_tarball(root: Path, name: str, environment: dict[str, str], *, postinstall: str | None = None) -> Path:
    directory = root / f'{name}-1.0.0'
    directory.mkdir(parents=True)
    manifest = {'name': name, 'version': '1.0.0'}
    if postinstall is not None:
        manifest['scripts'] = {'postinstall': 'node postinstall.cjs'}
        (directory / 'postinstall.cjs').write_text(postinstall, encoding='utf-8')
    write_json(directory / 'package.json', manifest)
    packed = subprocess.run(['npm', 'pack', '--ignore-scripts', '--pack-destination', str(root), '--json'],
                            cwd=directory, env=environment, capture_output=True, text=True,
                            check=True, timeout=60)
    filename = json.loads(packed.stdout)[0]['filename']
    return root / filename


def run_special_install(profile: Path, scratch: Path, case_id: str, source: str, name: str,
                        pi: Path, pisharp: Path, mode: str) -> dict:
    outcomes = {}
    for product in ('pi', 'pisharp'):
        item = product_item(profile, case_id, product)
        settings_path = item['agent'] / 'settings.json'
        if mode == 'write-failed':
            settings_path.unlink()
            settings_path.mkdir()
        before = cli.setting_snapshot(item['agent'], item['cwd']) if mode != 'write-failed' else None
        result = run(product, pi, pisharp, item, ['install', source])
        if mode == 'write-failed':
            state = {
                'installExitCode': 0 if result['exitCode'] == 0 else 1,
                'settingsPathRemainsDirectory': settings_path.is_dir(),
                'packageInstalled': installed_npm(item, name, 'user'),
                'configured': False,
            }
            state['contractSatisfied'] = (state['settingsPathRemainsDirectory']
                                          and state['configured'] is False)
        else:
            settings = cli.read_settings(settings_path)
            state = {
                'installExitCode': 0 if result['exitCode'] == 0 else 1,
                'configured': source in cli.package_sources(settings),
                'externalChangePreserved': settings.get('configurationProbe') == 'written-during-install',
                'settingsChanged': before != cli.setting_snapshot(item['agent'], item['cwd']),
            }
            state['contractSatisfied'] = (state['installExitCode'] == 0 and state['configured']
                                          and state['externalChangePreserved'])
        outcomes[product] = state
    return outcomes


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
    scratch = args.profile / 'fixture'
    scratch.mkdir(exist_ok=True)
    pack_home = scratch / 'pack-home'
    pack_home.mkdir(exist_ok=True)
    pack_environment = {
        'PATH': os.environ.get('PATH', ''), 'HOME': str(pack_home), 'TMPDIR': str(scratch),
        'npm_config_cache': str(scratch / 'npm-cache'), 'npm_config_offline': 'true',
    }
    cases = {}

    # User and project package declarations survive a separate list process.
    user_tarball = pack_tarball(scratch, 'configuration-user', pack_environment)
    user_source = f'npm:configuration-user@file:{user_tarball}'
    cases['config.user'] = run_install_list_pair(args.profile, 'config.user', user_source, 'user',
        args.pi, args.pisharp, ['install'], ['list'])

    project_tarball = pack_tarball(scratch, 'configuration-project', pack_environment)
    project_source = f'npm:configuration-project@file:{project_tarball}'
    cases['config.project'] = run_install_list_pair(args.profile, 'config.project', project_source, 'project',
        args.pi, args.pisharp, ['install', '--local', '--approve'], ['list', '--approve'])

    # A relative local source is normalized and re-read by a second CLI process.
    relative_source = './relative-package'
    outcomes = {}
    for product in ('pi', 'pisharp'):
        item = product_item(args.profile, 'config.relative-source', product)
        package_directory = item['cwd'] / 'relative-package'
        package_directory.mkdir()
        write_json(package_directory / 'package.json', {'name': 'relative-configuration-package', 'version': '1.0.0'})
        install = run(product, args.pi, args.pisharp, item, ['install', relative_source])
        listed_result = run(product, args.pi, args.pisharp, item, ['list'])
        scope = package_scope(item, relative_source, 'user')
        configured_sources = scope['sources']
        entry = next((entry for entry in cli.list_entries(listed_result, item['root'], item['agent'])
                      if entry['scope'] == 'user'
                      and resolves_to(entry['source'], item['agent'], package_directory)), None)
        outcomes[product] = {
            'installExitCode': 0 if install['exitCode'] == 0 else 1,
            'listExitCode': 0 if listed_result['exitCode'] == 0 else 1,
            'configured': any(resolves_to(value, item['agent'], package_directory) for value in configured_sources),
            'listed': entry is not None, 'sourceCount': len(configured_sources),
        }
        outcomes[product]['contractSatisfied'] = (outcomes[product]['installExitCode'] == 0
            and outcomes[product]['listExitCode'] == 0 and outcomes[product]['configured']
            and outcomes[product]['listed'] and outcomes[product]['sourceCount'] == 1)
    cases['config.relative-source'] = outcomes

    filtered_source = 'npm:configuration-filtered@1.0.0'
    cases['config.filters'] = run_install_list_pair(args.profile, 'config.filters', filtered_source, 'project',
        args.pi, args.pisharp, [], ['list', '--approve'], expected='filtered',
        initial_settings={'packages': [{'source': filtered_source, 'skills': ['skills/only/**']}]}, trust=True)

    disabled_source = 'npm:configuration-disabled@1.0.0'
    cases['config.disabled'] = run_install_list_pair(args.profile, 'config.disabled', disabled_source, 'project',
        args.pi, args.pisharp, [], ['list', '--approve'], expected='disabled',
        initial_settings={'packages': [{'source': disabled_source, 'autoload': False}]}, trust=True,
        extra_check=lambda item: {'autoloadDisabled': cli.read_settings(item['cwd'] / '.pi/settings.json')
                                  .get('packages', [{}])[0].get('autoload') is False})

    trust_tarball = pack_tarball(scratch, 'configuration-trust', pack_environment)
    trust_source = f'npm:configuration-trust@file:{trust_tarball}'
    denied = {}
    for product in ('pi', 'pisharp'):
        item = product_item(args.profile, 'config.trust-denied', product)
        write_json(item['agent'] / 'trust.json', {str(item['cwd'].resolve()): False})
        install = run(product, args.pi, args.pisharp, item, ['install', '--local', trust_source])
        denied[product] = {
            'installExitCode': 0 if install['exitCode'] == 0 else 1,
            'projectConfigured': package_scope(item, trust_source, 'project')['configured'],
            'trustDecision': project_trust(item),
            'packageInstalled': installed_npm(item, 'configuration-trust', 'project'),
        }
        denied[product]['contractSatisfied'] = (denied[product]['installExitCode'] != 0
            and denied[product]['projectConfigured'] is False
            and denied[product]['trustDecision'] is False
            and denied[product]['packageInstalled'] is False)
    cases['config.trust-denied'] = denied
    cases['config.trust-approved'] = run_install_list_pair(args.profile, 'config.trust-approved', trust_source,
        'project', args.pi, args.pisharp, ['install', '--local', '--approve'], ['list', '--approve'])

    failed_write_tarball = pack_tarball(scratch, 'configuration-write-failure', pack_environment)
    failed_write_source = f'npm:configuration-write-failure@file:{failed_write_tarball}'
    cases['config.failed-write'] = run_special_install(args.profile, scratch, 'config.failed-write',
        failed_write_source, 'configuration-write-failure', args.pi, args.pisharp, 'write-failed')

    concurrent_name = 'configuration-concurrent-change'
    concurrent_tarball = pack_tarball(scratch, concurrent_name, pack_environment,
        postinstall=("const fs=require('node:fs'); const p=process.env.PI_CODING_AGENT_DIR || "
                     "process.env.PISHARP_AGENT_DIR; const f=p+'/settings.json'; "
                     "const s=JSON.parse(fs.readFileSync(f,'utf8')); "
                     "s.configurationProbe='written-during-install'; "
                     "fs.writeFileSync(f,JSON.stringify(s,null,2)+'\\n');"))
    concurrent_source = f'npm:{concurrent_name}@file:{concurrent_tarball}'
    cases['config.concurrent-change'] = run_special_install(args.profile, scratch, 'config.concurrent-change',
        concurrent_source, concurrent_name, args.pi, args.pisharp, 'external-change')

    restart_tarball = pack_tarball(scratch, 'configuration-restart', pack_environment)
    restart_source = f'npm:configuration-restart@file:{restart_tarball}'
    cases['config.restart'] = run_install_list_pair(args.profile, 'config.restart', restart_source, 'user',
        args.pi, args.pisharp, ['install'], ['list'])

    normalized_cases = {}
    for case_id, outcomes in cases.items():
        pi_state, actual_state = outcomes['pi'], outcomes['pisharp']
        equal = pi_state == actual_state
        safety_difference = (case_id == 'config.failed-write' and not equal
            and pi_state.get('installExitCode') == 0 and actual_state.get('installExitCode') != 0
            and pi_state.get('settingsPathRemainsDirectory') is True
            and actual_state.get('settingsPathRemainsDirectory') is True
            and pi_state.get('configured') is False and actual_state.get('configured') is False
            and pi_state.get('packageInstalled') is True and actual_state.get('packageInstalled') is True
            and pi_state.get('contractSatisfied') is True and actual_state.get('contractSatisfied') is True)
        strict_pass = (equal and pi_state.get('contractSatisfied') is True
                       and actual_state.get('contractSatisfied') is True)
        normalized_cases[case_id] = {'oracle': pi_state, 'actual': actual_state, 'equal': equal,
            'safetyDifferenceAccepted': safety_difference,
            'contractSatisfied': strict_pass or safety_difference}
    match = all(case['contractSatisfied'] for case in normalized_cases.values())
    report = {
        'piSha': pi_sha,
        'piSharpBaseSha': subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, check=True,
                                         capture_output=True, text=True).stdout.strip(),
        'fixtureVersion': 'managed-package-configuration-v1',
        'scenario': 'paired-package-settings-trust-restart-processes',
        'match': match,
        'cases': normalized_cases,
        'limitations': ['Settings are isolated under per-product roots; no user configuration is read.'],
    }
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    print(json.dumps({'cases': len(normalized_cases), 'match': match, 'output': str(args.output)}, sort_keys=True))
    return 0 if match else 1


if __name__ == '__main__':
    raise SystemExit(main())
