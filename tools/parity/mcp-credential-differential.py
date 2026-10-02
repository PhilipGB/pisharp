#!/usr/bin/env python3
"""Compare current Pi and PiSharp MCP account isolation and migration."""
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
url = 'https://mcp.example.com/mcp'


def operation(action, name='work', account=None, resource=url):
    return dict(action=action, name=name, url=resource, account=account)


scenarios = [
    dict(id='separate-accounts-and-logout', operations=[
        operation('save', account='work'), operation('save', 'personal', 'personal'),
        operation('load'), operation('load', 'personal'), operation('remove'),
        operation('load'), operation('load', 'personal')]),
    dict(id='legacy-first-load-and-normalized-name', operations=[
        operation('seed', account='legacy'), operation('peek'),
        operation('load', 'my_work'), operation('peek', 'my-work'), operation('load', 'personal')]),
    dict(id='legacy-logout', operations=[
        operation('seed', account='legacy'), operation('remove'), operation('remove')]),
    dict(id='existing-account-precedes-legacy', operations=[
        operation('save', account='work'), operation('seed', account='legacy'),
        operation('peek'), operation('load'), operation('load', 'personal')]),
    dict(id='logout-precedence', operations=[
        operation('save', account='work'), operation('seed', account='legacy'),
        operation('remove'), operation('peek'), operation('remove'), operation('load')]),
    dict(id='same-name-different-resource', operations=[
        operation('save', account='first'), operation('save', account='second', resource=url + '2'),
        operation('load'), operation('load', resource=url + '2'), operation('remove'),
        operation('load', resource=url + '2')])]

with tempfile.TemporaryDirectory(prefix='pisharp-mcp-differential-') as directory:
    work = pathlib.Path(directory)
    (work / 'Program.cs').write_text((probes / 'mcp-credential-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>''')
    environment = dict(os.environ)
    environment['NUGET_HTTP_CACHE_PATH'] = '/tmp/pisharp-nuget-http'
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-p:UseSharedCompilation=false', '-m:1', '--nologo'],
                   env=environment, check=True, stdout=subprocess.DEVNULL, timeout=120)
    encoded = json.dumps(scenarios)
    pisharp_results = json.loads(subprocess.check_output(
        ['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'), encoded], env=environment, timeout=30))
    oracle = pi / 'packages/coding-agent/test/pisharp-credential-differential.test.ts'
    if oracle.exists():
        raise RuntimeError(f'Refusing to overwrite {oracle}')
    shutil.copyfile(probes / 'mcp-credential-probe.pi.test.ts', oracle)
    try:
        environment['PISHARP_CREDENTIAL_SCENARIOS'] = encoded
        environment['PISHARP_CREDENTIAL_OUTPUT'] = str(work / 'pi.json')
        subprocess.run(['node', str(pi / 'node_modules/vitest/vitest.mjs'), 'run', str(oracle)],
                       cwd=pi / 'packages/coding-agent', env=environment, check=True, timeout=60)
    finally:
        oracle.unlink()
    pi_results = json.loads((work / 'pi.json').read_text())
    assert pi_results == pisharp_results, json.dumps(dict(pi=pi_results, pisharp=pisharp_results), indent=2)
    evidence = dict(
        piSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=pi, text=True).strip(),
        normalization='Compare account tokens, operation results and exact persisted keys; omit SDK-specific token envelope and fixed timestamps.',
        scenarios=scenarios, pi=pi_results, pisharp=pisharp_results, match=True)
    pathlib.Path(args.output).write_text(json.dumps(evidence, indent=2) + '\n')
    print(f'Matched {len(scenarios)} credential scenarios.')
