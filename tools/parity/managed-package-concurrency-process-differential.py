#!/usr/bin/env python3
"""Exercise barrier-controlled managed-package races in pinned Pi and PiSharp processes."""

from __future__ import annotations

import argparse
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
sys.path.insert(0, str(ROOT / 'tools/parity/managed_packages'))
from clone_race import clone_destination_collision as proven_clone_collision
from pi_reference import verify_pi_checkout


def load_module(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f'Unable to load parity fixture helper {path}')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


cli = load_module(Path(__file__).with_name('package-cli-process-differential.py'), 'concurrency_cli')
updates = load_module(Path(__file__).with_name('managed-npm-update-process-differential.py'), 'concurrency_npm')
git_installs = load_module(Path(__file__).with_name('managed-git-install-process-differential.py'), 'concurrency_git')
resource = cli.resource

REQUIRED_CASE_IDS = (
    'concurrent.same-npm-install', 'concurrent.same-git-install', 'concurrent.install-remove',
    'concurrent.update-remove', 'concurrent.two-process-update', 'concurrent.update-discovery',
    'concurrent.cancelled-lock', 'concurrent.crashed-writer', 'concurrent.external-settings-change',
)

GATE_SOURCE = r'''import os, sys, time
marker = sys.argv[1]
participants = int(sys.argv[2])
os.makedirs(os.path.dirname(marker), exist_ok=True)
fd = os.open(marker, os.O_CREAT | os.O_APPEND | os.O_WRONLY, 0o600)
os.write(fd, (str(os.getpid()) + "\n").encode())
os.close(fd)
deadline = time.monotonic() + 30
while time.monotonic() < deadline:
    try:
        with open(marker, encoding="ascii") as stream:
            if len([line for line in stream if line.strip()]) >= participants:
                os.execvpe(sys.argv[3], sys.argv[3:], os.environ)
    except FileNotFoundError:
        pass
    time.sleep(0.01)
raise SystemExit(124)
'''


class Capture:
    def __init__(self, process: subprocess.Popen, stdout, stderr, started: float):
        self.process = process
        self.stdout = stdout
        self.stderr = stderr
        self.started = started

    def finish(self, timeout: float = 120) -> dict:
        reaped = True
        try:
            code = self.process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            os.killpg(self.process.pid, signal.SIGKILL)
            code = self.process.wait(timeout=15)
            reaped = False
        self.stdout.seek(0)
        self.stderr.seek(0)
        result = {
            'exitCode': code, 'reaped': reaped,
            'elapsedSeconds': round(time.monotonic() - self.started, 3),
            'stdout': self.stdout.read().decode(errors='replace'),
            'stderr': self.stderr.read().decode(errors='replace'),
        }
        self.stdout.close()
        self.stderr.close()
        return result

    def stop(self, signum: int = signal.SIGKILL) -> dict:
        if self.process.poll() is None:
            os.killpg(self.process.pid, signum)
        return self.finish(timeout=15)


def start_cli(product: str, pi: Path, pisharp: Path, item: dict, args: list[str],
              *, gate: Path | None = None, participants: int = 2) -> Capture:
    command = cli.command_for(product, pi, pisharp, args)
    if gate is not None:
        gate.parent.mkdir(parents=True, exist_ok=True)
        gate.write_text(GATE_SOURCE, encoding='utf-8')
        marker = gate.with_suffix('.ready')
        command = [sys.executable, str(gate), str(marker), str(participants), *command]
    stdout, stderr = tempfile.TemporaryFile(), tempfile.TemporaryFile()
    started = time.monotonic()
    process = subprocess.Popen(command, cwd=item['cwd'], env=item['env'], stdout=stdout, stderr=stderr,
                               start_new_session=True)
    return Capture(process, stdout, stderr, started)


def finish_pair(captures: list[Capture], gate: Path | None = None, timeout: float = 120) -> list[dict]:
    deadline = time.monotonic() + 35
    marker = gate.with_suffix('.ready') if gate is not None else None
    while marker is not None and time.monotonic() < deadline:
        if marker.is_file() and len(marker.read_text(encoding='ascii').splitlines()) >= len(captures):
            break
        if any(capture.process.poll() is not None for capture in captures):
            break
        time.sleep(0.02)
    results = []
    try:
        for capture in captures:
            results.append(capture.finish(timeout))
    except Exception:
        for capture in captures:
            if capture.process.poll() is None:
                capture.stop()
        raise
    return results


