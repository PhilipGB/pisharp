#!/usr/bin/env python3
"""Compare resource expansion with the real Pi AgentSession skill method/template pipeline."""
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
cases = [dict(id=f'input-{index:02d}', input=text, after='none') for index, text in enumerate([
    'ordinary text', '/skill:guide', '/skill:guide arg', '/skill:guide  two args  ',
    '/skill:guide\targ', '/skill:guide\narg', '/skill:guide\rarg', '/skill:guide\u00a0arg',
    '/skill:missing', '/skill:missing args', '/skill:', ' /skill:guide', '/SKILL:guide',
    '/skill:guide \ufeffarg\ufeff', '/review "two words"', '/review\targ', '/review\narg',
    '/review ', '/review  "unterminated', '/skill:template arg'])]
cases.extend(dict(id='after-' + state, input='/skill:guide args', after=state)
             for state in ['changed', 'delete', 'invalid-yaml', 'denied'])
for index, (template, arguments) in enumerate([
    ('${ARGUMENTS:-fallback}', ''), ('${ARGUMENTS:-fallback}', 'one two'),
    ('${@:-fallback}', ''), ('${1:-fallback}', '""'), ('$0|$1|$2|$10', 'one two'),
    ('${@:2}', 'one two three'), ('${@:2:1}', 'one two three'), ('${@:0:2}', 'one two three'),
    ('${@:9}', 'one two three'), ('${@:1:0}', 'one two three'),
    ('${9999999999999999999999:-fallback}', 'one'), ('${@:999999999999999999999}', 'one'),
    ('$1|$@', r'one\ two'), ('$1|$@', r'"one\"two"'), ('$1|$@', '"" second'),
    ('$1|$@', '\ufeffone\ufefftwo'), ('$1|$@', 'one\u0085two'),
    ('${1:-$2}|$2', ''), ('$1|$2', '"$2" "${1:-default}"'), ('${ARGUMENTS}', 'one two'),
    ('$١|${١:-fallback}', 'one'), ('${@:١}|${@:1:١}', 'one two'),
    ('$1١|$١1', 'one two')]):
    cases.append(dict(id=f'template-{index:02d}', input='/review ' + arguments, after='none', template=template))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if not hasattr(os, 'geteuid') or os.geteuid() == 0:
        raise RuntimeError('Permission cases require a non-root Unix runner.')
    sha = verify_pi_checkout(args.pi)
    with tempfile.TemporaryDirectory(prefix='pisharp-resource-invocation-') as directory:
        root = Path(directory)
        for product in ['pi', 'pisharp']:
            for case in cases:
                work = root / product / case['id']
                (work / 'guide').mkdir(parents=True)
                (work / 'prompts').mkdir()
                (work / 'guide/SKILL.md').write_text('---\nname: guide\ndescription: Guide\n---\nbody')
                (work / 'prompts/review.md').write_text(case.get('template', 'Review $1 | $@'))
                (work / 'prompts/skill:template.md').write_text('Template $1')
        inputs = root / 'inputs.json'
        inputs.write_text(json.dumps(cases))
        (root / 'Program.cs').write_text((ROOT / 'tools/parity/resource-invocation-probe.cs').read_text())
        (root / 'probe.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{ROOT}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>')
        env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http', NUGET_PACKAGES='/tmp/pisharp-nuget-packages')
        build = subprocess.run(['dotnet', 'build', str(root / 'probe.csproj'), '--warnaserror', '-m:1',
            '-p:UseSharedCompilation=false', f'-p:RestoreFallbackFolders={Path.home() / ".nuget/packages"}'],
            env=env, capture_output=True, text=True, timeout=120)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        home = root / 'home'
        home.mkdir()
        env.update(HOME=str(home))
        expected = json.loads(subprocess.check_output(['node', '--experimental-transform-types',
            str(ROOT / 'tools/parity/resource-invocation-probe.mjs'), str(args.pi.resolve()), str(inputs), str(root)], env=env, timeout=60))
        actual = json.loads(subprocess.check_output(['dotnet', str(root / 'bin/Debug/net10.0/probe.dll'), str(inputs), str(root)], env=env, timeout=60))
    differences = [dict(id=p['id'], pi=p, pisharp=n) for p, n in zip(expected, actual, strict=True) if p != n]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, cases=cases, pi=expected, pisharp=actual,
        differences=differences, match=not differences), indent=2) + '\n')
    print(f'{len(cases)} invocation scenarios; {len(differences)} mismatches.', flush=True)
    assert not differences, ', '.join(d['id'] for d in differences)


if __name__ == '__main__':
    main()
