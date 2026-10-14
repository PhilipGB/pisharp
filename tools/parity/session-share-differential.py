#!/usr/bin/env python3
"""Compare share routing, errors, exports and settings with pinned Pi; all uploads use fixtures."""
import argparse
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
pi_sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]


def flow(identifier, **options):
    return dict(id=identifier, destination='auto', **options)


flows = [
    dict(id='forced-gist', destination='gist', token='fixture-token', credentialError='unexpected Radius lookup'),
    dict(id='forced-radius-without-auth', destination='radius'),
    flow('auto-without-auth'), flow('auto-with-auth', token='fixture-token'),
    dict(id='forced-radius-with-auth', destination='radius', token='fixture-token'),
    dict(id='viewer-override', destination='gist', viewer='https://viewer.example/session/'),
    flow('radius-rejected', token='fixture-token', status=403, body='{"error":"denied"}'),
    flow('radius-broken-json', token='fixture-token', body='broken JSON'),
    flow('radius-no-artifact', token='fixture-token', body='{}'),
    flow('radius-null-artifact', token='fixture-token', body='{"artifact":null}'),
    flow('radius-false-artifact', token='fixture-token', body='{"artifact":false}'),
    flow('radius-zero-artifact', token='fixture-token', body='{"artifact":0}'),
    flow('radius-empty-artifact', token='fixture-token', body='{"artifact":""}'),
    flow('radius-empty-object-artifact', token='fixture-token', body='{"artifact":{}}'),
    flow('radius-null-url', token='fixture-token', body='{"artifact":{"canonical_url":null}}'),
    flow('radius-numeric-error', token='fixture-token', status=403, body='{"error":123}'),
    flow('radius-network-error', token='fixture-token', networkError='fixture network error'),
    flow('radius-cancel', token='fixture-token', cancel='radius'),
    dict(id='gist-cancel', destination='gist', cancel='gist', wait=True),
    dict(id='export-failed', destination='gist', exportError='fixture export failed'),
    dict(id='html-failed', destination='gist', htmlError='fixture html failed'),
    dict(id='github-auth-failed', destination='gist', authCode=1),
    dict(id='github-missing', destination='gist', missingGithub=True),
    dict(id='gist-failed', destination='gist', code=1, error='fixture gist failed'),
    dict(id='gist-failed-empty-error', destination='gist', code=1, error=''),
    dict(id='gist-empty-output', destination='gist', output=''),
    dict(id='gist-trailing-slash', destination='gist', output='https://gist.github.com/'),
    flow('credential-failed', credentialError='fixture credential failed'),
]
settings = [dict(id='absent', user={'theme': 'dark'}, project={}),
            dict(id='user-gist', user={'theme': 'dark', 'sessionShareDestination': 'gist'}, project={}),
            dict(id='project-radius', user={'sessionShareDestination': 'gist'}, project={'sessionShareDestination': 'radius'})]
for index, value in enumerate(['auto', 'gist', 'radius', 'unknown', False, None, 123, [], {}]):
    settings.append(dict(id=f'project-{index}', user={'theme': 'dark', 'sessionShareDestination': 'gist'},
                         project={'sessionShareDestination': value}))
records = [dict(type='session', version=3, id='share-session', timestamp='2026-10-13T12:00:00.000Z', cwd='/tmp', extra='header metadata'),
           dict(type='custom', customType='fixture', id='root', parentId=None, timestamp='2026-10-13T12:00:01.000Z', data={'retained': True}),
           dict(type='message', id='selected', parentId='root', timestamp='2026-10-13T12:00:02.000Z',
                message=dict(role='user', content='selected prompt', timestamp=1)),
           dict(type='message', id='inactive', parentId='root', timestamp='2026-10-13T12:00:03.000Z',
                message=dict(role='user', content='other prompt', timestamp=2))]
inputs = dict(flows=flows, settings=settings, heads=[None, 'root', 'selected', 'inactive'],
              sessionJsonl=''.join(json.dumps(record) + '\n' for record in records),
              presentation=dict(systemPrompt='share </script> 💡', tools=[dict(name='fixture', description='fixture tool',
                  parameters=dict(type='object', properties=dict(value=dict(type='string'))))]))
with tempfile.TemporaryDirectory(prefix='pisharp-share-differential-') as directory:
    work = Path(directory)
    (work / 'bin').mkdir()
    (work / 'bin/gh').write_text('''#!/usr/bin/env python3
import json, os, pathlib, sys, time
root = pathlib.Path(os.environ['PISHARP_SHARE_FIXTURE_ROOT'])
scenario = json.loads((root / 'scenario.json').read_text())
with (root / 'commands.jsonl').open('a') as log:
    log.write(json.dumps(sys.argv[1:]) + '\\n')
if sys.argv[1:3] == ['auth', 'status']:
    sys.exit(scenario.get('authCode', 0))
(root / 'gist-pid.tmp').write_text(str(os.getpid()))
(root / 'gist-pid.tmp').replace(root / 'gist-pid')
if scenario.get('wait'):
    while True: time.sleep(0.02)
if pathlib.Path(sys.argv[-1]).read_text() != 'fixture html': raise RuntimeError('wrong export')
print(scenario.get('output', 'https://gist.github.com/fixture/fixture-id'))
print(scenario.get('error', ''), file=sys.stderr)
sys.exit(scenario.get('code', 0))
''')
    (work / 'bin/gh').chmod(0o700)
    (work / 'inputs.json').write_text(json.dumps(inputs))
    (work / 'branch.jsonl').write_text(inputs['sessionJsonl'])
    (work / 'Program.cs').write_text((repo / 'tools/parity/session-share-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http', PATH=str(work / 'bin') + os.pathsep + os.environ['PATH'])
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'],
                           env=env, capture_output=True, text=True, timeout=120)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'),
        str(work / 'inputs.json'), str(work)], env=env, timeout=60))
    expected = json.loads(subprocess.check_output(['node', '--experimental-transform-types',
        str(repo / 'tools/parity/session-share-probe.mjs'), str(args.pi.resolve()), str(work / 'inputs.json'), str(work)], env=env, timeout=60))
    differences = []
    for section in ['flows', 'branches', 'settings']:
        for index, (pi, native) in enumerate(zip(expected[section], actual[section], strict=True)):
            if pi != native:
                differences.append(dict(section=section, index=index, id=pi.get('id', pi.get('head')), pi=pi, pisharp=native))
    result = dict(piSha=pi_sha, inputs=inputs, pi=expected, pisharp=actual, differences=differences, match=not differences,
        comparison='Exact parsed branch/metadata records, HTML-embedded viewer data, settings precedence and persisted global JSON; semantic upload result/error, destination lookup, progress labels, actual private-gh arguments, cancellation/child exit and temporary export cleanup. Only generated share IDs/timestamps and unique temporary export paths are normalized.',
        limitations='Pi uses its real source session/settings/share owners, with a fixture fetch response; PiSharp uses its real share owner and HttpMessageHandler fixture. The fake gh is a real isolated process. Full terminal row layout, all HTML visual rendering, paid live Radius/GitHub publication, native Windows execution and the broader output/share capability remain open.')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    assert not differences, json.dumps(differences, indent=2)
    print(f'Matched {len(flows)} share flows, four branch/HTML metadata exports and {len(settings)} settings/persistence vectors.')
