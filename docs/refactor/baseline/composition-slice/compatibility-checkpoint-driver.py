import ast,hashlib,json,os,shutil,subprocess,sys,time
from pathlib import Path
repo=Path('/home/philip/Downloads/pisharp-bootstrap/pisharp');out=Path('/tmp/pisharp-composition-slice/compatibility-verification');folder=out/'run-1';folder.mkdir(parents=True,exist_ok=True)
sys.path.insert(0,str(repo/'docs/refactor/tools'))
from results import capture,compare
from terminal_assertions import assertions
script=repo/'docs/refactor/tools/capture.py'
f=next(n for n in ast.parse(script.read_text()).body if isinstance(n,ast.FunctionDef) and n.name=='working_source')
ns=dict(Path=Path,json=json,subprocess=subprocess,hashlib=hashlib,repo=repo,__file__=str(script));exec(compile(ast.Module(body=[f],type_ignores=[]),str(script),'exec'),ns)
source=ns['working_source']();(out/'working-source.json').write_text(json.dumps(source,indent=2)+'\n')
(out/'working-tree.patch').write_bytes(subprocess.check_output(['git','diff','--binary','HEAD'],cwd=repo))
env=dict(os.environ,DOTNET_PROCESSOR_COUNT='2',TERM='xterm-256color',UV_THREADPOOL_SIZE='2',OPENBLAS_NUM_THREADS='2',OMP_NUM_THREADS='2',MKL_NUM_THREADS='2',NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
(out/'metadata.json').write_text(json.dumps(dict(head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip(),pi=subprocess.check_output(['git','rev-parse','HEAD'],cwd='/tmp/pisharp-frozen-pi1',text=True).strip(),environment={k:env[k] for k in ['DOTNET_PROCESSOR_COUNT','TERM','UV_THREADPOOL_SIZE','OPENBLAS_NUM_THREADS','OMP_NUM_THREADS','MKL_NUM_THREADS','NUGET_HTTP_CACHE_PATH']}),indent=2)+'\n')
keys=[]
def run(key,command,target_folder=folder,cwd=repo):
 print('START '+key,flush=True);start=time.time()
 with (target_folder/f'{key}.log').open('w') as stream:
  r=subprocess.run(command,cwd=cwd,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=1800)
 (target_folder/f'{key}.invocation.json').write_text(json.dumps(dict(id=key,command=command,cwd=str(cwd),exitCode=r.returncode,seconds=time.time()-start,logger='stdout+stderr text'),indent=2)+'\n')
 keys.append(key);print(f'DONE {key} exit={r.returncode}',flush=True);return r.returncode
assert run('pin-verify',['python3','tools/parity/pi_reference.py','verify','--pi','/tmp/pisharp-frozen-pi1'])==0
assert run('format',['dotnet','format','PiSharp.slnx','--verify-no-changes','--no-restore'])==0
assert run('build',['dotnet','build','PiSharp.slnx','--no-restore','--warnaserror','-m:1','-p:UseSharedCompilation=false'])==0
assert run('dotnet',['dotnet','test','PiSharp.slnx','--no-build','--no-restore','--logger','trx;LogFileName=tests.trx','--results-directory',str(folder/'dotnet')])==0
assert run('node',['node','--test','--test-reporter=tap','terminal-state.test.mjs'],cwd=repo/'tools/parity/terminal')==0
assert run('python',['python3','-m','unittest','discover','-s','tools/parity/terminal','-p','test_*.py','-v'])==0
pilot=repo/'docs/refactor/baseline/pilot-checkpoint/run-1'
for p in sorted(pilot.glob('*differential*.invocation.json')):
 r=json.loads(p.read_text());key=r['id'];command=r['command'];command[command.index('--output')+1]=str(folder/f'{key}.json');run(key,command)
fixtures=['selectors-settings','thinking-toggle','transcript-markdown-resume']
for stem in fixtures:
 fixture=repo/'tools/parity/terminal/fixtures'/f'{stem}.json'
 for mode in ['paired']:
  key=f'terminal-{stem}-{mode}';target=folder/key
  command=['python3','tools/parity/terminal/run.py','--pi','/tmp/pisharp-frozen-pi1','--fixture',str(fixture),'--output',str(target),'--pisharp-extension',str(repo/'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll')]
  if mode=='calibration':command.append('--calibrate')
  run(key,command)
  omitted=[]
  for artifact in target.glob('*.gz'):
   if '.assertions.' not in artifact.name:
    omitted.append(dict(name=artifact.name,bytes=artifact.stat().st_size,sha256=hashlib.sha256(artifact.read_bytes()).hexdigest()));artifact.unlink()
  (target/'omitted-raw-streams.json').write_text(json.dumps(omitted,indent=2)+'\n')
 assertions(repo,folder,fixture,run)
assert ns['working_source']()==source,'Source changed during verification'
base=json.loads((repo/'docs/refactor/baseline/semantic-baseline.json').read_text())
expected={k:v for k,v in base['identities'].items() if k.startswith(('dotnet/','node/','python/')) or (k.startswith('suite/') and k[6:] in keys) or any(k.startswith(f'terminal/terminal-{stem}-paired/') for stem in fixtures)}
(out/'scope.json').write_text(json.dumps(dict(suites=keys,fixtures=fixtures,expected=sorted(expected)),indent=2)+'\n')
result=compare(dict(identities=expected,baselineFlaky=[k for k in base['baselineFlaky'] if k in expected]),capture(folder),{})
(out/'preservation-comparison.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2),flush=True)
assert result['pass_'],result['failures']
print('COMPLETE composition ownership checkpoint',flush=True)
