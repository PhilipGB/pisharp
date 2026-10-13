#!/usr/bin/env python3
"""Compare resumed and initial tool selection through real Pi/PiSharp session composition."""
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
scenarios = ['default', 'settings', 'explicit', 'modifiers', 'excluded', 'disabled',
             'fresh', 'fresh-modifiers', 'empty', 'update']
with tempfile.TemporaryDirectory(prefix='pisharp-tool-resume-differential-') as directory:
    work = Path(directory)
    (work / 'Program.cs').write_text((probes / 'tool-resume-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-m:1', '-p:UseSharedCompilation=false'],
                   env=env, check=True, stdout=subprocess.DEVNULL, timeout=120)
    encoded = json.dumps(scenarios)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'), encoded],
                                              env=env, timeout=60))
    expected = json.loads(subprocess.check_output(['node', str(probes / 'tool-resume-probe.mjs'), str(args.pi), encoded],
                                                env=env, timeout=60))
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, pi=expected, pisharp=actual, match=True,
        comparison='Exact active name sets for ten real SDK/session-composition scenarios. Tool declaration order, pattern allowlists, provider requests, late registration and unrelated tool availability are not established by this slice.'), indent=2) + '\n')
    print('Matched all ten tool-selection and resume scenarios.')
