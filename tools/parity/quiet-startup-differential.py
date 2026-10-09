#!/usr/bin/env python3
"""Compare current Pi and PiSharp quiet-startup policy and persistence."""
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

values = [True, False, 'header', None, 1, 'true', 'HEADER', {}, []]
scenarios = [dict(id=f'value-{index}-verbose-{verbose}', user=dict(quietStartup=value), verbose=verbose)
             for index, value in enumerate(values) for verbose in (False, True)]
scenarios += [dict(id=f'missing-verbose-{verbose}', user={}, verbose=verbose) for verbose in (False, True)]
for trusted in (False, True):
    for index, (user, project) in enumerate([(True, 'header'), ('header', False), ('header', True), ('header', None)]):
        scenarios.append(dict(id=f'project-{index}-trusted-{trusted}', user=dict(quietStartup=user),
                              project=dict(quietStartup=project), trusted=trusted, verbose=False))

with tempfile.TemporaryDirectory(prefix='pisharp-quiet-startup-differential-') as directory:
    work = pathlib.Path(directory)
    (work / 'Program.cs').write_text((probes / 'quiet-startup-probe.cs').read_text())
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
    oracle = pi / 'packages/coding-agent/test/pisharp-quiet-startup-differential.test.ts'
    if oracle.exists():
        raise RuntimeError(f'Refusing to overwrite {oracle}')
    shutil.copyfile(probes / 'quiet-startup-probe.pi.test.ts', oracle)
    try:
        environment['PISHARP_QUIET_SCENARIOS'] = encoded
        environment['PISHARP_QUIET_OUTPUT'] = str(work / 'pi.json')
        subprocess.run(['node', str(pi / 'node_modules/vitest/vitest.mjs'), 'run', str(oracle)],
                       cwd=pi / 'packages/coding-agent', env=environment, check=True, timeout=60)
    finally:
        oracle.unlink()
    pi_results = json.loads((work / 'pi.json').read_text())
    assert pi_results == pisharp_results, json.dumps(dict(pi=pi_results, pisharp=pisharp_results), indent=2)
    evidence = dict(
        piSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=pi, text=True).strip(),
        normalization='Exact settings scalars, user/trusted-project precedence, header visibility, preserved JSON writes and ordered supported selector values. Startup body/details text, layout, ANSI and cursor parity are outside this probe.',
        scenarios=scenarios, pi=pi_results, pisharp=pisharp_results, match=True)
    pathlib.Path(args.output).write_text(json.dumps(evidence, indent=2) + '\n')
    print(f'Matched {len(scenarios)} quiet-startup scenarios, three writes and ordered selector values.')