def parallel_pair(product: str, pi: Path, pisharp: Path, item: dict, commands: list[list[str]],
                  gate: Path, timeout: float = 120) -> list[dict]:
    marker = gate.with_suffix('.ready')
    marker.unlink(missing_ok=True)
    captures = [start_cli(product, pi, pisharp, item, args, gate=gate) for args in commands]
    return finish_pair(captures, gate, timeout)


def state(item: dict, source: str, package_name: str, command_results: list[dict],
          *, expected_version: str | None = None, package_kind: str = 'npm') -> dict:
    settings_path = item['agent'] / 'settings.json'
    try:
        settings = cli.read_settings(settings_path)
        sources = cli.package_sources(settings)
        settings_valid = True
    except (OSError, ValueError, json.JSONDecodeError):
        sources, settings_valid = [], False
    if package_kind == 'git':
        package_root = git_installs.find_git_package(item['root'])
        manifest = package_root / 'package.json' if package_root else None
        version = cli.git_head(package_root, item['env']) if package_root else None
    else:
        package_root = item['agent'] / 'npm/node_modules' / package_name
        manifest = package_root / 'package.json'
        try:
            version = json.loads(manifest.read_text(encoding='utf-8')).get('version') if manifest.is_file() else None
        except (OSError, json.JSONDecodeError):
            version = 'corrupt'
    npm_root = item['agent'] / 'npm'
    return {
        'commandOutcomeClasses': sorted(
            'success' if result['exitCode'] == 0 else
            'cancelled' if result['exitCode'] in (-2, 130) else
            'killed' if result['exitCode'] == -9 else 'failure'
            for result in command_results),
        'allProcessesReaped': all(result.get('reaped') is True for result in command_results),
        'boundedSeconds': all(result.get('elapsedSeconds', 10**9) < 120 for result in command_results),
        'settingsValid': settings_valid,
        'sourceConfigured': source in sources,
        'sourceOccurrences': sources.count(source),
        'packageRootPresent': package_root is not None if package_kind == 'git' else package_root.is_dir(),
        'installedVersion': version,
        'packageManifestValid': manifest is not None and manifest.is_file() and version != 'corrupt',
        'expectedVersion': expected_version,
        'updateArtifacts': sorted(path.name for path in npm_root.parent.glob(npm_root.name + '.pisharp-update-*')),
        'diagnostics': [command_summary(result) for result in command_results],
    }


def origin_head_clone_race(command_results: list[dict]) -> bool:
    diagnostics = '\n'.join(result.get('stderr', '') for result in command_results)
    return ('error: Not a valid ref: refs/remotes/origin/main' in diagnostics
            and 'git rev-parse origin/HEAD failed with code 128' in diagnostics)


def clone_destination_collision(command_results: list[dict]) -> bool:
    errors = [result.get('stderr', '') for result in command_results if result.get('exitCode') != 0]
    return proven_clone_collision(errors)


def npm_target_install_race(command_results: list[dict]) -> bool:
    return any(
        result.get('exitCode') != 0
        and 'npm error code ENOENT' in result.get('stderr', '')
        and ('node_modules/concurrent-same-npm/package.json' in result.get('stderr', '')
             or ('Cannot cd into' in result.get('stderr', '')
                 and 'node_modules/concurrent-same-npm' in result.get('stderr', '')))
        for result in command_results
    )


def is_same_npm_install_safety_difference(pi: dict, pisharp: dict,
                                          oracle: dict, actual: dict) -> bool:
    return (oracle != actual
            and pi.get('npmTargetInstallRace') is True
            and pi.get('commandOutcomeClasses') in (['failure', 'failure'], ['failure', 'success'])
            and pi.get('allProcessesReaped') is True
            and pi.get('boundedSeconds') is True
            and pi.get('settingsValid') is True
            and pi.get('sourceOccurrences', 0) <= 1
            and not pi.get('updateArtifacts')
            and pisharp.get('allProcessesReaped') is True
            and pisharp.get('boundedSeconds') is True
            and pisharp.get('settingsValid') is True
            and pisharp.get('sourceConfigured') is True
            and pisharp.get('sourceOccurrences') == 1
            and pisharp.get('packageRootPresent') is True
            and pisharp.get('installedVersion') == pisharp.get('expectedVersion')
            and pisharp.get('packageManifestValid') is True
            and not pisharp.get('updateArtifacts')
            and pisharp.get('commandOutcomeClasses') == ['success', 'success'])


