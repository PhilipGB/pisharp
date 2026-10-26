#!/usr/bin/env python3
"""Compare pinned Pi and PiSharp managed Git package installation in disposable roots."""

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

cli_path = Path(__file__).with_name('package-cli-process-differential.py')
cli_spec = importlib.util.spec_from_file_location('package_cli_process', cli_path)
if cli_spec is None or cli_spec.loader is None:
    raise RuntimeError(f'Could not load package CLI process helpers from {cli_path}')
cli = importlib.util.module_from_spec(cli_spec)
cli_spec.loader.exec_module(cli)

resource = cli.resource


def find_git_package(product_root: Path):
    try:
        return cli.find_git_package(product_root, 'managed-git-package')
    except AssertionError:
        return None


def create_product(profile: Path, root: Path, product: str) -> dict:
    product_root = root / product
    cwd = product_root / 'work'
    agent = product_root / 'agent'
    markers = product_root / 'install-markers'
    trace = product_root / 'extension-load.log'
    cwd.mkdir(parents=True)
    (cwd / '.pi').mkdir()
    agent.mkdir()
    (agent / 'settings.json').write_text('{}\n')
    (cwd / '.pi/settings.json').write_text('{}\n')
    (cwd / '.pi/project-trust-extension.json').write_text('{"decision":"yes","remember":true}\n')
    return {
        'root': product_root,
        'cwd': cwd,
        'agent': agent,
        'markers': markers,
        'env': cli.base_environment(profile, product, agent, cwd, markers, trace),
    }


def install_state(product: str, item: dict, source: str, result: dict, expected_head: str | None) -> dict:
    settings = cli.package_sources(cli.read_settings(item['agent'] / 'settings.json'))
    package_root = find_git_package(item['root'])
    head = cli.git_head(package_root, item['env']) if package_root else None
    dependency = (package_root / 'node_modules/managed-extension-dependency').is_dir() if package_root else False
    host_peer = (package_root / 'node_modules/pisharp-host-api-must-not-install').exists() if package_root else False
    return {
        'exitCode': result['exitCode'],
        'sourceConfigured': source in settings,
        'sourceOccurrences': settings.count(source),
        'installed': package_root is not None,
        'head': head,
        'expectedHead': expected_head,
        'dependencyInstalled': dependency,
        'hostPeerInstalled': host_peer,
    }


def run_one_case(name: str, profile: Path, root: Path, fixture: dict, pi: Path,
                 pisharp: Path, source_for_product=None, expected_head: str | None = None,
                 configure_rewrites: bool = False) -> dict:
    products = {product: create_product(profile, root, product) for product in ('pi', 'pisharp')}
    source_fn = source_for_product or (lambda product, item: fixture['unpinned_source'])
    states = {}
    commands = {}
    for product, item in products.items():
        source = source_fn(product, item)
        if configure_rewrites:
            git_config = Path(item['env']['GIT_CONFIG_GLOBAL'])
            with git_config.open('a', encoding='utf-8') as stream:
                stream.write(
                    f'\n[url "git://127.0.0.1:{fixture["port"]}/fixture/"]\n'
                    '  insteadOf = https://github.invalid/fixture/\n'
                    '  insteadOf = git@github.invalid:fixture/\n'
                )
        result = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], ['install', source])
        commands[product] = cli.normalized_command(result, item['root'], item['agent'])
        states[product] = install_state(product, item, source, result, expected_head)
        states[product]['source'] = source
        states[product]['command'] = commands[product]
    equal = states['pi'] == states['pisharp']
    return {'name': name, 'oracle': states['pi'], 'actual': states['pisharp'], 'equal': equal}


def run_repeated_install(profile: Path, root: Path, fixture: dict, pi: Path, pisharp: Path) -> dict:
    products = {product: create_product(profile, root, product) for product in ('pi', 'pisharp')}
    outcomes = {}
    for product, item in products.items():
        source = fixture['pinned_source']
        first = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], ['install', source])
        package_root = find_git_package(item['root'])
        first_state = install_state(product, item, source, first, fixture['v1_head'])
        before = cli.content_tree_digest(package_root) if package_root else None
        second = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], ['install', source])
        after_root = find_git_package(item['root'])
        after = cli.content_tree_digest(after_root) if after_root else None
        settings = cli.package_sources(cli.read_settings(item['agent'] / 'settings.json'))
        outcomes[product] = {
            'first': first_state,
            'secondExitCode': second['exitCode'],
            'sourceOccurrences': settings.count(source),
            'headAfterSecond': cli.git_head(after_root, item['env']) if after_root else None,
            'contentUnchanged': before == after,
        }
    return {'oracle': outcomes['pi'], 'actual': outcomes['pisharp'],
            'equal': outcomes['pi'] == outcomes['pisharp']}


