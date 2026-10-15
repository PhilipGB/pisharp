#!/usr/bin/env python3
"""Compare PiSharp's shared thinking-budget math with pinned Pi's provider helper."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

from pi_reference import verify_pi_checkout

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
probes = Path(__file__).resolve().parent
cases = [
    dict(id='low-custom-expands-output', baseMaxTokens=4096, modelMaxTokens=20000,
         level='low', budgets={'low': 3000}),
    dict(id='high-custom-respects-model-ceiling', baseMaxTokens=4096, modelMaxTokens=12000,
         level='high', budgets={'high': 12000}),
    dict(id='xhigh-uses-high-custom-budget', baseMaxTokens=2048, modelMaxTokens=30000,
         level='xhigh', budgets={'high': 15000}),
    dict(id='uncapped-low-uses-model-ceiling', baseMaxTokens=None, modelMaxTokens=8192,
         level='low', budgets={'low': 5000}),
    dict(id='small-ceiling-preserves-answer-room', baseMaxTokens=1024, modelMaxTokens=3000,
         level='high', budgets={'high': 16384}),
]

with tempfile.TemporaryDirectory(prefix='pisharp-thinking-budget-') as directory:
    work = Path(directory)
    inputs = work / 'cases.json'
    inputs.write_text(json.dumps(cases))
    (work / 'Program.cs').write_text((probes / 'thinking-budget-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.ThinkingBudgetProbe</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ)
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1',
                           '-p:UseSharedCompilation=false'], env=env, capture_output=True, text=True, timeout=180)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    actual = json.loads(subprocess.check_output(
        ['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.ThinkingBudgetProbe.dll'), str(inputs)],
        env=env, text=True, timeout=30))
    expected = json.loads(subprocess.check_output(
        ['node', str(probes / 'thinking-budget-probe.mjs'), str(args.pi.resolve()), str(inputs)],
        env=env, text=True, timeout=30))
    result = dict(piSha=sha, cases=cases, pi=expected, pisharp=actual, exactMatch=actual == expected,
        scope='Five provider-helper cases compare custom low/high budgets, xhigh-to-high mapping, output expansion under model ceilings, uncapped requests, and the reserved 1024-token answer room. This does not claim full provider request or live service parity.')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    print(f'Matched Pi and PiSharp thinking-budget adjustment in {len(cases)} cases.')