def is_same_git_install_safety_difference(pi: dict, pisharp: dict,
                                          oracle: dict, actual: dict) -> bool:
    return (oracle != actual
            and (pi.get('originHeadCloneRace') is True or pi.get('cloneDestinationCollision') is True)
            and pi.get('commandOutcomeClasses') == ['failure', 'failure']
            and pi.get('settingsValid') is True
            and pi.get('sourceConfigured') is False
            and pi.get('sourceOccurrences') == 0
            and pi.get('packageRootPresent') is False
            and pi.get('installedVersion') is None
            and pi.get('packageManifestValid') is False
            and pisharp.get('allProcessesReaped') is True
            and pisharp.get('boundedSeconds') is True
            and pisharp.get('settingsValid') is True
            and pisharp.get('sourceConfigured') is True
            and pisharp.get('sourceOccurrences') == 1
            and pisharp.get('packageRootPresent') is True
            and pisharp.get('installedVersion') == pisharp.get('expectedVersion')
            and pisharp.get('packageManifestValid') is True
            and 'success' in pisharp.get('commandOutcomeClasses', []))


def command_summary(result: dict) -> dict:
    summary = {key: result.get(key) for key in ('exitCode', 'reaped', 'elapsedSeconds')}
    if result.get('exitCode') not in (0, -2, 130, -9):
        summary['stderrTail'] = result.get('stderr', '')[-4096:]
    return summary


def project_state(case_id: str, observed: dict) -> dict:
    if case_id == 'concurrent.same-npm-install':
        return {key: observed.get(key) for key in (
            'commandOutcomeClasses', 'allProcessesReaped', 'boundedSeconds', 'settingsValid',
            'sourceConfigured', 'sourceOccurrences', 'packageRootPresent', 'installedVersion',
            'packageManifestValid', 'expectedVersion', 'updateArtifacts')}
    if case_id == 'concurrent.same-git-install':
        return {key: observed.get(key) for key in (
            'allProcessesReaped', 'boundedSeconds', 'settingsValid', 'sourceConfigured',
            'sourceOccurrences', 'packageRootPresent', 'installedVersion', 'packageManifestValid')}
    if case_id == 'concurrent.install-remove':
        return {key: observed.get(key) for key in (
            'allProcessesReaped', 'boundedSeconds', 'settingsValid', 'terminalStateCoherent',
            'sourceOccurrenceBound', 'packageIntegrity')}
    if case_id == 'concurrent.update-remove':
        return {key: observed.get(key) for key in (
            'allProcessesReaped', 'boundedSeconds', 'settingsValid', 'serializationCoherent',
            'updateArtifactsClean')}
    return {key: value for key, value in observed.items() if key != 'diagnostics'}


def clean_npm_state(item: dict, source: str, name: str, results: list[dict], version: str | None) -> tuple[dict, bool]:
    observed = state(item, source, name, results, expected_version=version)
    terminal = (observed['allProcessesReaped'] and observed['boundedSeconds']
                and observed['settingsValid'] and observed['sourceOccurrences'] <= 1
                and observed['packageManifestValid'] and observed['installedVersion'] == version)
    return observed, terminal


def install_pair(profile: Path, case_id: str, source: str, name: str, pi: Path, pisharp: Path,
                 *, registry=None, package_dir=None) -> dict[str, dict]:
    products = {product: updates.make_product(profile / case_id, product) for product in ('pi', 'pisharp')}
    for item in products.values():
        if registry is not None:
            item['env']['npm_config_registry'] = registry.url
    return products


def pack_env(root: Path) -> dict:
    home = root / 'pack-home'
    home.mkdir(parents=True, exist_ok=True)
    return {'PATH': os.environ.get('PATH', ''), 'HOME': str(home), 'TMPDIR': str(root),
            'npm_config_cache': str(root / 'pack-cache'), 'npm_config_offline': 'true'}


def pack(root: Path, name: str, version: str, env: dict, manifest: dict | None = None,
         files: dict[str, str] | None = None) -> Path:
    failure = load_module(Path(__file__).with_name('managed-package-failure-process-differential.py'),
                          'concurrency_failure_helper')
    return failure.pack_package(root, name, version, manifest or {}, files or {}, env)


def install_before_update(product: str, pi: Path, pisharp: Path, item: dict,
                          source: str, registry) -> dict:
    item['env']['npm_config_registry'] = registry.url
    result = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], ['install', source])
    if result['exitCode'] != 0:
        raise RuntimeError(f'{product} setup install failed: {result["stderr"]}')
    settings = cli.read_settings(item['agent'] / 'settings.json')
    return {'exitCode': result['exitCode'], 'settings': settings}


