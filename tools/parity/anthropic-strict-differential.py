#!/usr/bin/env python3
"""Compare strict-tool retry traces from pinned Pi and PiSharp's real Anthropic SDK."""
import argparse
import hashlib
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
grammar_error = 'The compiled grammar is too large, which would cause performance issues.'
schema = dict(type='object', properties=dict(value=dict(type='string'), optional=dict(type='number')), required=['value'])


def tool(name, policy=None, parameters=None):
    result = dict(name=name, description='lookup', parameters=parameters if parameters is not None else schema)
    if policy:
        result['constrainedSampling'] = dict(type='json_schema', strict=policy)
    return result


def scenario(identifier, **options):
    result = dict(id=identifier, streaming=True, errorMessage=grammar_error, status=400,
                  errorType='invalid_request_error', streamError=False, secondError=False,
                  native=False, supported=True, hookError=False, repeat=False,
                  messages=[dict(role='system', content='base', timestamp=1,
                                 toolsAdded=[tool('preferred', 'prefer'), tool('required', 'require'), tool('ordinary')]),
                            dict(role='user', content='Hello', timestamp=2)])
    result.update(options)
    return result


cases = []
for streaming in [False, True]:
    for index, message in enumerate([grammar_error, 'Too many strict tools (32). Maximum 20.',
                                    'Schemas contains too many parameters with union types (480 parameters).']):
        cases.append(scenario(f'capacity-{index}-{streaming}', streaming=streaming, errorMessage=message))
    for identifier, options in [('unrelated', dict(errorMessage='Invalid tool schema')),
                                ('wrong-type', dict(errorType='authentication_error')),
                                ('wrong-status', dict(status=500)), ('second-rejection', dict(secondError=True)),
                                ('hook-error', dict(hookError=True)), ('next-request', dict(repeat=True))]:
        cases.append(scenario(f'{identifier}-{streaming}', streaming=streaming, **options))
    for policy in [None, 'require']:
        cases.append(scenario(f'only-{policy}-{streaming}', streaming=streaming,
            messages=[dict(role='system', content='base', timestamp=1, toolsAdded=[tool('lookup', policy)]),
                      dict(role='user', content='Hello', timestamp=2)]))
cases.append(scenario('accepted-stream-error', streamError=True))
cases.append(scenario('inline-redefinitions', native=True,
    messages=[dict(role='system', content='base', timestamp=1, toolsAdded=[tool('lookup', 'require')]),
              dict(role='user', content='Hello', timestamp=2),
              dict(role='system', content='', timestamp=3, toolsAdded=[tool('lookup', 'prefer')]),
              dict(role='system', content='', timestamp=4, toolsAdded=[tool('lookup', 'require')])]))
schema_vectors = [
    schema, dict(type='object'), dict(type='object', properties=dict(value=dict(type=['string', 'null']))),
    dict(type='object', properties=dict(value=dict(type='array', items=dict(type='object', properties=dict(nested=dict(type='string')))))),
    dict(type='object', properties=dict(value=dict(type='number', minimum=1))),
    dict(type='object', properties=dict(value=dict(type='array', minItems=2, items=dict(type='string')))),
    dict(type='object', properties=dict(value=dict(type='string', format='uuid'))),
    dict(type='object', properties=dict(value=dict(type='string', format='unsupported'))),
    dict(type='object', properties=dict(value=dict(anyOf=[dict(type='string'), dict(type='null')]))),
    dict(type='object', properties=dict(value=dict(anyOf=[dict(type='object'), dict(type='null')]))),
    dict(type='object', additionalProperties=True), dict(type='object', properties=dict(value=True)),
    dict(type='object', required=['unknown']), dict(type='object', **{'$ref': 'other'}),
    dict(type='object', properties=dict(value=dict(type='array', items=[]))),
    dict(type='object', properties={'2':dict(type='number'), '01':dict(type='string'),
         '1':dict(type='number'), '4294967295':dict(type='string'), 'last':dict(type='string')}),
]
for index, parameters in enumerate(schema_vectors):
    for policy in ['prefer', 'require']:
        cases.append(scenario(f'schema-{index}-{policy}', status=200,
            messages=[dict(role='system', content='base', timestamp=1, toolsAdded=[tool('lookup', policy, parameters)]),
                      dict(role='user', content='Hello', timestamp=2)]))
cases.append(scenario('unsupported-required-provider', supported=False, status=200,
    messages=[dict(role='system', content='base', timestamp=1, toolsAdded=[tool('lookup', 'require')]),
              dict(role='user', content='Hello', timestamp=2)]))
env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
with tempfile.TemporaryDirectory(prefix='pisharp-anthropic-strict-') as directory:
    work = Path(directory)
    inputs = work / 'cases.json'
    inputs.write_text(json.dumps(cases))
    (work / 'Program.cs').write_text((repo / 'tools/parity/anthropic-strict-probe.cs').read_text())
    (work / 'Fixture.cs').write_text((repo / 'tests/PiSharp.Tests/TestInfrastructure/AnthropicStrictHttpFixture.cs').read_text())
    (work / 'Listener.cs').write_text((repo / 'tests/PiSharp.Tests/TestInfrastructure/TestLoopbackListener.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'],
                           env=env, capture_output=True, text=True, timeout=120)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'), str(inputs)],
                                                env=env, timeout=120))
    expected = json.loads(subprocess.check_output(['node', str(repo / 'tools/parity/anthropic-strict-probe.mjs'),
                                                  str(args.pi.resolve()), str(inputs)], env=env, timeout=120))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=pi_sha, cases=cases, pi=expected, pisharp=actual, match=actual == expected,
        comparison='Exact JSON-equivalent ordered tool/addition schemas, retry request counts, hook counts and unrelated max-token changes; semantic success/error and diagnostics presence. Pi streams every request; the .NET nonstream cases additionally exercise the native SDK path. Both preserve input history.',
        limitations='Response diagnostic timestamps and provider exception/message envelopes are not compared. Cancellation is covered by the .NET control and current Pi source test; paid-provider execution is unavailable. Wider provider/protocol diagnostics and constrained sampling on other APIs remain separately ledgered.'), indent=2) + '\n')
    assert actual == expected, json.dumps([dict(id=left['id'], differingFields=[key for key in left if left[key] != right[key]])
                                         for left, right in zip(expected, actual) if left != right], indent=2)
    print(f'Matched {len(cases)} Anthropic strict-tool request/schema cases.')