def run_url_shorthands(profile: Path, root: Path, fixture: dict, pi: Path, pisharp: Path) -> dict:
    git_url = fixture['unpinned_source'][4:]
    variants = {
        'explicit-git-prefix': lambda: 'git:' + git_url + '@main',
        'explicit-https-url': lambda: 'https://github.invalid/fixture/repo.git@main',
        'host-shorthand': lambda: 'git:github.invalid/fixture/repo.git@main',
        'scp-shorthand': lambda: 'git:git@github.invalid:fixture/repo.git@main',
    }
    states = {product: {} for product in ('pi', 'pisharp')}
    diagnostics = {product: {} for product in ('pi', 'pisharp')}
    for product in ('pi', 'pisharp'):
        for variant, make_source in variants.items():
            variant_root = root / product / variant
            item = create_product(profile / product / variant, variant_root, product)
            source = make_source()
            if variant in ('explicit-https-url', 'host-shorthand', 'scp-shorthand'):
                git_config = Path(item['env']['GIT_CONFIG_GLOBAL'])
                with git_config.open('a', encoding='utf-8') as stream:
                    stream.write(
                        f'\n[url "git://127.0.0.1:{fixture["port"]}/fixture/"]\n'
                        '  insteadOf = https://github.invalid/fixture/\n'
                        '  insteadOf = git@github.invalid:fixture/\n'
                    )
            result = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], ['install', source])
            state = install_state(product, item, source, result, fixture['v1_head'])
            rewrite_config = subprocess.run(
                ['git', 'config', '--file', str(item['env']['GIT_CONFIG_GLOBAL']), '--get-regexp', r'^url\.'],
                capture_output=True, text=True)
            state['gitRewriteConfig'] = rewrite_config.stdout.splitlines()
            state['gitRewriteConfigReadExitCode'] = rewrite_config.returncode
            state['gitDaemonRunningAfterInstall'] = fixture['daemon'].poll() is None
            state['gitDaemonPort'] = fixture['port']
            diagnostics[product][variant] = cli.normalized_command(result, item['root'], item['agent'])
            state['resolved'] = (state['exitCode'] == 0 and state['sourceConfigured']
                                 and state['installed'] and state['head'] == fixture['v1_head'])
            states[product][variant] = state
    return {'oracle': states['pi'], 'actual': states['pisharp'], 'equal': states['pi'] == states['pisharp'],
            'diagnostics': diagnostics}


def run_failed_clone(profile: Path, root: Path, fixture: dict, pi: Path, pisharp: Path) -> dict:
    cli.stop_daemon(fixture['daemon'])
    states = {}
    for product in ('pi', 'pisharp'):
        item = create_product(profile, root / product, product)
        source = fixture['unpinned_source']
        before = cli.setting_snapshot(item['agent'], item['cwd'])
        result = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], ['install', source])
        after = cli.setting_snapshot(item['agent'], item['cwd'])
        package_root = find_git_package(item['root'])
        states[product] = {
            'exitCode': result['exitCode'],
            'settingsUnchanged': before == after,
            'sourceConfigured': source in cli.package_sources(cli.read_settings(item['agent'] / 'settings.json')),
            'installed': package_root is not None,
        }
    fixture['daemon'] = cli.register_daemon(cli.start_git_daemon(
        fixture['repository'].parent.parent, fixture['port'], fixture['environment']))
    return {'oracle': states['pi'], 'actual': states['pisharp'], 'equal': states['pi'] == states['pisharp']}