def wait_registry(registry, capture: Capture, timeout: float = 60) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline and capture.process.poll() is None:
        if registry.tarball_requested.is_set():
            return True
        time.sleep(0.02)
    return registry.tarball_requested.is_set()


def case_same_npm(profile: Path, scratch: Path, pi: Path, pisharp: Path, env: dict) -> tuple[dict, bool]:
    name, version = 'concurrent-same-npm', '1.0.0'
    tarball = pack(scratch / 'same-npm', name, version, env,
                   {'scripts': {'postinstall': 'node -e "setTimeout(()=>{},350)"'}})
    source = f'npm:{name}@file:{tarball}'
    results = {}
    passed = True
    for product in ('pi', 'pisharp'):
        item = updates.make_product(profile / 'same-npm' / product, product)
        commands = [['install', source], ['install', source]]
        command_results = parallel_pair(product, pi, pisharp, item, commands,
                                        profile / 'gates/same-npm.py')
        observed, product_pass = clean_npm_state(item, source, name, command_results, version)
        observed['npmTargetInstallRace'] = npm_target_install_race(command_results)
        product_pass = product_pass and observed['sourceConfigured'] and observed['sourceOccurrences'] == 1
        results[product] = observed
        passed = passed and product_pass
    return results, passed


def case_same_git(profile: Path, scratch: Path, pi: Path, pisharp: Path) -> tuple[dict, bool]:
    setup = git_installs.create_product(profile / 'git-fixture-profile', scratch / 'git-fixture-env', 'pi')
    fixture = resource.create_managed_git_repository(scratch / 'git-source', pi, setup['env'])
    fixture['daemon'] = cli.register_daemon(fixture['daemon'])
    source = fixture['unpinned_source']
    results, passed = {}, True
    try:
        for product in ('pi', 'pisharp'):
            item = updates.make_product(profile / 'same-git' / product, product)
            command_results = parallel_pair(product, pi, pisharp, item,
                [['install', source], ['install', source]], profile / 'gates/same-git.py')
            observed = state(item, source, 'managed-git-package', command_results,
                             expected_version=fixture['v1_head'], package_kind='git')
            observed['originHeadCloneRace'] = origin_head_clone_race(command_results)
            observed['cloneDestinationCollision'] = clone_destination_collision(command_results)
            if observed['cloneDestinationCollision']:
                observed['cloneCollisionErrors'] = [result['stderr'][-4096:] for result in command_results
                                                    if result['exitCode'] != 0]
            product_pass = (observed['allProcessesReaped'] and observed['boundedSeconds']
                            and observed['settingsValid'] and observed['sourceConfigured']
                            and observed['sourceOccurrences'] == 1 and observed['packageRootPresent']
                            and 'success' in observed['commandOutcomeClasses']
                            and observed['installedVersion'] == fixture['v1_head']
                            and observed['packageManifestValid'])
            results[product] = observed
            passed = passed and product_pass
    finally:
        cli.stop_daemon(fixture['daemon'])
    return results, passed


def case_install_remove(profile: Path, scratch: Path, pi: Path, pisharp: Path,
                        registry_factory, env: dict) -> tuple[dict, bool]:
    name = 'concurrent-install-remove'
    tarball = package_cli = cli.pack_registry_package(scratch / 'install-remove', name, '1.0.0', env)
    registry = registry_factory(name, {'1.0.0': tarball}, '1.0.0', f'{name}-1.0.0.tgz')
    source = f'npm:{name}@1.0.0'
    results, passed = {}, True
    try:
        for product in ('pi', 'pisharp'):
            item = updates.make_product(profile / 'install-remove' / product, product)
            item['env']['npm_config_registry'] = registry.url
            install_capture = start_cli(product, pi, pisharp, item, ['install', source])
            started = wait_registry(registry, install_capture)
            remove_capture = start_cli(product, pi, pisharp, item, ['remove', source])
            time.sleep(0.25)
            registry.release_tarball.set()
            install_result = install_capture.finish()
            remove_result = remove_capture.finish()
            outcomes = [install_result, remove_result]
            observed = state(item, source, name, outcomes, expected_version='1.0.0')
            terminal_state_coherent = (
                observed['sourceConfigured'] and observed['sourceOccurrences'] == 1
                and observed['packageManifestValid'] and observed['installedVersion'] == '1.0.0'
            ) or (not observed['sourceConfigured'] and observed['sourceOccurrences'] == 0
                  and not observed['packageRootPresent'])
            observed.update({
                'terminalStateCoherent': terminal_state_coherent,
                'sourceOccurrenceBound': observed['sourceOccurrences'] <= 1,
                'packageIntegrity': (observed['packageManifestValid'] and observed['installedVersion'] == '1.0.0')
                    or not observed['packageRootPresent'],
                'allProcessesReaped': install_result['reaped'] and remove_result['reaped'],
                'boundedSeconds': install_result['elapsedSeconds'] < 120 and remove_result['elapsedSeconds'] < 120,
                'removeExitCode': remove_result['exitCode'],
                'installStartedBeforeRemove': started,
            })
            product_pass = (started and observed['allProcessesReaped'] and observed['boundedSeconds']
                            and observed['terminalStateCoherent'] and observed['sourceOccurrenceBound']
                            and observed['packageIntegrity'])
            results[product] = observed
            passed = passed and product_pass
            registry.release_tarball.clear()
    finally:
        registry.close()
    return results, passed


