#!/usr/bin/env python3
"""Compare real Codemode cancellation and shared worker admission at the pinned Pi source."""
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
pi_sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
with tempfile.TemporaryDirectory(prefix='pisharp-codemode-lifetime-') as directory:
    work = Path(directory)
    (work / 'Program.cs').write_text((repo / 'tools/parity/codemode-lifetime-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>''')
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'],
                   env=env, check=True, stdout=subprocess.DEVNULL, timeout=120)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll')],
                                                env=env, timeout=30))
    expected = json.loads(subprocess.check_output(['node', str(repo / 'tools/parity/codemode-lifetime-probe.mjs'),
                                                  str(args.pi.resolve())], env=env, timeout=30))
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    assert all(result.get('settledPromptly', True) and result.get('exitedPromptly', True) for result in actual)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=pi_sha, pi=expected, pisharp=actual, match=True,
        comparison='Semantic match: real abort/timeout results settle within 1 second after host entry, preserve text, cancel the host token and discard writes; non-interruptible string searches settle within 1 second of abort/the 2-second deadline and exit within 1 second of settlement; two concurrent sessions share capacity, already-aborted admission wins, and held actual-exit signals release replacement capacity.',
        limitations='This probe compares lifecycle predicates, not output envelopes or exact elapsed times. Pi Worker.terminate and the C# exit lease use held exit signals only in the exit-accounting case; abort/timeout and shared-admission use real workers/processes. The .NET execution is one-shot; session/run close cancels its invocation token. Bun process behavior is unavailable. Existing Codemode output formatting, other options/deadlines, memory ceilings, nested-call details and model globals remain separately ledgered.'), indent=2) + '\n')
    print(f'Matched {len(actual)} Codemode lifecycle/admission cases.')
