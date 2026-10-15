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
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--ordering', action='store_true', help='Compare a retained personal module and a new project module.')
    mode.add_argument('--conflicts', action='store_true', help='Compare tool/command winners and replaceable builtins.')
    args = parser.parse_args()
    sha = verify_pi_checkout(args.pi)
    cache_root = Path(os.environ.get('PISHARP_PARITY_NUGET_ROOT', '/tmp/pisharp-sol-orchestration-20261015/root-profile'))
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH=str(cache_root / 'nuget-http'), NUGET_PACKAGES=str(cache_root / 'nuget'))
    with tempfile.TemporaryDirectory(prefix='pisharp-extension-pretrust-') as directory:
        work = Path(directory)
        (work / 'Program.cs').write_text((ROOT / 'tools/parity/extension-pretrust-selection-probe.cs').read_text())
        refs = [ROOT / 'src/PiSharp.Runtime/PiSharp.Runtime.csproj', ROOT / 'tests/ResourceDiscoveryExtension/ResourceDiscoveryExtension.csproj']
        (work / 'probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>' + ''.join(f'<ProjectReference Include="{ref}" />' for ref in refs) + '</ItemGroup></Project>')
        build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1',
            '-p:UseSharedCompilation=false', f'-p:RestoreFallbackFolders={Path.home() / ".nuget/packages"}'],
            env=env, check=False, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=120)
        if build.returncode:
            raise RuntimeError(build.stdout)
        extra = ['--conflicts'] if args.conflicts else ['--ordering'] if args.ordering else []
        actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/probe.dll'), str(work)] + extra, env=env, timeout=30))
        expected = json.loads(subprocess.check_output(['node', '--experimental-transform-types', str(ROOT / 'tools/parity/extension-pretrust-selection-probe.mjs'), str(args.pi.resolve()), str(work)] + extra, env=env, timeout=30))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    receipt = dict(piSha=sha, pi=expected, pisharp=actual, match=actual == expected,
        scope=('Actual DefaultResourceLoader conflict selection/ExtensionRunner command resolution versus ExtensionCatalog: '
            'compares loaded owners, first tool winner, duplicate-command aliases/descriptions, conflict diagnostics and '
            'replaceable-builtin warnings; temporary fixture paths normalize to inline owner labels.' if args.conflicts else
            'Actual DefaultResourceLoader.reload(resolveProjectTrust) versus bootstrap/final ExtensionCatalog selection: '
            'selected project package cases compare bootstrap count, configuration trace, errors, final loaded modules and '
            'all five source-info fields. Only generated root paths and .ts/.dll suffixes normalize.'))
    args.output.write_text(json.dumps(receipt, indent=2) + '\n')
    assert actual == expected
    print('Matched Pi and PiSharp extension conflict winners, command aliases and diagnostics.' if args.conflicts else
        f'Matched {len(actual)} full-loader pre-trust extension selection/source metadata cases.')


if __name__ == '__main__':
    main()
