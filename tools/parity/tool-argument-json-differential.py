#!/usr/bin/env python3
"""Compare Unicode repair, partial prefixes and real provider tool arguments with Pi."""
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
full = [r'{"text":"\url{x}"}', r'{"path":"a.tex","content":"\usepackage{x}","mode":"w"}',
        r'{"text":"caf\u00e9 \url"}', r'{"text":"literal\\usepackage"}',
        r'{"nested":[{"text":"\url{x}"},true,null,3],"after":"present"}']
inputs = list(dict.fromkeys([prefix for value in full for end in range(len(value) + 1) for prefix in [value[:end]]]
    + [r'{"text":"ab\u00', r'{"text":"\uD83D\uDE00"}', '{"text":"line\n\t\0\\\"quoted\\\"","mode":"w"}',
       '{"a":1,"n":1e', '{"a":1,"n":1.', '{"a":tru', '{"a":nul', '{"a":[1,2,']))
with tempfile.TemporaryDirectory(prefix='pisharp-tool-json-differential-') as directory:
    work = Path(directory)
    (work / 'inputs.json').write_text(json.dumps(inputs))
    (work / 'Program.cs').write_text((probes / 'tool-argument-json-probe.cs').read_text())
    fixtures = repo / 'tests/PiSharp.Tests/TestInfrastructure'
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" />
<Compile Include="{fixtures}/ProviderToolArgumentServer.cs" /><Compile Include="{fixtures}/TestLoopbackListener.cs" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-m:1', '-p:UseSharedCompilation=false'],
                   env=env, check=True, stdout=subprocess.DEVNULL, timeout=120)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'), str(work / 'inputs.json')],
                                              env=env, timeout=60))
    (work / 'native.json').write_text(json.dumps(actual['native']))
    expected = json.loads(subprocess.check_output(['node', str(probes / 'tool-argument-json-probe.mjs'), str(args.pi),
                                                 str(work / 'inputs.json'), str(work / 'native.json')], env=env, timeout=60))
    for fixture in actual['native']:
        del fixture['wire']
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, pi=expected, pisharp=actual, match=True,
        comparison='Exact repair strings and JSON values across captured prefixes; completed arguments and error presence/raw-input retention through real Pi provider adapters and native PiSharp SDKs. Pi HTTP is an injected fetch; PiSharp uses loopback HTTP. Exception wording, depths above 64, non-JSON numeric values/unsafe integer rounding, browser services and full event envelopes are not covered.'), indent=2) + '\n')
    print(f'Matched {len(inputs)} parser prefixes and {len(actual["native"])} native provider tool calls.')
