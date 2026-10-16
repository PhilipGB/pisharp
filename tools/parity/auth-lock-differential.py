#!/usr/bin/env python3
"""Compare native auth locking with pinned Pi's Windows retry and cross-process contracts."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time

from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
pi_sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
cases = [dict(id=identifier, platform=platform, codes=codes, cancel=cancel) for identifier, platform, codes, cancel in [
    ('transient-windows-permission', 'win32', ['EPERM'], None),
    ('persistent-windows-permission', 'win32', ['EPERM'] * 20, None),
    ('linux-permission', 'linux', ['EPERM'], None),
    ('linux-contention', 'linux', ['ELOCKED'], None),
    ('mixed-contention-permission', 'win32', ['ELOCKED', 'EPERM'] * 5, None),
    ('missing-file', 'win32', ['ENOENT'], None),
    ('disk-error', 'win32', ['ENOSPC'], None),
    ('already-canceled', 'win32', [], 'before'),
    ('cancel-after-rejection', 'win32', ['EPERM'], 'rejection'),
    ('cancel-after-acquisition', 'win32', [], 'acquired')]]
env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')


def contention(commands, directory):
    path = str(directory / 'auth.json')
    holder = subprocess.Popen([*commands, 'hold', path], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE, text=True)
    writer = None
    try:
        assert holder.stdout.readline().strip() == 'ready'
        writer = subprocess.Popen([*commands, 'write', path], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        assert writer.stdout.readline().strip() == 'writing'
        time.sleep(0.15)
        blocked = writer.poll() is None
        holder.communicate('release\n', timeout=10)
        stdout, stderr = writer.communicate(timeout=10)
        assert holder.returncode == writer.returncode == 0, stderr
        return dict(blockedUntilRelease=blocked, providers=json.loads(stdout), holderExited=True, writerExited=True)
    finally:
        for process in [holder, writer]:
            if process is not None and process.poll() is None:
                process.kill()
                process.communicate()


with tempfile.TemporaryDirectory(prefix='pisharp-auth-lock-') as directory:
    work = Path(directory)
    inputs = work / 'cases.json'
    inputs.write_text(json.dumps(cases))
    (work / 'Program.cs').write_text((repo / 'tools/parity/auth-lock-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'],
                           env=env, capture_output=True, text=True, timeout=120)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    native = ['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll')]
    oracle = ['node', str(repo / 'tools/parity/auth-lock-probe.mjs'), str(args.pi.resolve())]
    actual = json.loads(subprocess.check_output([*native, str(inputs)], env=env, timeout=30))
    expected = json.loads(subprocess.check_output([*oracle, str(inputs)], env=env, timeout=30))
    (work / 'native').mkdir()
    (work / 'pi').mkdir()
    native_contention = contention(native, work / 'native')
    pi_contention = contention(oracle, work / 'pi')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=pi_sha, cases=cases, pi=expected, pisharp=actual,
        piContention=pi_contention, pisharpContention=native_contention,
        match=expected == actual and pi_contention == native_contention,
        comparison='Exact async attempt/update/release counts and deterministic backoff delays for simulated Windows permission errors, persistent/non-Windows errors, ordinary contention, mixed counters and cancellation. Real separate processes verify writes wait for another holder and preserve both provider entries.',
        limitations='Windows platform/error inputs are injected like Pi source tests; a real Windows delete-pending filesystem is unavailable. Pi uses proper-lockfile directories; PiSharp uses persistent private native FileShare.None leases and separate process gates. Both async policy contracts match; PiSharp exposes no synchronous auth-storage API. Cross-language simultaneous locking of the same auth file, lease timeout wording and wider auth refresh/reload semantics remain open.'), indent=2) + '\n')
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    assert native_contention == pi_contention and native_contention['blockedUntilRelease'], json.dumps(dict(pi=pi_contention, pisharp=native_contention), indent=2)
    print(f'Matched {len(cases)} auth-lock policy vectors and real cross-process contention.')
