#!/usr/bin/env python3
"""Compare focused-overlay wheel routing in a real PiSharp PTY and Pi's native virtual terminal."""
import argparse
import json
import os
from pathlib import Path
import shlex
import subprocess
import tempfile

from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--pty-capture', type=Path)
args = parser.parse_args()
sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
probes = Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(prefix='pisharp-overlay-wheel-differential-') as directory:
    work = Path(directory)
    (work / 'Program.cs').write_text((probes / 'overlay-wheel-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http', TERM='xterm-256color')
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-m:1', '-p:UseSharedCompilation=false'],
                           env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=120)
    if build.returncode:
        print(build.stdout)
    build.check_returncode()
    command = shlex.join(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll')])
    raw = subprocess.check_output(['script', '-q', '-e', '-c', command, '/dev/null'], env=env, timeout=30)
    if args.pty_capture:
        args.pty_capture.parent.mkdir(parents=True, exist_ok=True)
        args.pty_capture.write_bytes(raw)
    actual = json.loads(raw.decode().split('PISHARP_OVERLAY_WHEEL=', 1)[1].splitlines()[0])
    expected = json.loads(subprocess.check_output(['node', str(probes / 'overlay-wheel-probe.mjs'), str(args.pi)],
                                                env=env, timeout=30))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, pi=expected, pisharp=actual, match=actual == expected,
        comparison='Compare upward transcript movement for five keyboard-only modal hosts, batched extension input, an empty selector and a mouse-aware selector. PiSharp invokes real dialogs/progress/search under a PTY; Pi invokes TuiAltScreen/VirtualTerminal with focused mouse-aware and unaware components. Typed text and selection/progress outcomes are asserted in PiSharp, and wheel never reaches Pi keyboard input. Full cell layout, pointer selection, selector movement increments, keyboard viewport key handling and non-POSIX hosts are not covered.'), indent=2) + '\n')
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    print('Matched focused modal and mouse-aware wheel routing.')