def case_update_remove(profile: Path, scratch: Path, pi: Path, pisharp: Path,
                       registry_factory, env: dict) -> tuple[dict, bool]:
    name = 'concurrent-update-remove'
    versions = {version: cli.pack_registry_package(scratch / 'update-remove', name, version, env)
                for version in ('1.0.0', '1.1.0')}
    registry = registry_factory(name, versions, '1.0.0', f'{name}-1.1.0.tgz')
    source = f'npm:{name}@^1.0.0'
    results, passed = {}, True
    try:
        for product in ('pi', 'pisharp'):
            item = updates.make_product(profile / 'update-remove' / product, product)
            registry.set_versions(name, {'1.0.0': versions['1.0.0']}, {'latest': '1.0.0'})
            install_before_update(product, pi, pisharp, item, source, registry)
            registry.set_versions(name, versions, {'latest': '1.1.0'})
            update_capture = start_cli(product, pi, pisharp, item, ['update', '--extensions'])
            started = wait_registry(registry, update_capture)
            remove_capture = start_cli(product, pi, pisharp, item, ['remove', source])
            time.sleep(0.25)
            registry.release_tarball.set()
            update_result = update_capture.finish()
            remove_result = remove_capture.finish()
            remove_elapsed = remove_result['elapsedSeconds']
            configured = source in cli.package_sources(cli.read_settings(item['agent'] / 'settings.json'))
            manifest = item['agent'] / 'npm/node_modules' / name / 'package.json'
            try:
                installed = json.loads(manifest.read_text(encoding='utf-8')).get('version') if manifest.is_file() else None
            except json.JSONDecodeError:
                installed = 'corrupt'
            coherent = ((configured and installed in ('1.0.0', '1.1.0')) or (not configured and installed is None))
            observed = {
                'updateExitCode': update_result['exitCode'], 'removeExitCode': remove_result['exitCode'],
                'updateStartedBeforeRemove': started, 'sourceConfigured': configured,
                'installedVersion': installed, 'settingsValid': True,
                'allProcessesReaped': update_result['reaped'],
                'boundedSeconds': update_result['elapsedSeconds'] < 120 and remove_elapsed < 120,
                'serializationCoherent': coherent,
                'updateArtifacts': sorted(path.name for path in item['agent'].glob('npm.pisharp-update-*')),
                'updateArtifactsClean': not list(item['agent'].glob('npm.pisharp-update-*')),
                'diagnostics': {'update': command_summary(update_result), 'remove': command_summary(remove_result)},
            }
            observed['allProcessesReaped'] = update_result['reaped'] and remove_result['reaped']
            product_pass = (started and observed['allProcessesReaped'] and observed['boundedSeconds']
                            and coherent and installed != 'corrupt' and observed['updateArtifactsClean'])
            results[product] = observed
            passed = passed and product_pass
            registry.release_tarball.clear()
    finally:
        registry.close()
    return results, passed


def prepare_update_registry(scratch: Path, name: str, env: dict, registry_factory, *, blocked: bool = True):
    versions = {version: cli.pack_registry_package(scratch / name, name, version, env)
                for version in ('1.0.0', '1.1.0')}
    registry = registry_factory(name, versions, '1.0.0', f'{name}-1.1.0.tgz' if blocked else None)
    return versions, registry, f'npm:{name}@^1.0.0'


