#!/usr/bin/env python3
"""Compare termination requests before result/cleanup scheduling with pinned Pi."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

from pi_reference import verify_pi_checkout

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    sha = verify_pi_checkout(args.pi)
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http',
               NUGET_PACKAGES='/tmp/pisharp-nuget-packages')
    with tempfile.TemporaryDirectory(prefix='pisharp-codemode-shutdown-') as directory:
        work = Path(directory)
        (work / 'Program.cs').write_text((ROOT / 'tools/parity/codemode-shutdown-probe.cs').read_text())
        (work / 'probe.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{ROOT}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>')
        subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1',
            '-p:UseSharedCompilation=false', f'-p:RestoreFallbackFolders={Path.home() / ".nuget/packages"}'],
            env=env, check=True, stdout=subprocess.DEVNULL, timeout=120)
        actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll')],
            env=env, timeout=15))
    expected = json.loads(subprocess.check_output(['node', str(ROOT / 'tools/parity/codemode-shutdown-probe.mjs'),
        str(args.pi.resolve())], env=env, timeout=15))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, pi=expected, pisharp=actual, match=actual == expected,
        scope='Termination is requested before Pi returns a normal guest result and before native detached cleanup can be scheduled. Native holds its only cleanup-pool thread behind a barrier and observes a real child exit; source observes the real Worker.terminate request before execute resolves.',
        limits='This proves request ordering, not an obligation to wait for actual exit before settlement, and does not reproduce the exact historical CI scheduling. Existing worker-admission and cancellation comparisons remain separate.'), indent=2) + '\n')
    assert actual == expected and expected['terminationRequestedBeforePoolAvailable']
    print('Matched immediate Codemode termination request boundary.')


if __name__ == '__main__':
    main()
