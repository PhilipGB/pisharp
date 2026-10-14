#!/usr/bin/env python3
"""Compare trusted prompt discovery, CLI overrides, text decoding and read errors with Pi."""
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
    raise RuntimeError('Unreadable prompt-file evidence requires a non-root Unix runner.')
pi_sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]


def file(value, **options):
    return dict(bytes=base64.b64encode(value.encode('utf8') if isinstance(value, str) else value).decode(), **options)


defaults = {'agent/SYSTEM.md': file('GLOBAL SYSTEM'), 'agent/APPEND_SYSTEM.md': file('GLOBAL APPEND'),
            'project/.pi/SYSTEM.md': file('PROJECT SYSTEM'), 'project/.pi/APPEND_SYSTEM.md': file('PROJECT APPEND')}
cases = [dict(id='trusted-project-replaces-both', trusted=True, files=defaults),
         dict(id='untrusted-project-is-never-read', trusted=False, files=defaults),
         dict(id='empty-project-files-still-override', trusted=True, files={**defaults,
              'project/.pi/SYSTEM.md': file(''), 'project/.pi/APPEND_SYSTEM.md': file('')}),
         dict(id='no-discovered-prompts', trusted=True, files={})]
for kind in ['SYSTEM.md', 'APPEND_SYSTEM.md']:
    for identifier, contents, options in [('large', 'x' * (65 * 1024), {}), ('two-boms', '\ufeff\ufeffretained', {}),
                                          ('invalid-utf8', b'bad \xff tail', {}), ('denied', 'unreadable', {'denied': True}),
                                          ('directory', '', {'directory': True})]:
        cases.append(dict(id=f'discovered-{kind}-{identifier}', trusted=True,
                          files={**defaults, 'project/.pi/' + kind: file(contents, **options)}))
for identifier, value, files in [('literal', 'override literal', {}), ('whitespace', '  ', {}),
                                ('empty', '', {}), ('dash-prefix', '--literal', {}),
                                ('large-file', 'input.txt', {'project/input.txt': file('x' * (65 * 1024))}),
                                ('two-boms', 'input.txt', {'project/input.txt': file('\ufeff\ufeffretained')}),
                                ('invalid-utf8', 'input.txt', {'project/input.txt': file(b'bad \xff tail')}),
                                ('denied-file', 'input.txt', {'project/input.txt': file('unreadable', denied=True)}),
                                ('directory', 'input-dir', {'project/input-dir': file('', directory=True)})]:
    cases.append(dict(id='explicit-system-' + identifier, trusted=True, files={**defaults, **files}, system=value))
cases.append(dict(id='explicit-append-mix', trusted=True, files={**defaults, 'project/input.txt': file('FILE')},
                  append=['literal', 'input.txt']))
cases.append(dict(id='explicit-append-empty-literal-is-ignored', trusted=True, files=defaults, append=['', 'literal']))
cases.append(dict(id='explicit-append-empty-file-is-retained', trusted=True,
                  files={**defaults, 'project/empty.txt': file('')}, append=['empty.txt', 'literal']))
cases.append(dict(id='explicit-overrides-do-not-read-unused-discovered-files', trusted=True,
                  files={**defaults, 'project/.pi/SYSTEM.md': file('DENIED', denied=True),
                         'project/.pi/APPEND_SYSTEM.md': file('DENIED', denied=True)}, system='literal', append=['literal']))

with tempfile.TemporaryDirectory(prefix='pisharp-system-prompt-differential-') as directory:
    work = Path(directory)
    (work / 'inputs.json').write_text(json.dumps(cases))
    (work / 'Program.cs').write_text((repo / 'tools/parity/system-prompt-files-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'],
                           env=env, capture_output=True, text=True, timeout=120)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/probe.dll'),
        str(work / 'inputs.json'), str(work)], env=env, timeout=30))
    expected = json.loads(subprocess.check_output(['node', '--experimental-transform-types',
        str(repo / 'tools/parity/system-prompt-files-probe.mjs'), str(args.pi.resolve()), str(work / 'inputs.json'), str(work)], env=env, timeout=60))
    differences = [dict(id=pi['id'], pi=pi, pisharp=native) for pi, native in zip(expected, actual, strict=True) if pi != native]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=pi_sha, scenarios=[dict(id=case['id'], trusted=case['trusted'],
        system=case.get('system'), append=case.get('append'), files={name: dict(bytes=len(base64.b64decode(value['bytes'])),
            denied=value.get('denied', False), directory=value.get('directory', False)) for name, value in case['files'].items()}) for case in cases],
        pi=expected, pisharp=actual, differences=differences, match=not differences,
        comparison='Exact full normalized-content SHA256/UTF16 length, source-path projections and warning prefix/order from real DefaultResourceLoader/CLI parsing versus native prompt discovery/CLI override resolution. Only isolated workspace prefixes are normalized.',
        limitations='Real non-root Linux files and trusted/untrusted discovery; exact native warning error text/colors and complete provider/startup/reload traces remain separately required.'), indent=2)+'\n')
    assert not differences, json.dumps(differences, indent=2)
    print(f'Matched {len(cases)} prompt discovery/override/content/source scenarios.')
