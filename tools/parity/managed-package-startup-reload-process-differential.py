#!/usr/bin/env python3
"""Compare pinned Pi and PiSharp resource state across package reload transitions."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/parity'))
from pi_reference import verify_pi_checkout

# The verifier records these two cases from the paired managed-npm report produced in the same run.
EXTERNAL_REPORT_CASES = ('reload.npm', 'reload.no-duplicate-registration')

SCENARIOS = {
    'reload.local': ('local', lambda before, after: before == after and names(before) == ['reload-resource']
                     and 'Initial managed package resource.' in before.get('invocation', '')),
    'reload.git': ('git', lambda before, after: before == after and names(before) == ['reload-resource']
                   and 'Initial managed package resource.' in before.get('invocation', '')),
    'reload.updated-resource': ('updated-resource', lambda before, after:
        names(before) == ['reload-resource'] and names(after) == ['reload-resource-v2']
        and 'Initial managed package resource.' in before.get('invocation', '')
        and 'Updated managed package resource.' in after.get('invocation', '')),
    'reload.removed-resource': ('removed-resource', lambda before, after:
        names(before) == ['reload-resource'] and names(after) == []
        and 'Initial managed package resource.' in before.get('invocation', '')
        and after.get('invocation') == '/skill:reload-resource args'),
    'reload.disabled-resource': ('disabled-resource', lambda before, after:
        names(before) == ['reload-resource'] and names(after) == []
        and 'Initial managed package resource.' in before.get('invocation', '')
        and after.get('invocation') == '/skill:reload-resource args'),
    'reload.trust-revoked': ('trust-revoked', lambda before, after:
        names(before) == ['reload-resource'] and names(after) == []
        and 'Initial managed package resource.' in before.get('invocation', '')
        and after.get('invocation') == '/skill:reload-resource args'),
}


def names(state: dict) -> list[str]:
    return [item['name'] for item in state.get('skills', [])]


def run(argv: list[str], *, cwd: Path, env: dict[str, str], log: Path) -> dict:
    try:
        result = subprocess.run(argv, cwd=cwd, env=env, capture_output=True, text=True, timeout=180)
        value = {'argv': argv, 'exitCode': result.returncode,
                 'stdout': result.stdout, 'stderr': result.stderr}
    except (OSError, subprocess.TimeoutExpired) as error:
        value = {'argv': argv, 'exitCode': 127 if isinstance(error, OSError) else 124,
                 'stdout': '', 'stderr': str(error)}
    log.parent.mkdir(parents=True, exist_ok=True)
    log.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')
    if value['exitCode'] != 0:
        raise RuntimeError(f"Process failed ({value['exitCode']}): {' '.join(argv)}\n{value['stderr']}")
    return value


def execute_product(product: str, scenario: str, pi: Path, pisharp_probe: Path,
                    profile: Path, env: dict[str, str]) -> tuple[dict, dict]:
    root = profile / scenario / product
    root.mkdir(parents=True, exist_ok=True)
    if scenario == 'git':
        prepare_git_cache(root, env)
    if product == 'pi':
        argv = ['node', '--experimental-transform-types',
                str(ROOT / 'tools/parity/managed-package-startup-reload-probe.mjs'),
                str(pi), str(root), scenario]
    else:
        argv = ['dotnet', str(pisharp_probe), str(root), scenario]
    result = run(argv, cwd=ROOT, env=env,
                 log=root / 'process.json')
    state = json.loads(result['stdout'].splitlines()[-1])
    if state.get('scenario') != scenario:
        raise ValueError(f'{product} returned the wrong scenario for {scenario}')
    return state, result


def prepare_git_cache(root: Path, env: dict[str, str]) -> None:
    cache = root / 'agent/git/127.0.0.1/fixture/repo'
    skill = cache / 'skills/guide'
    skill.mkdir(parents=True, exist_ok=True)
    (cache / 'package.json').write_text('{"pi":{"skills":["skills"]}}\n', encoding='utf-8')
    (skill / 'SKILL.md').write_text(
        '---\nname: reload-resource\ndescription: reload-resource\n---\nInitial managed package resource.\n',
        encoding='utf-8')
    git_env = dict(env, GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=str(root / 'gitconfig'),
                   GIT_TERMINAL_PROMPT='0', GIT_AUTHOR_NAME='Parity Fixture',
                   GIT_AUTHOR_EMAIL='parity@example.invalid', GIT_COMMITTER_NAME='Parity Fixture',
                   GIT_COMMITTER_EMAIL='parity@example.invalid')
    remote = 'git://127.0.0.1:54321/fixture/repo.git'
    subprocess.run(['git', 'init', '--initial-branch=main'], cwd=cache, env=git_env,
                   check=True, capture_output=True, text=True, timeout=30)
    subprocess.run(['git', 'add', '.'], cwd=cache, env=git_env,
                   check=True, capture_output=True, text=True, timeout=30)
    subprocess.run(['git', 'commit', '-m', 'Initial managed package'], cwd=cache, env=git_env,
                   check=True, capture_output=True, text=True, timeout=30)
    subprocess.run(['git', 'remote', 'add', 'origin', remote], cwd=cache, env=git_env,
                   check=True, capture_output=True, text=True, timeout=30)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.pi, args.profile, args.output = args.pi.resolve(), args.profile.resolve(), args.output.resolve()
    pi_sha = verify_pi_checkout(args.pi)
    args.profile.mkdir(parents=True, exist_ok=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)

    work = args.profile / 'probe-build'
    work.mkdir(parents=True, exist_ok=True)
    source_file = ROOT / 'tools/parity/managed-package-startup-reload-probe.cs'
    project = work / 'StartupReloadProbe.csproj'
    project.write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
        '<OutputType>Exe</OutputType><AssemblyName>StartupReloadProbe</AssemblyName>'
        '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>'
        f'<ProjectReference Include="{ROOT / "src/PiSharp.Runtime/PiSharp.Runtime.csproj"}" />'
        '</ItemGroup></Project>\n', encoding='utf-8')
    (work / 'Program.cs').write_text(source_file.read_text(encoding='utf-8'), encoding='utf-8')
    env = dict(os.environ)
    env.update(DOTNET_PROCESSOR_COUNT='2', MSBUILDDISABLENODEREUSE='1',
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               DOTNET_NOLOGO='1', PI_OFFLINE='1',
               NUGET_PACKAGES=str(args.profile / 'nuget'),
               NUGET_HTTP_CACHE_PATH=str(args.profile / 'nuget-http'))
    build = run(['dotnet', 'build', str(project), '--warnaserror', '--disable-build-servers',
                 '-m:1', '-p:UseSharedCompilation=false'], cwd=ROOT, env=env,
                log=work / 'build.json')
    probe = work / 'bin/Debug/net10.0/StartupReloadProbe.dll'
    if not probe.is_file():
        raise FileNotFoundError('The startup/reload C# probe build produced no assembly')

    cases = {}
    all_passed = True
    for case_id, (scenario, predicate) in SCENARIOS.items():
        product_states = {}
        product_commands = {}
        for product in ('pi', 'pisharp'):
            product_root = args.profile / scenario / product
            product_root.mkdir(parents=True, exist_ok=True)
            product_env = dict(env, HOME=str(product_root / 'home'),
                               XDG_CONFIG_HOME=str(product_root / 'config'),
                               XDG_CACHE_HOME=str(product_root / 'cache'),
                               TMPDIR=str(product_root / 'tmp'))
            for key in ('HOME', 'XDG_CONFIG_HOME', 'XDG_CACHE_HOME', 'TMPDIR'):
                Path(product_env[key]).mkdir(parents=True, exist_ok=True)
            product_states[product], product_commands[product] = execute_product(
                product, scenario, args.pi, probe, args.profile, product_env)
        pi_state = {'before': product_states['pi']['before'], 'after': product_states['pi']['after']}
        actual_state = {'before': product_states['pisharp']['before'], 'after': product_states['pisharp']['after']}
        pi_contract = predicate(pi_state['before'], pi_state['after'])
        pisharp_contract = predicate(actual_state['before'], actual_state['after'])
        matched = pi_state == actual_state
        passed = matched and pi_contract and pisharp_contract
        cases[case_id] = {
            'pi': pi_state, 'pisharp': actual_state,
            'commands': product_commands,
            'match': matched, 'piContractSatisfied': pi_contract,
            'pisharpContractSatisfied': pisharp_contract, 'contractSatisfied': passed,
        }
        all_passed = all_passed and passed

    try:
        source_sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    except subprocess.CalledProcessError:
        source_sha = None
    evidence = {
        'fixtureVersion': 'managed-package-startup-reload-v1',
        'piSha': pi_sha,
        'pisharpSourceSha': source_sha,
        'probeSourceSha256': hashlib.sha256(source_file.read_bytes()).hexdigest(),
        'build': {'argv': build['argv'], 'exitCode': build['exitCode']},
        'cases': cases,
        'match': all_passed,
        'limitations': [
            'Local package resource reload transitions are isolated from npm/Git acquisition; those source types have dedicated install and update process runners.',
            'Duplicate extension registration is verified by the managed npm process differential.',
        ],
    }
    args.output.write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'piSha': pi_sha, 'cases': len(cases), 'match': all_passed,
                      'output': str(args.output)}))
    return 0 if all_passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
