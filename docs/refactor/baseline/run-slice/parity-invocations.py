import hashlib,json,os,subprocess,sys,time
from pathlib import Path
repo=Path('/home/philip/Downloads/pisharp-bootstrap/pisharp');out=Path('/tmp/pisharp-run-parity');out.mkdir(exist_ok=True)
sys.path.insert(0,str(repo/'docs/refactor/tools'));from terminal_assertions import assertions
source={str(p.relative_to(repo)):hashlib.sha256(p.read_bytes()).hexdigest() for p in [repo/'src/PiSharp.Runtime/Sessions/ConversationRun.cs',repo/'src/PiSharp.Runtime/Sessions/ProviderTurnJournal.cs',repo/'src/PiSharp.Runtime/Sessions/ProviderRequestAttribution.cs',repo/'src/PiSharp.Runtime/Sessions/ConversationAttemptStream.cs']}
(out/'source-manifest.json').write_text(json.dumps(source,indent=2)+'\n')
env=dict(os.environ,DOTNET_PROCESSOR_COUNT='2')
def run(key,command,folder=out):
 print('START '+key,flush=True);start=time.time()
 with (folder/f'{key}.log').open('w') as stream:
  r=subprocess.run(command,cwd=repo,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=1800)
 (folder/f'{key}.invocation.json').write_text(json.dumps(dict(id=key,command=command,cwd=str(repo),exitCode=r.returncode,seconds=time.time()-start,logger='stdout+stderr text'),indent=2)+'\n');print(f'DONE {key} exit={r.returncode}',flush=True)
# Reuse exactly the full pilot process invocation scope at frozen Pi.
pilot=repo/'docs/refactor/baseline/pilot-checkpoint/run-1'
for p in sorted(pilot.glob('*differential*.invocation.json')):
 r=json.loads(p.read_text());key=r['id'];command=r['command'];command[command.index('--output')+1]=str(out/f'{key}.json');run(key,command)
for stem in ['extension-resource-discovery-reload','resource-skill-prompt-read-bash']:
 fixture=repo/'tools/parity/terminal/fixtures'/f'{stem}.json'
 for mode in ['calibration','paired']:
  key=f'terminal-{stem}-{mode}';target=out/key
  command=['python3','tools/parity/terminal/run.py','--pi','/tmp/pisharp-frozen-pi1','--fixture',str(fixture),'--output',str(target),'--pisharp-extension',str(repo/'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll')]
  if mode=='calibration':command.append('--calibrate')
  run(key,command)
  omitted=[]
  for artifact in target.glob('*.gz'):
   if '.assertions.' not in artifact.name:
    omitted.append(dict(name=artifact.name,bytes=artifact.stat().st_size,sha256=hashlib.sha256(artifact.read_bytes()).hexdigest()));artifact.unlink()
  (target/'omitted-raw-streams.json').write_text(json.dumps(omitted,indent=2)+'\n')
 assertions(repo,out,fixture,run)
for p,sha in source.items():assert hashlib.sha256((repo/p).read_bytes()).hexdigest()==sha,p
print('COMPLETE agent parity',flush=True)