def case_two_updates(profile: Path, scratch: Path, pi: Path, pisharp: Path,
                     registry_factory, env: dict) -> tuple[dict, bool]:
    name = 'concurrent-two-updates'
    versions, registry, source = prepare_update_registry(scratch, name, env, registry_factory, blocked=False)
    results, passed = {}, True
    try:
        for product in ('pi', 'pisharp'):
            item = updates.make_product(profile / 'two-updates' / product, product)
            registry.set_versions(name, {'1.0.0': versions['1.0.0']}, {'latest': '1.0.0'})
            install_before_update(product, pi, pisharp, item, source, registry)
            registry.set_versions(name, versions, {'latest': '1.1.0'})
            command_results = parallel_pair(product, pi, pisharp, item,
                [['update', '--extensions'], ['update', '--extensions']], profile / 'gates/two-updates.py')
            observed, product_pass = clean_npm_state(item, source, name, command_results, '1.1.0')
            product_pass = product_pass and observed['sourceConfigured'] and observed['sourceOccurrences'] == 1
            results[product] = observed
            passed = passed and product_pass
            registry.release_tarball.clear()
    finally:
        registry.close()
    return results, passed


def case_update_discovery(profile: Path, scratch: Path, pi: Path, pisharp: Path,
                          registry_factory, env: dict) -> tuple[dict, bool]:
    name = 'concurrent-update-discovery'
    versions, registry, source = prepare_update_registry(scratch, name, env, registry_factory)
    results, passed = {}, True
    try:
        for product in ('pi', 'pisharp'):
            item = updates.make_product(profile / 'update-discovery' / product, product)
            registry.set_versions(name, {'1.0.0': versions['1.0.0']}, {'latest': '1.0.0'})
            install_before_update(product, pi, pisharp, item, source, registry)
            registry.set_versions(name, versions, {'latest': '1.1.0'})
            update_capture = start_cli(product, pi, pisharp, item, ['update', '--extensions'])
            started = wait_registry(registry, update_capture)
            list_capture = start_cli(product, pi, pisharp, item, ['list'])
            time.sleep(0.25)
            during = item['agent'] / 'npm/node_modules' / name / 'package.json'
            try:
                during_version = json.loads(during.read_text(encoding='utf-8')).get('version') if during.is_file() else None
            except json.JSONDecodeError:
                during_version = 'corrupt'
            registry.release_tarball.set()
            update_result = update_capture.finish()
            list_result = list_capture.finish()
            final_manifest = item['agent'] / 'npm/node_modules' / name / 'package.json'
            try:
                final_version = json.loads(final_manifest.read_text(encoding='utf-8')).get('version') if final_manifest.is_file() else None
            except json.JSONDecodeError:
                final_version = 'corrupt'
            observed = {
                'listExitCode': list_result['exitCode'], 'updateExitCode': update_result['exitCode'],
                'updateStartedBeforeDiscovery': started, 'versionDuringDiscovery': during_version,
                'finalVersion': final_version,
                'sourceConfigured': source in cli.package_sources(cli.read_settings(item['agent'] / 'settings.json')),
                'processReaped': update_result['reaped'], 'boundedSeconds': update_result['elapsedSeconds'] < 120,
                'diagnostics': {'update': command_summary(update_result), 'list': command_summary(list_result)},
            }
            product_pass = (started and observed['listExitCode'] == 0 and observed['updateExitCode'] == 0
                            and observed['processReaped'] and observed['boundedSeconds']
                            and during_version in ('1.0.0', None) and final_version == '1.1.0'
                            and observed['sourceConfigured'])
            results[product] = observed
            passed = passed and product_pass
            registry.release_tarball.clear()
    finally:
        registry.close()
    return results, passed


