#!/usr/bin/env python3
"""Compare force-OSC52 behavior and native-copy ordering with real current Pi code."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
probes = Path(__file__).resolve().parent
scenarios = [dict(platform=platform, force=force) for platform in ['darwin', 'win32', 'linux']
             for force in ['1', '0', 'true', '']]
with tempfile.TemporaryDirectory(prefix='pisharp-clipboard-force-') as directory:
    work = Path(directory)
    (work / 'Program.cs').write_text((probes / 'clipboard-force-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-m:1', '-p:UseSharedCompilation=false'],
                   env=env, check=True, stdout=subprocess.DEVNULL, timeout=120)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'),
                                               json.dumps(scenarios)], env=env, timeout=20))
    oracle = args.pi / 'packages/coding-agent/test/pisharp-clipboard-force.test.ts'
    if oracle.exists(): raise RuntimeError(f'Refusing to overwrite {oracle}')
    shutil.copyfile(probes / 'clipboard-force-probe.pi.test.ts', oracle)
    try:
        env.update(PISHARP_CLIPBOARD_SCENARIOS=json.dumps(scenarios), PISHARP_CLIPBOARD_OUTPUT=str(work / 'pi.json'))
        subprocess.run(['node', str(args.pi / 'node_modules/vitest/vitest.mjs'), 'run', str(oracle.resolve())],
                       cwd=args.pi / 'packages/coding-agent', env=env, check=True, timeout=60)
    finally: oracle.unlink()
    expected = json.loads((work / 'pi.json').read_text())
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, scenarios=scenarios, pi=expected, pisharp=actual,
        match=True, comparison='Exact successful-native-write/OSC52 order and UTF-8 terminal payload; platform and native writer are injected in both real implementations. Covers only exact force value 1 and negative controls; no real OS clipboard or remote-terminal availability claim.'), indent=2) + '\n')
    print(f'Matched {len(scenarios)} clipboard force/order cases.')