def cancellation_state(product: str, pi: Path, pisharp: Path, item: dict, source: str) -> dict:
    marker = item['root'] / 'git-clone-started.marker'
    wrapper = item['root'] / 'shim-bin'
    wrapper.mkdir(parents=True)
    real_git = subprocess.run(['which', 'git'], check=True, capture_output=True, text=True).stdout.strip()
    git_shim = wrapper / 'git'
    git_shim.write_text(
        '#!/bin/sh\n'
        'if [ "$1" = "clone" ]; then echo started > "$PISHARP_TEST_GIT_CLONE_MARKER"; exec /usr/bin/sleep 60; fi\n'
        f'exec {real_git} "$@"\n', encoding='utf-8')
    git_shim.chmod(0o755)
    item['env']['PATH'] = str(wrapper) + os.pathsep + item['env'].get('PATH', '')
    item['env']['PISHARP_TEST_GIT_CLONE_MARKER'] = str(marker)
    before = cli.setting_snapshot(item['agent'], item['cwd'])
    command = cli.command_for(product, pi, pisharp, ['install', source])
    with tempfile.TemporaryFile() as stdout, tempfile.TemporaryFile() as stderr:
        process = subprocess.Popen(command, cwd=item['cwd'], env=item['env'], stdout=stdout, stderr=stderr,
                                   start_new_session=True)
        deadline = time.monotonic() + 30
        try:
            while time.monotonic() < deadline and process.poll() is None and not marker.is_file():
                time.sleep(0.05)
            started = marker.is_file()
            if started and process.poll() is None:
                os.killpg(process.pid, signal.SIGINT)
            try:
                exit_code = process.wait(timeout=20)
                reaped = True
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
                exit_code, reaped = process.returncode, False
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)
        settings_after = cli.setting_snapshot(item['agent'], item['cwd'])
        package_root = find_git_package(item['root'])
        return {
            'started': started,
            'exitCode': 0 if exit_code == 0 else 1,
            'processReaped': reaped,
            'settingsUnchanged': before == settings_after,
            'sourceConfigured': source in cli.package_sources(cli.read_settings(item['agent'] / 'settings.json')),
            'installed': package_root is not None,
        }


