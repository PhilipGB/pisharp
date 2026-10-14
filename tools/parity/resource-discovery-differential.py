#!/usr/bin/env python3
"""Compare directory traversal through the real Pi skill/template loaders."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
ROOT = Path(__file__).resolve().parents[2]


def skill(name, description=True):
    return f'---\nname: {name}\n' + (f'description: {name} guide\n' if description else '') + f'---\n{name} body'


def scenarios():
    cases = []

    def add(name, files, kind='skill', links=None):
        cases.append(dict(id=name, kind=kind, paths=['scan'], files=files, links=links or {}))

    add('root-declaration', {'scan/SKILL.md': skill('root'), 'scan/alias.md': skill('alias'),
                             'scan/child/SKILL.md': skill('child')})
    add('root-missing-description', {'scan/SKILL.md': skill('root', False), 'scan/child/SKILL.md': skill('child')})
    add('root-invalid-yaml', {'scan/SKILL.md': '---\ndescription: [broken\n---\nbody',
                             'scan/child/SKILL.md': skill('child')})
    add('root-aliases', {'scan/a.md': skill('a'), 'scan/b.md': skill('b'),
                        'scan/child/alias.md': skill('nested-alias'), 'scan/child/SKILL.md': skill('child')})
    add('nested-alias-only', {'scan/child/alias.md': skill('nested-alias')})
    add('case-sensitive-skills', {'scan/skill.md': skill('lower'), 'scan/SKILL.MD': skill('upper'),
                                 'scan/child/skill.md': skill('nested-lower')})
    add('depth-first-order', {'scan/a.md': skill('root-alias'), 'scan/a/SKILL.md': skill('nested'),
                             'scan/z/SKILL.md': skill('last')})
    add('hidden-skills', {'scan/.hidden.md': skill('hidden-file'), 'scan/.hidden/SKILL.md': skill('hidden-dir'),
                          'scan/node_modules/SKILL.md': skill('dependency'), 'scan/public/SKILL.md': skill('public')})
    add('root-declaration-link', {'target.md': skill('linked'), 'scan/child/SKILL.md': skill('child')},
        links={'scan/SKILL.md': '../target.md'})
    add('broken-root-link', {'scan/child/SKILL.md': skill('child')}, links={'scan/SKILL.md': '../absent.md'})
    add('directory-link', {'target/SKILL.md': skill('linked')}, links={'scan/linked': '../target'})
    add('prompt-direct-files', {'scan/a.md': 'a body', 'scan/child/b.md': 'nested body'}, 'prompt')
    add('prompt-hidden-file', {'scan/.hidden.md': 'hidden body', 'scan/a.md': 'a body'}, 'prompt')
    add('prompt-case-sensitive', {'scan/a.md': 'a body', 'scan/B.MD': 'uppercase body'}, 'prompt')
    add('prompt-file-link', {'target.txt': 'linked body'}, 'prompt', {'scan/linked.md': '../target.txt'})
    add('prompt-directory-link', {'target/a.md': 'nested body'}, 'prompt', {'scan/linked': '../target'})
    add('over-one-thousand-skills', {f'scan/s{i:04d}/SKILL.md': skill(f's{i:04d}') for i in range(1002)})
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    sha = verify_pi_checkout(args.pi)
    cases = scenarios()
    with tempfile.TemporaryDirectory(prefix='pisharp-resource-discovery-') as directory:
        root = Path(directory)
        (root / 'Program.cs').write_text((ROOT / 'tools/parity/resource-path-probe.cs').read_text())
        (root / 'probe.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{ROOT}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>')
        env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http', NUGET_PACKAGES='/tmp/pisharp-nuget-packages')
        build = subprocess.run(['dotnet', 'build', str(root / 'probe.csproj'), '--warnaserror', '-m:1',
                                '-p:UseSharedCompilation=false', f'-p:RestoreFallbackFolders={Path.home() / ".nuget/packages"}'],
                               env=env, capture_output=True, text=True, timeout=120)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        for case in cases:
            work = root / case['id']
            (work / 'scan').mkdir(parents=True)
            for name, content in case['files'].items():
                target = work / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(content)
            for name, target in case['links'].items():
                link = work / name
                link.parent.mkdir(parents=True, exist_ok=True)
                link.symlink_to(target)
        inputs = root / 'inputs.json'
        inputs.write_text(json.dumps(cases))
        expected = json.loads(subprocess.check_output(['node', '--experimental-transform-types',
            str(ROOT / 'tools/parity/resource-path-probe.mjs'), str(args.pi.resolve()), str(inputs), str(root)], env=env, timeout=60))
        actual = json.loads(subprocess.check_output(['dotnet', str(root / 'bin/Debug/net10.0/probe.dll'),
                                                     str(inputs), str(root)], env=env, timeout=60))
    differences = [dict(id=p['id'], pi=p, pisharp=n) for p, n in zip(expected, actual, strict=True) if p != n]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, scenarios=cases, pi=expected, pisharp=actual,
                                           differences=differences, match=not differences), indent=2) + '\n')
    print(f'{len(cases)} directory scenarios; {len(differences)} mismatches.', flush=True)
    assert not differences, ', '.join(d['id'] for d in differences)


if __name__ == '__main__':
    main()
