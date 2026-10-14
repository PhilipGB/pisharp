#!/usr/bin/env python3
from pathlib import Path
import json, subprocess, tempfile, os
import argparse
from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit
apply_test_thread_limit()
parser=argparse.ArgumentParser(description='Compare real skill/template YAML metadata and per-file parse recovery with Pi.')
parser.add_argument('--pi',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args()
repo=Path(__file__).resolve().parents[2]; pi=args.pi.resolve();pi_sha=verify_pi_checkout(pi)
vectors={
'normal':'name: guide\ndescription: Guide',
'inline-comment':'name: guide\ndescription: Guide # comment',
'double-escapes':'name: guide\ndescription: "Line\\nnext"',
'single-quote-escape':"name: guide\ndescription: 'It''s a guide'",
'plain-multiline':'name: guide\ndescription: First\n  second',
'flow-map':'{name: guide, description: Guide}',
'anchor':'name: guide\nvalue: &shared Guide\ndescription: *shared',
'literal':'name: guide\ndescription: |\n  First\n  second',
'folded':'name: guide\ndescription: >\n  First\n  second',
'explicit-indent':'name: guide\ndescription: |2-\n    Indented',
'duplicate':'name: guide\ndescription: Guide\ndescription: Later',
'invalid-flow':'name: guide\ndescription: Guide\nunused: [invalid',
'unknown-nested-duplicate':'name: guide\ndescription: Guide\nunused:\n  one: a\n  one: b',
'description-boolean':'name: guide\ndescription: true',
'description-number':'name: guide\ndescription: 123',
'description-null':'name: guide\ndescription: null',
'description-sequence':'name: guide\ndescription: [Guide]',
'description-map':'name: guide\ndescription: {one: Guide}',
'name-boolean':'name: true\ndescription: Guide',
'name-number':'name: 123\ndescription: Guide',
'name-sequence':'name: [guide]\ndescription: Guide',
'quoted-invocation':'name: guide\ndescription: Guide\ndisable-model-invocation: "true"',
'boolean-invocation':'name: guide\ndescription: Guide\ndisable-model-invocation: true',
'yes-is-string':'name: guide\ndescription: yes',
'on-is-string':'name: guide\ndescription: on',
'explicit-string':'name: guide\ndescription: !!str true',
'explicit-boolean':'name: guide\ndescription: Guide\ndisable-model-invocation: !!bool true',
'case-sensitive-key':'name: guide\nDescription: Guide',
'empty':'',
'comment-only':'# comment',
'root-scalar':'just text',
'root-sequence':'[one, two]',
'float':'name: guide\ndescription: 1.2',
'exponent':'name: guide\ndescription: 1e3',
'hex':'name: guide\ndescription: 0x10',
'leading-zero':'name: guide\ndescription: 0123',
'quoted-number':'name: guide\ndescription: "123"',
'colon-description':'name: guide\ndescription: "Guide: details"',
'literal-keep':'name: guide\ndescription: |+\n  First\n\n',
'literal-strip':'name: guide\ndescription: |-\n  First\n\n',
'folded-keep':'name: guide\ndescription: >+\n  First\n  second\n\n',
'bool-upper-invocation':'name: guide\ndescription: Guide\ndisable-model-invocation: True',
'mixed-bool-is-string':'name: guide\ndescription: tRuE',
'empty-literal':'name: guide\ndescription: |',
'empty-quoted':'name: guide\ndescription: ""',
'nel-description':'name: guide\ndescription: "\\u0085"'}
cases=[]
for kind in ['skill','prompt']:
 for identifier, yaml in vectors.items():
  cases.append(dict(id=kind+'-'+identifier,kind=kind,paths=['SKILL.md' if kind=='skill' else 'guide.md'],content='---\n'+yaml+'\n---\nbody'))
 cases.append(dict(id=kind+'-immediate-empty',kind=kind,paths=['SKILL.md' if kind=='skill' else 'guide.md'],content='---\n---\nbody'))
 cases.append(dict(id=kind+'-space-opening',kind=kind,paths=['SKILL.md' if kind=='skill' else 'guide.md'],content='--- \nname: guide\ndescription: Guide\n---\nbody'))
cases.extend([dict(id='prompt-raw-feff-line',kind='prompt',paths=['guide.md'],content='\ufeff\ufeff\nbody'), dict(id='prompt-raw-nel-line',kind='prompt',paths=['guide.md'],content='\u0085\nbody')])
with tempfile.TemporaryDirectory(prefix='pisharp-frontmatter-oracle-') as directory:
 root=Path(directory)
 (root/'Program.cs').write_text((repo/'tools/parity/resource-frontmatter-probe.cs').read_text())
 (root/'probe.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>')
 env=dict(os.environ,NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
 build=subprocess.run(['dotnet','build',str(root/'probe.csproj'),'--warnaserror','-m:1','-p:UseSharedCompilation=false', '-p:RestoreFallbackFolders=' + str(Path.home() / '.nuget/packages')],env=env,capture_output=True,text=True,timeout=120)
 if build.returncode:raise RuntimeError(build.stdout+build.stderr)
 for case in cases:
  work=root/case['id'];work.mkdir();(work/case['paths'][0]).write_text(case['content'])
 inputs=root/'inputs.json';inputs.write_text(json.dumps(cases))
 expected=json.loads(subprocess.check_output(['node','--experimental-transform-types',str(repo/'tools/parity/resource-frontmatter-probe.mjs'),str(pi),str(inputs),str(root)],env=env,timeout=60))
 actual=json.loads(subprocess.check_output(['dotnet',str(root/'bin/Debug/net10.0/probe.dll'),str(inputs),str(root)],env=env,timeout=30))
 def projection(value):
  return {key: value[key] for key in ['id','result','error']}
 differences=[dict(id=p['id'],pi=p,pisharp=n) for p,n in zip(expected,actual,strict=True) if projection(p)!=projection(n)]
 warning_text_differences=[dict(id=p['id'],pi=p['warningMessages'],pisharp=n['warningMessages']) for p,n in zip(expected,actual,strict=True) if p['warningMessages']!=n['warningMessages']]
 output=args.output
 output.parent.mkdir(parents=True,exist_ok=True)
 output.write_text(json.dumps(dict(piSha=pi_sha,scenarios=cases,pi=expected,pisharp=actual,differences=differences,match=not differences and not warning_text_differences,warningTextMatch=not warning_text_differences,warningTextDifferences=warning_text_differences,comparison='Exact loaded metadata/order, full body SHA256/UTF16 length and diagnostic type/path/order/message for these cases. Original warning messages are retained.'),indent=2)+'\n')
 assert not differences and not warning_text_differences,json.dumps(dict(data=differences,warnings=warning_text_differences),indent=2)
 print(f'Matched {len(cases)} frontmatter cases.')
