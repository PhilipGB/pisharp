#!/usr/bin/env python3
"""Compare complete skill/template text and per-file recovery with real pinned Pi loaders."""
import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import tempfile
from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
if not hasattr(os, 'geteuid') or os.geteuid() == 0:
    raise RuntimeError('Permission fixtures require a non-root Unix runner.')
pi_sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
cases = []
for kind in ['skill', 'prompt']:
    frontmatter = b'---\nname: guide\ndescription: Guide\n---\n'
    target = 'guide/SKILL.md' if kind == 'skill' else 'guide.md'
    for identifier, content, denied in [('normal', frontmatter + b'body', False),
        ('large', frontmatter + b'x' * 66560, False), ('utf8', frontmatter + b'bad \xff tail', False),
        ('one-bom', b'\xef\xbb\xbf' + frontmatter + b'body', False),
        ('two-boms', b'\xef\xbb\xbf\xef\xbb\xbf' + frontmatter + b'body', False),
        ('bare-cr', frontmatter.replace(b'\n', b'\r') + b'body\rsecond', False),
        ('denied-then-valid', frontmatter + b'body', True)]:
        files = {target: dict(bytes=base64.b64encode(content).decode(), denied=denied)}
        paths = [target]
        if denied:
            second = 'second/SKILL.md' if kind == 'skill' else 'second.md'
            files[second] = dict(bytes=base64.b64encode(frontmatter.replace(b'guide', b'second') + b'next').decode(), denied=False)
            paths.append(second)
        cases.append(dict(id=kind + '-' + identifier, kind=kind, files=files, paths=paths))
with tempfile.TemporaryDirectory(prefix='pisharp-resource-file-oracle-') as directory:
    work = Path(directory)
    for case in cases:
        for relative, file in case['files'].items():
            target = work / case['id'] / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(base64.b64decode(file['bytes']))
            if file['denied']:
                target.chmod(0)
    (work / 'inputs.json').write_text(json.dumps(cases))
    (work / 'Program.cs').write_text((repo / 'tools/parity/resource-file-loading-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
</PropertyGroup><ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'],
                           env=env, capture_output=True, text=True, timeout=120)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/probe.dll'), str(work / 'inputs.json'), str(work)], env=env, timeout=30))
    expected = json.loads(subprocess.check_output(['node', '--experimental-transform-types', str(repo / 'tools/parity/resource-file-loading-probe.mjs'),
        str(args.pi.resolve()), str(work / 'inputs.json'), str(work)], env=env, timeout=60))
    differences = [dict(id=pi['id'], pi=pi, pisharp=native) for pi, native in zip(expected, actual, strict=True) if pi != native]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=pi_sha, scenarios=[dict(id=c['id'], kind=c['kind'], paths=c['paths']) for c in cases],
        pi=expected, pisharp=actual, differences=differences, match=not differences,
        comparison='Exact metadata/order, full body SHA256/UTF16 length and diagnostic type/path/order; native platform read-error text is classified as read warning.'), indent=2) + '\n')
    assert not differences, json.dumps(differences, indent=2)
    print(f'Matched {len(cases)} resource file scenarios.')
