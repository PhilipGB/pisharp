#!/usr/bin/env python3
"""Compare paid classifier calls inside current Pi and PiSharp Codemode sandboxes."""
import argparse
import json
import os
import pathlib
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--pi', required=True, help='Current Pi checkout')
parser.add_argument('--output', required=True)
args = parser.parse_args()
repo = pathlib.Path(__file__).resolve().parents[2]
probes = pathlib.Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(prefix='pisharp-codemode-parity-') as directory:
    work = pathlib.Path(directory)
    (work / 'Program.cs').write_text((probes / 'codemode-model-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>''')
    environment = dict(os.environ)
    environment.setdefault('NUGET_PACKAGES', '/tmp/pisharp-parity-nuget')
    environment.setdefault('NUGET_HTTP_CACHE_PATH', '/tmp/pisharp-nuget-http')
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-p:UseSharedCompilation=false', '-m:1'],
                   env=environment, check=True, stdout=subprocess.DEVNULL, timeout=120)
    evidence = {'cases': []}
    for name, fail in [('success', 'false'), ('failure', 'true')]:
        pi = json.loads(subprocess.check_output(['node', str(probes / 'codemode-model-probe.mjs'), args.pi, fail], timeout=25))
        pisharp = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/probe.dll'), fail], timeout=25))
        assert pi == pisharp, (name, pi, pisharp)
        evidence['cases'].append({'case': name, 'match': True, 'pi': pi, 'pisharp': pisharp})
    pathlib.Path(args.output).write_text(json.dumps(evidence, indent=2) + '\n')
