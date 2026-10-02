#!/usr/bin/env python3
"""Compare current Pi and PiSharp deferred tool restoration."""
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

scenarios = ['resume', 'additive', 'replacement', 'prompt', 'cancel', 'excluded', 'allowed', 'reload']

with tempfile.TemporaryDirectory(prefix='pisharp-deferred-differential-') as directory:
    work = pathlib.Path(directory)
    (work / 'Program.cs').write_text((probes / 'deferred-tool-probe.cs').read_text())
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
    oracle = pi / 'packages/coding-agent/test/suite/pisharp-deferred-differential.test.ts'
    if oracle.exists():
        raise RuntimeError(f'Refusing to overwrite {oracle}')
    shutil.copyfile(probes / 'deferred-tool-probe.pi.test.ts', oracle)
    try:
        environment['PISHARP_DEFERRED_SCENARIOS'] = encoded
        environment['PISHARP_DEFERRED_OUTPUT'] = str(work / 'pi.json')
        subprocess.run(['node', str(pi / 'node_modules/vitest/vitest.mjs'), 'run', str(oracle)],
                       cwd=pi / 'packages/coding-agent', env=environment, check=True, timeout=60)
    finally:
        oracle.unlink()
    pi_results = json.loads((work / 'pi.json').read_text())
    assert pi_results == pisharp_results, json.dumps(dict(pi=pi_results, pisharp=pisharp_results), indent=2)
    evidence = dict(
        piSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=pi, text=True).strip(),
        normalization='Compare ordered active names before and after registration, provider request tool names and persisted loadout names; omit unrelated messages, metadata and IDs.',
        scenarios=scenarios, pi=pi_results, pisharp=pisharp_results, match=True)
    pathlib.Path(args.output).write_text(json.dumps(evidence, indent=2) + '\n')
    print(f'Matched {len(scenarios)} deferred-tool scenarios.')