def case_cancelled_lock(profile: Path, scratch: Path, pi: Path, pisharp: Path,
                        registry_factory, env: dict) -> tuple[dict, bool]:
    name = 'concurrent-cancelled-lock'
    tarball = cli.pack_registry_package(scratch / 'cancelled-lock', name, '1.0.0', env)
    registry = registry_factory(name, {'1.0.0': tarball}, '1.0.0', f'{name}-1.0.0.tgz')
    source = f'npm:{name}@1.0.0'
    results, passed = {}, True
    try:
        for product in ('pi', 'pisharp'):
            item = updates.make_product(profile / 'cancelled-lock' / product, product)
            item['env']['npm_config_registry'] = registry.url
            first = start_cli(product, pi, pisharp, item, ['install', source])
            started = wait_registry(registry, first)
            second = start_cli(product, pi, pisharp, item, ['install', source])
            time.sleep(0.4)
            waiter_alive = second.process.poll() is None
            second_result = second.stop(signal.SIGINT) if waiter_alive else second.finish()
            registry.release_tarball.set()
            first_result = first.finish()
            observed, product_pass = clean_npm_state(item, source, name, [first_result, second_result], '1.0.0')
            product_pass = (started and waiter_alive and first_result['exitCode'] == 0
                            and second_result['exitCode'] != 0 and product_pass
                            and observed['sourceConfigured'] and observed['sourceOccurrences'] == 1)
            observed['waiterCancelled'] = second_result['exitCode'] != 0
            observed['diagnostics'] = {'firstInstall': command_summary(first_result),
                                       'cancelledWaiter': command_summary(second_result)}
            results[product] = observed
            passed = passed and product_pass
            registry.release_tarball.clear()
    finally:
        registry.close()
    return results, passed


def case_crashed_writer(profile: Path, scratch: Path, pi: Path, pisharp: Path,
                        registry_factory, env: dict) -> tuple[dict, bool]:
    name = 'concurrent-crashed-writer'
    versions, registry, source = prepare_update_registry(scratch, name, env, registry_factory)
    results, passed = {}, True
    try:
        for product in ('pi', 'pisharp'):
            item = updates.make_product(profile / 'crashed-writer' / product, product)
            registry.set_versions(name, {'1.0.0': versions['1.0.0']}, {'latest': '1.0.0'})
            install_before_update(product, pi, pisharp, item, source, registry)
            registry.set_versions(name, versions, {'latest': '1.1.0'})
            writer = start_cli(product, pi, pisharp, item, ['update', '--extensions'])
            started = wait_registry(registry, writer)
            successor = start_cli(product, pi, pisharp, item, ['update', '--extensions'])
            time.sleep(0.4)
            successor_alive = successor.process.poll() is None
            killed = writer.stop(signal.SIGKILL)
            registry.release_tarball.set()
            successor_result = successor.finish()
            observed, product_pass = clean_npm_state(item, source, name, [killed, successor_result], '1.1.0')
            product_pass = (started and successor_alive and successor_result['exitCode'] == 0
                            and product_pass and observed['sourceConfigured']
                            and observed['sourceOccurrences'] == 1 and not observed['updateArtifacts'])
            observed['writerKilled'] = killed['exitCode'] != 0
            observed['successorWaited'] = successor_alive
            observed['diagnostics'] = {'killedWriter': command_summary(killed),
                                       'successorUpdate': command_summary(successor_result)}
            results[product] = observed
            passed = passed and product_pass
            registry.release_tarball.clear()
    finally:
        registry.close()
    return results, passed


