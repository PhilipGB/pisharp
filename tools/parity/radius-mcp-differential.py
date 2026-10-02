#!/usr/bin/env python3
"""Compare current Pi and PiSharp radius-mcp MCP setup configuration."""
import argparse
import json
import os
import pathlib
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--pi', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
pi = pathlib.Path(args.pi).resolve()
repo = pathlib.Path(__file__).resolve().parents[2]
probes = pathlib.Path(__file__).resolve().parent

url = 'https://radius.pi.dev/mcp'
scenarios = [dict(id='missing-' + action, action=action) for action in ('yes', 'no', 'cancel')]
scenarios += [
    dict(id='configured', action='yes', global_config=dict(mcpServers=dict(custom=dict(url=url + '///', auth=dict(provider='radius'))))),
    dict(id='existing-options', action='yes', global_config=dict(autoEnableCodemode=False, mcpServers=dict(custom=dict(url=url+'/', oauth={}, headers={'X-Extra': 'kept'}, enabled=False)))),
    dict(id='name-conflict', action='yes', global_config=dict(mcpServers=dict(radius=dict(command='unrelated')))),
    dict(id='double-name-conflict', action='yes', global_config=dict(mcpServers={'radius': dict(command='first'), 'radius-mcp': dict(command='second')})),
    dict(id='project-only', action='yes', project=dict(mcpServers=dict(custom=dict(url=url, auth=dict(provider='radius'))))),
    dict(id='other-provider', action='yes', global_config=dict(mcpServers=dict(custom=dict(url=url, auth=dict(provider='other'))))),
    dict(id='case-sensitive-url', action='yes', global_config=dict(mcpServers=dict(custom=dict(url='https://RADIUS.pi.dev/mcp'))))]
for scenario in scenarios:
    if 'global_config' in scenario:
        scenario['global'] = scenario.pop('global_config')

with tempfile.TemporaryDirectory(prefix='pisharp-radius-mcp-differential-') as directory:
    work = pathlib.Path(directory)
    (work / 'Program.cs').write_text((probes / 'radius-mcp-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    environment = dict(os.environ)
    environment['NUGET_HTTP_CACHE_PATH'] = '/tmp/pisharp-nuget-http'
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-p:UseSharedCompilation=false', '-m:1', '--nologo'],
                   env=environment, check=True, stdout=subprocess.DEVNULL, timeout=120)
    encoded = json.dumps(scenarios)
    pisharp_results = json.loads(subprocess.check_output(
        ['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'), encoded], env=environment, timeout=30))
    oracle = pi / 'packages/coding-agent/test/pisharp-radius-mcp-differential.test.ts'
    if oracle.exists():
        raise RuntimeError(f'Refusing to overwrite {oracle}')
    shutil.copyfile(probes / 'radius-mcp-probe.pi.test.ts', oracle)
    try:
        environment['PISHARP_RADIUS_SCENARIOS'] = encoded
        environment['PISHARP_RADIUS_OUTPUT'] = str(work / 'pi.json')
        subprocess.run(['node', str(pi / 'node_modules/vitest/vitest.mjs'), 'run', str(oracle)],
                       cwd=pi / 'packages/coding-agent', env=environment, check=True, timeout=60)
    finally:
        oracle.unlink()
    pi_results = json.loads((work / 'pi.json').read_text())
    assert pi_results == pisharp_results, json.dumps(dict(pi=pi_results, pisharp=pisharp_results), indent=2)
    evidence = dict(
        piSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=pi, text=True).strip(),
        normalization='Exact offer presence and full resulting global JSON. No configuration normalization. UI rendering, credential exchange, cancellation navigation and actual runtime reload are separate terminal coverage.',
        scenarios=scenarios, pi=pi_results, pisharp=pisharp_results, match=True)
    pathlib.Path(args.output).write_text(json.dumps(evidence, indent=2) + '\n')
    print(f'Matched {len(scenarios)} radius-mcp MCP setup scenarios.')
