#!/usr/bin/env python3
from pathlib import Path
import json, os, subprocess, tempfile
import argparse
from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit
apply_test_thread_limit()
parser = argparse.ArgumentParser(description='Compare explicit resource file kinds and path diagnostics with Pi.')
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
if not hasattr(os, 'mkfifo'):
    raise RuntimeError('Named-pipe cases require Unix.')
repo=Path(__file__).resolve().parents[2]; pi=args.pi.resolve();pi_sha=verify_pi_checkout(pi);cases=[]
header='---\nname: guide\ndescription: Guide\n---\nbody'
for kind in ['skill','prompt']:
 for variant in ['normal','alias','wrong-extension','uppercase','missing','fifo','symlink','broken-link','empty-directory']:
  name=('SKILL.md' if kind=='skill' else 'guide.md')
  name={'alias':'alias.md','wrong-extension':'guide.txt','uppercase':'GUIDE.MD'}.get(variant,name)
  cases.append(dict(id=kind+'-'+variant,kind=kind,paths=[name],variant=variant))
cases.append(dict(id='prompt-spaces-name',kind='prompt',paths=['space name.md'],variant='normal'))
with tempfile.TemporaryDirectory(prefix='pisharp-resource-path-oracle-') as directory:
 root=Path(directory)
 (root/'Program.cs').write_text((repo / 'tools/parity/resource-path-probe.cs').read_text())
 (root/'probe.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>')
 env=dict(os.environ,NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
 build=subprocess.run(['dotnet','build',str(root/'probe.csproj'),'--warnaserror','-m:1','-p:UseSharedCompilation=false'],env=env,capture_output=True,text=True,timeout=120)
 if build.returncode:raise RuntimeError(build.stdout+build.stderr)
 for case in cases:
  work=root/case['id'];work.mkdir();target=work/case['paths'][0];variant=case['variant']
  if variant=='fifo':os.mkfifo(target)
  elif variant=='empty-directory':target.mkdir()
  elif variant in ['symlink','broken-link']:
   original=work/'original.txt'
   if variant=='symlink':original.write_text(header)
   target.symlink_to(original)
  elif variant!='missing':target.write_text(header)
 inputs=root/'inputs.json';inputs.write_text(json.dumps(cases))
 expected=json.loads(subprocess.check_output(['node','--experimental-transform-types',str(repo / 'tools/parity/resource-path-probe.mjs'),str(pi),str(inputs),str(root)],env=env,timeout=60))
 actual=[]
 for case in cases:
  inputs.write_text(json.dumps([case]))
  try:
   result=json.loads(subprocess.check_output(['dotnet',str(root/'bin/Debug/net10.0/probe.dll'),str(inputs),str(root)],env=env,timeout=4))[0]
  except subprocess.TimeoutExpired:result=dict(id=case['id'],result=None,error='TimeoutAfter4Seconds')
  actual.append(result)
 differences=[dict(id=p['id'],pi=p,pisharp=n) for p,n in zip(expected,actual,strict=True) if p!=n]
 output=args.output
 output.parent.mkdir(parents=True,exist_ok=True)
 output.write_text(json.dumps(dict(piSha=pi_sha,scenarios=cases,pi=expected,pisharp=actual,differences=differences,match=not differences),indent=2)+'\n')
 assert not differences, json.dumps(differences,indent=2)
 print(f'Matched {len(cases)} explicit resource path scenarios.')
