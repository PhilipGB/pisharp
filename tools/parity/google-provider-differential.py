#!/usr/bin/env python3
"""Compare Google and Vertex image/signature/tool history mapping with pinned Pi."""
import argparse
import json
import os
from pathlib import Path
import shutil
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
pi = args.pi.resolve()
repo = Path(__file__).resolve().parents[2]
probes = Path(__file__).resolve().parent
oracle = pi / 'packages/ai/test/pisharp-google-provider-differential.test.ts'
if oracle.exists():
    raise RuntimeError(f'Refusing to overwrite {oracle}')

cases = [
    {'id': 'genai-gemini-2.5', 'api': 'google-generative-ai', 'provider': 'google', 'model': 'gemini-2.5-flash'},
    {'id': 'genai-gemini-3.5', 'api': 'google-generative-ai', 'provider': 'google', 'model': 'gemini-3.5-flash'},
    {'id': 'genai-gemma-4', 'api': 'google-generative-ai', 'provider': 'google', 'model': 'gemma-4-26b-a4b-it'},
    {'id': 'vertex-gemini-2.5', 'api': 'google-vertex', 'provider': 'google-vertex', 'model': 'gemini-2.5-flash'},
    {'id': 'vertex-gemini-3.5', 'api': 'google-vertex', 'provider': 'google-vertex', 'model': 'gemini-3.5-flash'},
    {'id': 'vertex-gemma-4', 'api': 'google-vertex', 'provider': 'google-vertex', 'model': 'gemma-4-26b-a4b-it'},
]

with tempfile.TemporaryDirectory(prefix='pisharp-google-provider-') as directory:
    work = Path(directory)
    (work / 'Program.cs').write_text((probes / 'google-provider-probe.cs').read_text())
    lightweight_copy = work / 'ExcludeHeavyPackageAssets.targets'
    lightweight_copy.write_text('''<Project>
  <Target Name="ExcludeHeavyPackageAssetsFromLocalCopy" BeforeTargets="_CopyFilesMarkedCopyLocal">
    <ItemGroup>
      <ReferenceCopyLocalPaths Remove="@(ReferenceCopyLocalPaths)" Condition="'%(ReferenceCopyLocalPaths.Extension)' == '.pdb' or $([System.String]::Copy('%(ReferenceCopyLocalPaths.DestinationSubDirectory)').Contains('native'))" />
    </ItemGroup>
  </Target>
</Project>''')
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    environment = dict(os.environ)
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-m:1', '-p:UseSharedCompilation=false',
                            f'-p:CustomAfterMicrosoftCommonTargets={lightweight_copy}'],
                           env=environment, capture_output=True, text=True, timeout=120)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    pisharp = json.loads(subprocess.check_output(
        ['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll')], env=environment, text=True, timeout=30))
    shutil.copyfile(probes / 'google-provider-probe.pi.test.ts', oracle)
    try:
        environment['PISHARP_GOOGLE_PROVIDER_OUTPUT'] = str(work / 'pi.json')
        subprocess.run(['node', str(pi / 'node_modules/vitest/vitest.mjs'), 'run', '--config',
                        str(pi / 'packages/coding-agent/vitest.config.ts'), str(oracle)],
                       cwd=pi / 'packages/ai', env=environment, check=True, timeout=60)
    finally:
        oracle.unlink()
    expected = json.loads((work / 'pi.json').read_text())
    evidence = {
        'piSha': sha,
        'scope': 'Six transcript replay requests cover user images, signed thinking/text/tool-call parts, tool-result images, Gemini 2.5/3.x routing, Gemma 4 routing, and both Google APIs. This captures Google shared message conversion before transport; it does not claim live-provider or HTTP error-string parity.',
        'normalization': 'Compare the complete ordered contents arrays; JSON object key order is ignored by structural comparison.',
        'pi': expected,
        'pisharp': pisharp,
        'match': expected == pisharp,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(evidence, indent=2) + '\n')
    assert evidence['match'], 'Google/Vertex transcript requests differ; inspect ' + str(args.output)
    print(f'Matched {len(cases)} pinned Google/Vertex transcript cases.')
