#!/usr/bin/env python3
"""Compare current Pi and PiSharp system-theme ANSI colors."""
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

frappe = ['#51576d', '#e78284', '#a6d189', '#e5c890', '#8caaee', '#f4b8e4', '#81c8be', '#b5bfe2',
          '#626880', '#e67172', '#8ec772', '#d9ba73', '#7b9ef0', '#f2a4db', '#5abfb5', '#a5adce']
latte = ['#5c5f77', '#d20f39', '#40a02b', '#df8e1d', '#1e66f5', '#ea76cb', '#179299', '#acb0be',
         '#6c6f85', '#e64553', '#40a02b', '#df8e1d', '#1e66f5', '#ea76cb', '#179299', '#bcc0cc']
dracula = ['#21222c', '#ff5555', '#50fa7b', '#f1fa8c', '#bd93f9', '#ff79c6', '#8be9fd', '#f8f8f2',
           '#6272a4', '#ff6e6e', '#69ff94', '#ffffa5', '#d6acff', '#ff92df', '#a4ffff', '#ffffff']
scenarios = [
    dict(id='frappe', background='#303446', foreground='#c6d0f5', palette=frappe),
    dict(id='latte', background='#eff1f5', foreground='#4c4f69', palette=latte),
    dict(id='dracula', background='#282a36', foreground='#f8f8f2', palette=dracula),
    dict(id='solarized-light', background='#fdf6e3', foreground='#657b83'),
    dict(id='background-only', background='#1e1e1e'),
    dict(id='mid-gray', background='#808080', foreground='#ffffff'),
    dict(id='grayscale', background='#303030', foreground='#c0c0c0', palette=['#808080'] * 16),
    dict(id='indexed-dark'), dict(id='indexed-light', appearance='light')]

scenarios = [dict(scenario, mode=mode, id=scenario['id'] + '-' + mode)
             for scenario in scenarios for mode in ('truecolor', '256color')]

with tempfile.TemporaryDirectory(prefix='pisharp-theme-differential-') as directory:
    work = pathlib.Path(directory)
    (work / 'Program.cs').write_text((probes / 'system-theme-probe.cs').read_text())
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
    oracle = pi / 'packages/coding-agent/test/pisharp-theme-differential.test.ts'
    if oracle.exists():
        raise RuntimeError(f'Refusing to overwrite {oracle}')
    shutil.copyfile(probes / 'system-theme-probe.pi.test.ts', oracle)
    try:
        environment['PISHARP_THEME_SCENARIOS'] = encoded
        environment['PISHARP_THEME_OUTPUT'] = str(work / 'pi.json')
        subprocess.run(['node', str(pi / 'node_modules/vitest/vitest.mjs'), 'run', str(oracle)],
                       cwd=pi / 'packages/coding-agent', env=environment, check=True, timeout=60)
    finally:
        oracle.unlink()
    pi_results = json.loads((work / 'pi.json').read_text())
    assert pi_results == pisharp_results, json.dumps(dict(pi=pi_results, pisharp=pisharp_results), indent=2)
    evidence = dict(
        piSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=pi, text=True).strip(),
        normalization='Compare exact ANSI color prefixes for every foreground/background role plus appearance; no color or style normalization. This fixture does not compare component layout or text reset sequences.',
        scenarios=scenarios, pi=pi_results, pisharp=pisharp_results, match=True)
    pathlib.Path(args.output).write_text(json.dumps(evidence, indent=2) + '\n')
    print(f'Matched {len(scenarios)} system-theme palettes.')
