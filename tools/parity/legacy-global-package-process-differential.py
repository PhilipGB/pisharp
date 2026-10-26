#!/usr/bin/env python3
"""Compare pinned Pi and PiSharp read-only legacy global package resolution."""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/parity'))
package_cli_path = Path(__file__).with_name('package-cli-process-differential.py')
package_cli_spec = importlib.util.spec_from_file_location('package_cli_process', package_cli_path)
package_cli = importlib.util.module_from_spec(package_cli_spec)
package_cli_spec.loader.exec_module(package_cli)

CASES = {
    'legacy.npm': ('npm', 'legacy-npm', 'global-npm'),
    'legacy.pnpm': ('pnpm', 'legacy-pnpm', 'global-pnpm'),
    'legacy.yarn': ('yarn', 'legacy-yarn', 'global-yarn'),
    'legacy.bun': ('bun', 'legacy-bun', 'global-bun'),
    'legacy.missing': ('npm', 'legacy-missing', 'missing-global'),
    'legacy.malformed': ('pnpm', 'legacy-malformed', 'malformed-pnpm'),
    'legacy.managed-precedence': ('npm', 'legacy-precedence', 'managed-precedence'),
    'legacy.read-only': ('npm', 'legacy-read-only', 'read-only-removal'),
}

SHIM = r'''#!/bin/sh
printf '%s\0' "$@" >> "$PISHARP_LEGACY_ARGV_LOG"
printf '\n' >> "$PISHARP_LEGACY_ARGV_LOG"
if [ "$1" = "list" ] && [ "$2" = "-g" ]; then
    printf '%s\n' "$PISHARP_LEGACY_PNPM_LIST"
    exit "${PISHARP_LEGACY_PNPM_EXIT:-0}"
fi
if [ "$1" = "pm" ] && [ "$2" = "bin" ] && [ "$3" = "-g" ]; then
    printf '%s\n' "$PISHARP_LEGACY_BUN_BIN"
    exit 0
fi
if [ "$1" = "root" ] && [ "$2" = "-g" ]; then
    printf '%s\n' "$PISHARP_LEGACY_GLOBAL_ROOT"
    exit 0
fi
if [ "$1" = "install" ]; then
    install_root=
    while [ $# -gt 0 ]; do
        case "$1" in
            --prefix|--cwd) shift; install_root=$1 ;;
        esac
        shift
    done
    if [ -z "$install_root" ]; then exit 39; fi
    package="$install_root/node_modules/$PISHARP_LEGACY_PACKAGE_NAME"
    mkdir -p "$package"
    printf '{"name":"%s","version":"1.0.0"}\n' "$PISHARP_LEGACY_PACKAGE_NAME" > "$package/package.json"
    exit 0
fi
exit 1
'''


def manager_paths(product_root: Path, manager: str, package_name: str, mode: str):
    if manager == 'pnpm':
        global_root = product_root / 'global' / 'pnpm-root'
        package_root = product_root / 'global' / 'pnpm' / '20-fixture' / 'node_modules' / package_name
        bin_root = None
    elif manager == 'bun':
        bin_root = product_root / 'global' / 'bun' / '.bun' / 'bin'
        global_root = bin_root.parent / 'install' / 'global' / 'node_modules'
        package_root = global_root / package_name
    else:
        global_root = product_root / 'global' / manager / 'node_modules'
        package_root = global_root / package_name
        bin_root = None
    if mode == 'malformed-pnpm':
        package_root = global_root / package_name
    return global_root, package_root, bin_root


def write_package(path: Path, package_name: str):
    path.mkdir(parents=True, exist_ok=True)
    (path / 'package.json').write_text(json.dumps({'name': package_name, 'version': '1.0.0'}) + '\n')
    (path / 'fixture.txt').write_text('legacy global package fixture\n')


def parse_manager_calls(path: Path, product_root: Path, agent: Path):
    if not path.is_file():
        return []
    calls = []
    for line in path.read_bytes().decode(errors='replace').splitlines():
        argv = [part for part in line.split('\0') if part]
        calls.append([part.replace(str(product_root), '<product-root>').replace(str(agent), '<agent>')
                      for part in argv])
    return calls


def package_path_from_list(stdout: str, source: str, product_root: Path):
    lines = [package_cli.strip_ansi(line).rstrip() for line in stdout.splitlines()]
    for index, line in enumerate(lines):
        if line.strip() != source:
            continue
        if index + 1 >= len(lines):
            return None
        candidate = lines[index + 1].strip()
        if not candidate:
            return None
        try:
            return Path(candidate).resolve().relative_to(product_root.resolve()).as_posix()
        except (OSError, ValueError):
            return None
    return None


