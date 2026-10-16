#!/usr/bin/env python3
"""Compare canceled requests during real Pi/PiSharp MCP OAuth recovery."""
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
with tempfile.TemporaryDirectory(prefix='pisharp-mcp-oauth-differential-') as directory:
    work = Path(directory)
    (work / 'Program.cs').write_text((probes / 'mcp-oauth-cancellation-probe.cs').read_text())
    fixtures = repo / 'tests/PiSharp.Tests/TestInfrastructure'
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" />
<Compile Include="{fixtures}/McpOAuthRecoveryServer.cs" /><Compile Include="{fixtures}/TestLoopbackListener.cs" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-m:1', '-p:UseSharedCompilation=false'],
                   env=env, check=True, stdout=subprocess.DEVNULL, timeout=120)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll')],
                                              env=env, timeout=30))
    expected = json.loads(subprocess.check_output(['node', str(probes / 'mcp-oauth-cancellation-probe.mjs'), str(args.pi)],
                                                env=env, timeout=30))
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    assert actual['settledBeforeRelease'] == [True, True] and actual['executed'] == ['kept']
    assert actual['refreshes'] == 1 and actual['bindingPreserved']
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, pi=expected, pisharp=actual, match=True,
        comparison='Real loopback MCP and OAuth grants using production auth providers/transports. Compare settlement before gate release, executed calls, surviving result, grant count and token/client preservation. Raw request IDs, timestamps, endpoint ports and exception type spelling differ and are excluded; close-during-refresh, browser sign-in and live identity services are not covered.'), indent=2) + '\n')
    print('Matched canceled/deadline/surviving calls across one real OAuth refresh.')
