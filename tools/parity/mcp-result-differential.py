#!/usr/bin/env python3
"""Compare native MCP result values and resource-list demand at the configured Pi reference."""
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
sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
probes = Path(__file__).resolve().parent
scenarios = [dict(json=value, omitContent=False) for value in ['[1,2]', 'null', '"text"', '3', 'false']]
scenarios += [dict(json=value, omitContent=True) for value in ['[1,2]', 'null']]
with tempfile.TemporaryDirectory(prefix='pisharp-mcp-result-differential-') as directory:
    work = Path(directory)
    (work / 'input.json').write_text(json.dumps(scenarios))
    (work / 'Program.cs').write_text((probes / 'mcp-result-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-m:1', '-p:UseSharedCompilation=false'],
                   env=env, check=True, stdout=subprocess.DEVNULL, timeout=120)
    parameters = [str(work), str(work / 'input.json'), str(repo / 'tests/PiSharp.Tests/TestInfrastructure/Fixtures/mcp_fixture.py')]
    expected = json.loads(subprocess.check_output(['node', str(probes / 'mcp-result-probe.mjs'), str(args.pi),
                                                 *parameters], env=env, timeout=60))
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'),
                                               *parameters], env=env, timeout=90))
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    for result in actual:
        assert result['atConnection'] == dict(tools=1, resources=0, templates=0, calls=0)
        assert result['afterDemand'] == dict(tools=1, resources=1, templates=1, calls=1)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, scenarios=scenarios, pi=expected, pisharp=actual,
        match=True, comparison='Exact model text, complete script-visible JSON result (including explicit null and default content), error state and request counts at connection and after demand. Wire IDs, protocol negotiation probes, temporary paths and timings are excluded; no credential/live-service coverage.'), indent=2) + '\n')
    print(f'Matched {len(scenarios)} real-stdio result and on-demand resource scenarios.')