def state_for(product: str, product_root: Path, agent: Path, settings_path: Path,
              package_name: str, source: str, manager: str, manager_log: Path,
              global_root: Path, global_package: Path, global_before, output: dict):
    settings = package_cli.read_settings(settings_path)
    managed_package = agent / 'npm' / 'node_modules' / package_name
    selected_path = package_path_from_list(output.get('stdout', ''), source, product_root)
    after_digest = package_cli.file_tree_digest(global_root.parent if global_root.parent.exists() else global_root)
    return {
        'exitCode': output.get('exitCode', 1),
        'manager': manager,
        'packageManagerConfig': settings.get('npmCommand'),
        'configuredSources': package_cli.package_sources(settings),
        'selectedPath': selected_path,
        'managerCalls': parse_manager_calls(manager_log, product_root, agent),
        'globalPackageExists': global_package.is_dir(),
        'globalPackageDigestBefore': global_before,
        'globalPackageDigestAfter': after_digest,
        'managedPackageExists': managed_package.is_dir(),
        'managedPackageDigest': package_cli.file_tree_digest(managed_package),
        'stdoutStatus': [line.strip() for line in package_cli.strip_ansi(output.get('stdout', '')).splitlines()
                         if line.strip().startswith(('Installed ', 'Removed '))],
    }


def create_product(profile: Path, shims: Path, case_id: str, product: str,
                   manager: str, package_name: str, mode: str):
    product_root = profile / case_id / product
    cwd = product_root / 'work'
    agent = product_root / 'agent'
    markers = product_root / 'markers'
    trace = product_root / 'extension.log'
    cwd.mkdir(parents=True)
    (cwd / '.pi').mkdir()
    agent.mkdir()
    global_root, global_package, bun_bin = manager_paths(product_root, manager, package_name, mode)
    global_root.mkdir(parents=True, exist_ok=True)
    managed_root = agent / 'npm' / 'node_modules' / package_name
    seed_global = mode not in ('missing-global',)
    if seed_global:
        write_package(global_package, package_name)
    if mode == 'managed-precedence':
        write_package(managed_root, package_name)
    if bun_bin is not None:
        bun_bin.mkdir(parents=True, exist_ok=True)

    npm_command = None if manager == 'npm' else [manager]
    initial_settings = {'packages': [f'npm:{package_name}']}
    if npm_command is not None:
        initial_settings['npmCommand'] = npm_command
    settings_path = agent / 'settings.json'
    settings_path.write_text(json.dumps(initial_settings, indent=2) + '\n')
    (cwd / '.pi' / 'settings.json').write_text('{}\n')
    env = package_cli.base_environment(profile / case_id, product, agent, cwd, markers, trace)
    env['PATH'] = str(shims) + os.pathsep + env.get('PATH', '')
    manager_log = product_root / 'manager-argv.log'
    env.update({
        'PISHARP_LEGACY_ARGV_LOG': str(manager_log),
        'PISHARP_LEGACY_GLOBAL_ROOT': str(global_root),
        'PISHARP_LEGACY_PACKAGE_NAME': package_name,
        'PISHARP_LEGACY_BUN_BIN': str(bun_bin or ''),
        'PISHARP_LEGACY_PNPM_LIST': '[]',
        'PISHARP_LEGACY_PNPM_EXIT': '0',
    })
    if manager == 'pnpm' and mode == 'global-pnpm':
        env['PISHARP_LEGACY_PNPM_LIST'] = json.dumps([{
            'path': str(global_root),
            'dependencies': {package_name: {'version': '1.0.0', 'path': str(global_package)}},
        }])
    elif manager == 'pnpm' and mode == 'malformed-pnpm':
        env['PISHARP_LEGACY_PNPM_LIST'] = 'not-json'
    digest_root = global_root.parent if global_root.parent.exists() else global_root
    global_before = package_cli.file_tree_digest(digest_root)
    return {
        'productRoot': product_root, 'cwd': cwd, 'agent': agent, 'env': env,
        'settingsPath': settings_path, 'managerLog': manager_log,
        'globalRoot': global_root, 'globalPackage': global_package,
        'globalBefore': global_before, 'packageName': package_name,
        'source': f'npm:{package_name}', 'manager': manager, 'mode': mode,
    }


