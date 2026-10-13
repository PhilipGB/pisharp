#!/usr/bin/env python3
"""Compare Codemode row timing checkpoints and formatting against pinned Pi."""
import argparse
import json
import os
import shlex
from pathlib import Path
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
pi_sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
durations = [0, 1, 49, 50, 149, 150, 250, 1050, 1150, 1250, 2050, 2550, 3000, 5100,
             59949, 59950, 59999, 60000, 60100, 90000, 3599999, 3600000, 7384000,
             1e-9, 2.2250738585072014e-308]
durations.extend(value for index in range(1, 60) for value in [index * 1000 + 50, index * 1000 + 150])
env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
with tempfile.TemporaryDirectory(prefix='pisharp-codemode-timer-') as directory:
    work = Path(directory)
    inputs = work / 'durations.json'
    inputs.write_text(json.dumps(durations))
    (work / 'Program.cs').write_text((repo / 'tools/parity/codemode-timer-probe.cs').read_text())
    for name in ['ManualTimeProvider', 'TerminalOutputFrameReader']:
        (work / f'{name}.cs').write_text((repo / f'tests/PiSharp.Tests/TestInfrastructure/{name}.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'],
                           env=env, capture_output=True, text=True, timeout=120)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'), str(inputs)], env=env, timeout=30))
    expected = json.loads(subprocess.check_output(['node', str(repo / 'tools/parity/codemode-timer-probe.mjs'),
                                                  str(args.pi.resolve()), str(inputs)], env=env, timeout=30))
    (work / 'Program.cs').write_text((repo / 'tools/parity/codemode-timer-live-probe.cs').read_text())
    live_build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--no-restore', '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'],
                                env=env, capture_output=True, text=True, timeout=120)
    if live_build.returncode:
        raise RuntimeError(live_build.stdout + live_build.stderr)
    command = shlex.join(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll')])
    raw_pty = subprocess.check_output(['script', '-q', '-e', '-c', command, '/dev/null'],
                                      env=dict(env, TERM='xterm-256color'), timeout=30)
    if args.pty_capture:
        args.pty_capture.parent.mkdir(parents=True, exist_ok=True)
        args.pty_capture.write_bytes(raw_pty)
    live_actual = json.loads(raw_pty.decode().split('PISHARP_CODEMODE_TIMER=', 1)[1].splitlines()[0])
    live_expected = json.loads(subprocess.check_output(['node', str(repo / 'tools/parity/codemode-timer-live-probe.mjs'),
                                                       str(args.pi.resolve())], env=env, timeout=30))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=pi_sha, durations=durations, pi=expected, pisharp=actual,
        livePi=live_expected, livePiSharp=live_actual,
        match=actual == expected and live_actual == live_expected,
        comparison='Exact duration text and named timer checkpoints from real Pi ToolExecutionComponent/codemode renderer and PiSharp TerminalScreen frame decoding. Injected clocks verify initial progress, autonomous redraw, recorded duration overriding the live clock, settled redraw cleanup, reload and partial-error freeze. A real PiSharp MAF/Codemode worker run under a PTY matches Pi runToolCall/codemode execution and renderer predicates for initial progress before nested entry, live one-second redraw and final recorded timing.',
        limitations='Timer text/state are compared; complete row layout, script preview, nested-call/cost rendering, other tool renderers and platform terminal behavior remain open. Initial progress and monotonic persisted runtime duration are separately covered by real Codemode agent-process tests.'), indent=2) + '\n')
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    assert live_actual == live_expected and all(live_actual.values()), json.dumps(dict(pi=live_expected, pisharp=live_actual), indent=2)
    print(f'Matched {len(durations)} duration vectors, live/final/reload timer checkpoints and real-worker PTY predicates.')