def run_cancellation(profile: Path, root: Path, fixture: dict, pi: Path, pisharp: Path) -> dict:
    states = {}
    source = fixture['unpinned_source']
    for product in ('pi', 'pisharp'):
        item = create_product(profile, root / product, product)
        states[product] = cancellation_state(product, pi, pisharp, item, source)
    return {'oracle': states['pi'], 'actual': states['pisharp'], 'equal': states['pi'] == states['pisharp']}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.pi = args.pi.resolve()
    args.pisharp = args.pisharp.resolve()
    args.profile = args.profile.resolve()
    args.output = args.output.resolve()
    args.profile.mkdir(parents=True, exist_ok=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    pi_sha = verify_pi_checkout(args.pi)
    results: dict[str, dict] = {'pi': {}, 'pisharp': {}}
    with tempfile.TemporaryDirectory(prefix='managed-git-install-', dir=str(args.profile)) as scratch_name:
        scratch = Path(scratch_name)
        setup = create_product(args.profile / 'fixture-profile', scratch / 'fixture-env', 'pi')
        fixture = resource.create_managed_git_repository(scratch, args.pi, setup['env'])
        fixture['daemon'] = cli.register_daemon(fixture['daemon'])
        fixture['environment'] = setup['env']
        subprocess.run(['git', '--git-dir', str(fixture['repository']), 'config',
                        'uploadpack.allowReachableSHA1InWant', 'true'], check=True,
                       capture_output=True, text=True)
        git_url = fixture['unpinned_source'][4:]
        fixture['port'] = int(git_url.split(':')[-1].split('/')[0])

        case_specs = [
            ('git.install-branch', lambda: fixture['unpinned_source'] + '@main', fixture['v1_head']),
            ('git.install-tag', lambda: fixture['pinned_source'], fixture['v1_head']),
            ('git.install-commit', lambda: fixture['unpinned_source'] + '@' + fixture['v2_head'], fixture['v2_head']),
            ('git.install-floating', lambda: fixture['unpinned_source'], fixture['v1_head']),
            ('git.install-invalid-ref', lambda: fixture['unpinned_source'] + '@missing-parity-ref', None),
        ]
        cases = {}
        for index, (case_id, make_source, expected_head) in enumerate(case_specs):
            if case_id == 'git.install-commit':
                subprocess.run(['git', '--git-dir', str(fixture['repository']), 'update-ref',
                                'refs/heads/main', fixture['v2_head']], check=True, capture_output=True, text=True)
            case_root = scratch / 'cases' / f'{index}-{case_id}'
            products = {product: create_product(args.profile, case_root, product)
                        for product in ('pi', 'pisharp')}
            outcomes = {}
            for product, item in products.items():
                source = make_source()
                result = cli.run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'], ['install', source])
                state = install_state(product, item, source, result, expected_head)
                if case_id == 'git.install-invalid-ref':
                    state['contractSatisfied'] = (state['exitCode'] != 0 and not state['sourceConfigured']
                                                  and not state['installed'])
                else:
                    state['contractSatisfied'] = (state['exitCode'] == 0 and state['sourceConfigured']
                                                  and state['installed'] and state['head'] == expected_head)
                outcomes[product] = state
            cases[case_id] = {'oracle': outcomes['pi'], 'actual': outcomes['pisharp'],
                              'equal': outcomes['pi'] == outcomes['pisharp']}
            if case_id == 'git.install-commit':
                subprocess.run(['git', '--git-dir', str(fixture['repository']), 'update-ref',
                                'refs/heads/main', fixture['v1_head']], check=True, capture_output=True, text=True)

        repeated = run_repeated_install(args.profile, scratch / 'repeated', fixture, args.pi, args.pisharp)
        cases['git.install-existing'] = repeated
        for side in ('oracle', 'actual'):
            state = repeated[side]
            state['contractSatisfied'] = (state['first']['exitCode'] == 0 and state['secondExitCode'] == 0
                                          and state['sourceOccurrences'] == 1
                                          and state['headAfterSecond'] == fixture['v1_head']
                                          and state['contentUnchanged'])

        dependencies = run_one_case('git.install-missing-dependencies', args.profile,
                                    scratch / 'dependencies', fixture, args.pi, args.pisharp,
                                    expected_head=fixture['v1_head'])
        cases['git.install-missing-dependencies'] = dependencies
        for side in ('oracle', 'actual'):
            state = dependencies[side]
            state['contractSatisfied'] = (state['exitCode'] == 0 and state['installed']
                                          and state['dependencyInstalled'] and not state['hostPeerInstalled'])

        url_case = run_url_shorthands(args.profile, scratch / 'url-shorthands', fixture, args.pi, args.pisharp)
        cases['git.install-url-shorthands'] = url_case
        for side in ('oracle', 'actual'):
            url_case[side]['contractSatisfied'] = all(value['resolved'] for value in url_case[side].values())

        # Failure and cancellation are run only after the deterministic successful cases.
        failed = run_failed_clone(args.profile, scratch / 'failed-clone', fixture, args.pi, args.pisharp)
        cases['git.install-failure'] = failed
        for side in ('oracle', 'actual'):
            failed[side]['contractSatisfied'] = (failed[side]['exitCode'] != 0
                                                 and failed[side]['settingsUnchanged']
                                                 and not failed[side]['sourceConfigured']
                                                 and not failed[side]['installed'])

        cancelled = run_cancellation(args.profile, scratch / 'cancelled', fixture, args.pi, args.pisharp)
        cases['git.install-cancelled'] = cancelled
        for side in ('oracle', 'actual'):
            cancelled[side]['contractSatisfied'] = (cancelled[side]['started']
                                                    and cancelled[side]['exitCode'] != 0
                                                    and cancelled[side]['processReaped']
                                                    and cancelled[side]['settingsUnchanged']
                                                    and not cancelled[side]['sourceConfigured']
                                                    and not cancelled[side]['installed'])

        cli.stop_daemon(fixture['daemon'])

    for case_id, result in cases.items():
        results['pi'][case_id] = result['oracle']
        results['pisharp'][case_id] = result['actual']
    match = all(result['equal'] and result['oracle']['contractSatisfied']
                and result['actual']['contractSatisfied'] for result in cases.values())
    report = {
        'piSha': pi_sha,
        'piSharpBaseSha': subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT,
                                         check=True, capture_output=True, text=True).stdout.strip(),
        'fixtureVersion': 'managed-git-install-v1',
        'scenario': 'paired-managed-git-install-local-daemon',
        'match': match,
        'cases': cases,
        'results': results,
    }
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    print(json.dumps({'match': match, 'cases': len(cases), 'output': str(args.output)}, sort_keys=True))
    return 0 if match else 1


if __name__ == '__main__':
    raise SystemExit(main())