def run_case(profile: Path, shims: Path, pi: Path, pisharp: Path, case_id: str):
    manager, package_name, mode = CASES[case_id]
    products = {product: create_product(profile, shims, case_id, product, manager, package_name, mode)
                for product in ('pi', 'pisharp')}
    command = ['list']
    if case_id == 'legacy.read-only':
        command = ['remove', products['pi']['source']]
    results = {}
    diagnostics = {}
    for product, item in products.items():
        completed = package_cli.run_cli(product, pi, pisharp, item['cwd'], item['env'], command)
        diagnostics[product] = completed
        results[product] = state_for(product, item['productRoot'], item['agent'], item['settingsPath'],
                                     package_name, item['source'], manager, item['managerLog'],
                                     item['globalRoot'], item['globalPackage'], item['globalBefore'], completed)
    pi_state, actual_state = results['pi'], results['pisharp']
    assertions = {
        'statesMatch': pi_state == actual_state,
        'commandSucceeded': pi_state['exitCode'] == 0 and actual_state['exitCode'] == 0,
        'managerConfigurationMatches': pi_state['packageManagerConfig'] == actual_state['packageManagerConfig'],
        'globalTreeUnchanged': (pi_state['globalPackageDigestBefore'] == pi_state['globalPackageDigestAfter']
                                and actual_state['globalPackageDigestBefore'] == actual_state['globalPackageDigestAfter']),
    }
    if case_id in ('legacy.npm', 'legacy.pnpm', 'legacy.yarn', 'legacy.bun'):
        assertions['legacyPathResolved'] = pi_state['selectedPath'] is not None
    elif case_id == 'legacy.missing':
        expected_calls = [['root', '-g'], ['install', package_name, '--prefix', '<product-root>/agent/npm',
                           '--legacy-peer-deps']]
        assertions['missingGlobalFallsBackToManaged'] = (
            pi_state['selectedPath'] == 'agent/npm/node_modules/' + package_name
            and actual_state['selectedPath'] == 'agent/npm/node_modules/' + package_name
            and pi_state['managedPackageExists'] and actual_state['managedPackageExists']
            and pi_state['managerCalls'] == expected_calls and actual_state['managerCalls'] == expected_calls)
    elif case_id == 'legacy.malformed':
        expected_calls = [['list', '-g', '--depth', '0', '--json'],
                          ['install', package_name, '--prefix', '<product-root>/agent/npm',
                           '--config.auto-install-peers=false', '--config.strict-peer-dependencies=false',
                           '--config.strict-dep-builds=false']]
        assertions['malformedLookupFallsBackToManaged'] = (
            pi_state['selectedPath'] == 'agent/npm/node_modules/' + package_name
            and actual_state['selectedPath'] == 'agent/npm/node_modules/' + package_name
            and pi_state['managedPackageExists'] and actual_state['managedPackageExists']
            and pi_state['globalPackageExists'] and actual_state['globalPackageExists']
            and pi_state['managerCalls'] == expected_calls and actual_state['managerCalls'] == expected_calls)
    elif case_id == 'legacy.managed-precedence':
        assertions['managedPathWinsWithoutGlobalProbe'] = (
            pi_state['selectedPath'] == 'agent/npm/node_modules/' + package_name
            and actual_state['selectedPath'] == 'agent/npm/node_modules/' + package_name
            and pi_state['managerCalls'] == [] and actual_state['managerCalls'] == [])
    elif case_id == 'legacy.read-only':
        assertions['removalLeavesLegacyPackageUntouched'] = (
            pi_state['globalPackageExists'] and actual_state['globalPackageExists']
            and pi_state['configuredSources'] == [] and actual_state['configuredSources'] == []
            and pi_state['managerCalls'] == [['root', '-g']]
            and actual_state['managerCalls'] == [['root', '-g']])
    else:
        assertions[case_id] = False
    passed = all(assertions.values())
    return {
        'expected': {'manager': manager, 'packageName': package_name, 'mode': mode},
        'oracle': pi_state, 'actual': actual_state,
        'classification': 'strict' if passed else 'mismatch',
        'passed': passed, 'assertions': assertions,
        'diagnostics': diagnostics,
        'fixtureFamilies': ['disposable-global-roots', 'package-manager-shims'],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.profile = args.profile.resolve()
    args.output = args.output.resolve()
    args.profile.mkdir(parents=True, exist_ok=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    pi_sha = package_cli.verify_pi_checkout(args.pi.resolve())
    shims = args.profile / 'manager-bin'
    shims.mkdir(parents=True, exist_ok=True)
    for manager in ('npm', 'pnpm', 'yarn', 'bun'):
        shim = shims / manager
        shim.write_text(SHIM, encoding='utf-8')
        shim.chmod(0o755)

    results = {case_id: run_case(args.profile, shims, args.pi.resolve(), args.pisharp.resolve(), case_id)
               for case_id in CASES}
    evidence = {
        'piSha': pi_sha,
        'pisharpSha': package_cli.subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        'scenario': 'legacy-global-package-resolution',
        'fixtureFamilies': ['disposable-global-roots', 'package-manager-shims'],
        'cases': results,
        'match': all(result['passed'] for result in results.values()),
        'limitations': ['Manager commands are isolated shims; global package contents are disposable local fixtures.'],
    }
    args.output.write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')
    print(f"Recorded {len(results)} legacy global package resolution cases: {args.output}")
    return 0 if evidence['match'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
