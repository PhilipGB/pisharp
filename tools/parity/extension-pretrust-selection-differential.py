#!/usr/bin/env python3
"""Compare pre-trust package extension selection and metadata with Pi's full loader."""
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
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http', NUGET_PACKAGES='/tmp/pisharp-nuget-packages')
    with tempfile.TemporaryDirectory(prefix='pisharp-extension-pretrust-') as directory:
        work = Path(directory)
        (work / 'Program.cs').write_text((ROOT / 'tools/parity/extension-pretrust-selection-probe.cs').read_text())
        refs = [ROOT / 'src/PiSharp.Runtime/PiSharp.Runtime.csproj', ROOT / 'tests/ResourceDiscoveryExtension/ResourceDiscoveryExtension.csproj']
        (work / 'probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>' + ''.join(f'<ProjectReference Include="{ref}" />' for ref in refs) + '</ItemGroup></Project>')
        subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1',
            '-p:UseSharedCompilation=false', f'-p:RestoreFallbackFolders={Path.home() / ".nuget/packages"}'],
            env=env, check=True, stdout=subprocess.DEVNULL, timeout=120)
        actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/probe.dll'), str(work)], env=env, timeout=30))
        expected = json.loads(subprocess.check_output(['node', '--experimental-transform-types', str(ROOT / 'tools/parity/extension-pretrust-selection-probe.mjs'), str(args.pi.resolve()), str(work)], env=env, timeout=30))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, pi=expected, pisharp=actual, match=actual == expected,
        scope='Actual DefaultResourceLoader.reload(resolveProjectTrust) versus bootstrap/final ExtensionCatalog selection: disabled/enabled/untrusted project package cases compare bootstrap count, configuration trace, errors, final loaded modules and all five source-info fields. Only generated root paths and .ts/.dll suffixes normalize.'), indent=2) + '\n')
    assert actual == expected
    print('Matched three full-loader pre-trust extension selection/source metadata cases.')


if __name__ == '__main__':
    main()