def case_external_settings(profile: Path, scratch: Path, pi: Path, pisharp: Path,
                           env: dict) -> tuple[dict, bool]:
    name = 'concurrent-external-settings'
    script = ("const fs=require('node:fs'); const p=process.env.PI_CODING_AGENT_DIR || "
              "process.env.PISHARP_AGENT_DIR; const f=p+'/settings.json'; "
              "const s=JSON.parse(fs.readFileSync(f,'utf8')); "
              "s.configurationProbe='written-during-install'; "
              "fs.writeFileSync(f,JSON.stringify(s,null,2)+'\\n');")
    tarball = pack(scratch / 'external-settings', name, '1.0.0', env,
                   {'scripts': {'postinstall': 'node mutate.cjs'}}, {'mutate.cjs': script})
    source = f'npm:{name}@file:{tarball}'
    results, passed = {}, True
    for product in ('pi', 'pisharp'):
        item = updates.make_product(profile / 'external-settings' / product, product)
        command = cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], ['install', source])
        settings = cli.read_settings(item['agent'] / 'settings.json')
        manifest = item['agent'] / 'npm/node_modules' / name / 'package.json'
        version = json.loads(manifest.read_text(encoding='utf-8')).get('version') if manifest.is_file() else None
        observed = {
            'installExitCode': command['exitCode'],
            'externalChangePreserved': settings.get('configurationProbe') == 'written-during-install',
            'sourceConfigured': source in cli.package_sources(settings),
            'sourceOccurrences': cli.package_sources(settings).count(source),
            'installedVersion': version,
            'settingsValid': True,
            'diagnostics': command_summary(command),
        }
        product_pass = (observed['installExitCode'] == 0 and observed['externalChangePreserved']
                        and observed['sourceConfigured'] and observed['sourceOccurrences'] == 1
                        and version == '1.0.0')
        results[product] = observed
        passed = passed and product_pass
    return results, passed


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
    scratch = Path(tempfile.mkdtemp(prefix='managed-package-concurrency-', dir=args.profile))
    pack_root = scratch / 'packed'
    pack_root.mkdir()
    environment = pack_env(scratch)

    def registry_factory(name, versions, tag, blocked):
        return cli.LocalNpmRegistry({name: {'versions': versions, 'tags': {'latest': tag}}},
                                    blocked_tarball=blocked, preserve_blocked_tarball=True)

    case_functions = {
        'concurrent.same-npm-install': lambda: case_same_npm(args.profile, scratch, args.pi, args.pisharp, environment),
        'concurrent.same-git-install': lambda: case_same_git(args.profile, scratch, args.pi, args.pisharp),
        'concurrent.install-remove': lambda: case_install_remove(args.profile, scratch, args.pi, args.pisharp,
                                                                  registry_factory, environment),
        'concurrent.update-remove': lambda: case_update_remove(args.profile, scratch, args.pi, args.pisharp,
                                                               registry_factory, environment),
        'concurrent.two-process-update': lambda: case_two_updates(args.profile, scratch, args.pi, args.pisharp,
                                                                  registry_factory, environment),
        'concurrent.update-discovery': lambda: case_update_discovery(args.profile, scratch, args.pi, args.pisharp,
                                                                     registry_factory, environment),
        'concurrent.cancelled-lock': lambda: case_cancelled_lock(args.profile, scratch, args.pi, args.pisharp,
                                                                 registry_factory, environment),
        'concurrent.crashed-writer': lambda: case_crashed_writer(args.profile, scratch, args.pi, args.pisharp,
                                                                 registry_factory, environment),
        'concurrent.external-settings-change': lambda: case_external_settings(args.profile, scratch,
                                                                               args.pi, args.pisharp, environment),
    }
    cases = {}
    try:
        for case_id, run_case in case_functions.items():
            outcomes, satisfied = run_case()
            pi_observed, pisharp_observed = outcomes['pi'], outcomes['pisharp']
            oracle, actual = project_state(case_id, pi_observed), project_state(case_id, pisharp_observed)
            equal = oracle == actual
            classification = 'strict' if equal else 'mismatch'
            passed = satisfied and equal
            if (case_id == 'concurrent.same-npm-install'
                    and is_same_npm_install_safety_difference(pi_observed, pisharp_observed,
                                                              oracle, actual)):
                classification, passed = 'intentional-safety-difference', True
            if (case_id == 'concurrent.same-git-install'
                    and is_same_git_install_safety_difference(pi_observed, pisharp_observed, oracle, actual)):
                classification, passed = 'intentional-safety-difference', True
            if case_id == 'concurrent.update-discovery':
                pi_state_safe = (pi_observed.get('listExitCode') == 0
                                 and pi_observed.get('updateExitCode') == 0
                                 and pi_observed.get('versionDuringDiscovery') is None
                                 and pi_observed.get('finalVersion') == '1.1.0'
                                 and pi_observed.get('sourceConfigured') is True)
                pisharp_safe = (pisharp_observed.get('listExitCode') == 0
                                and pisharp_observed.get('updateExitCode') == 0
                                and pisharp_observed.get('versionDuringDiscovery') == '1.0.0'
                                and pisharp_observed.get('finalVersion') == '1.1.0'
                                and pisharp_observed.get('sourceConfigured') is True)
                if pi_state_safe and pisharp_safe:
                    classification, passed = 'intentional-safety-difference', True
            cases[case_id] = {'oracle': oracle, 'actual': actual, 'equal': equal,
                              'contractSatisfied': passed, 'classification': classification,
                              'observations': {'pi': pi_observed, 'pisharp': pisharp_observed}}
    finally:
        cli.cleanup_daemons()
        shutil.rmtree(scratch, ignore_errors=True)

    source_sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    report = {'piSha': pi_sha, 'pisharpSourceSha': source_sha,
              'fixtureVersion': 'managed-package-concurrency-v1',
              'scenario': 'barrier-controlled-managed-package-processes',
              'cases': cases, 'match': set(cases) == set(REQUIRED_CASE_IDS)
              and all(case['contractSatisfied'] for case in cases.values())}
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    print(json.dumps({'cases': len(cases), 'match': report['match'], 'output': str(args.output)}))
    return 0 if report['match'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
