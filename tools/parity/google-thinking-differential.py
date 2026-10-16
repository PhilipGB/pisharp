#!/usr/bin/env python3
"""Compare Google/Vertex thinking controls at the configured Pi source boundary."""
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
cases = [dict(model=model, level=level, vertex=vertex, budgets=None)
         for model, level in [('gemini-4-argon', 'high'), ('gemini-4-argon', 'off'),
                              ('gemini-3.5-flash', 'low'), ('gemini-2.5-flash', 'high')]
         for vertex in [False, True]]
cases.extend(dict(model='gemini-2.5-flash', level=level, vertex=vertex, budgets={level: budget})
             for level, budget in [('low', 4096), ('high', 6144)] for vertex in [False, True])
with tempfile.TemporaryDirectory(prefix='pisharp-google-thinking-') as directory:
    work = Path(directory)
    (work / 'inputs.json').write_text(json.dumps(cases))
    (work / 'Program.cs').write_text((probes / 'google-thinking-probe.cs').read_text())
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
    env = dict(os.environ)
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-m:1', '-p:UseSharedCompilation=false',
                            f'-p:CustomAfterMicrosoftCommonTargets={lightweight_copy}'],
                           env=env, capture_output=True, text=True, timeout=120)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'),
                                                str(work / 'inputs.json')], env=env, timeout=30))
    expected = json.loads(subprocess.check_output(['node', str(probes / 'google-thinking-probe.mjs'),
                                                  str(args.pi), str(work / 'inputs.json')], env=env, timeout=30))
    differences = [dict(pi=left, pisharp=right) for left, right in zip(expected, actual) if left != right]
    assert len(actual) == len(expected)
    assert len(differences) == 2 and all(item['pi']['level'] == 'off' for item in differences), json.dumps(
        dict(pi=expected, pisharp=actual), indent=2)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=sha, pi=expected, pisharp=actual, match=False,
        matchedConfigurations=len(cases) - len(differences), deferredDifferences=differences,
        deferredPriority='P2', deferredReason='Thinking off uses includeThoughts:false/thinkingLevel:MINIMAL in PiSharp, while this Pi model without a level map sends thinkingBudget:0. Enabled controls match; off mapping remains provider feature work.',
        scope='Twelve Google and Vertex thinking controls, including four custom low/high token budgets, captured before network I/O. Ten enabled controls match and two off controls retain an explicit P2 difference. Native tests cover SDK request construction; no live provider, Google auth, full body or streaming response parity claim.'), indent=2) + '\n')
    print(f'Matched {len(cases) - len(differences)} enabled controls; retained {len(differences)} P2 off-control differences.')
