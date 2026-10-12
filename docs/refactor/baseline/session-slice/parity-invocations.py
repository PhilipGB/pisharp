from pathlib import Path
import hashlib,json,os,subprocess,time
repo=Path('/home/philip/Downloads/pisharp-bootstrap/pisharp');output=Path('/tmp/pisharp-session-parity');output.mkdir(exist_ok=True)
env=dict(os.environ,DOTNET_PROCESSOR_COUNT='2')
def run(key,command):
 print('START '+key,flush=True);start=time.time()
 with (output/f'{key}.log').open('w') as stream:
  r=subprocess.run(command,cwd=repo,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=1800)
 (output/f'{key}.invocation.json').write_text(json.dumps(dict(id=key,command=command,cwd=str(repo),exitCode=r.returncode,seconds=time.time()-start,logger='stdout+stderr text'),indent=2)+'\n');print(f'DONE {key} exit={r.returncode}',flush=True)
for variant in ['context-compaction','session-transitions','state-identity']:
 key='virtual-model-routing-differential-'+variant
 run(key,['python3','tools/parity/virtual-model-routing-differential.py','--pi','/tmp/pisharp-frozen-pi1','--'+variant,'--output',str(output/f'{key}.json')])
for mode in ['calibration','paired']:
 key='terminal-transcript-markdown-resume-'+mode;target=output/key
 command=['python3','tools/parity/terminal/run.py','--pi','/tmp/pisharp-frozen-pi1','--fixture','tools/parity/terminal/fixtures/transcript-markdown-resume.json','--output',str(target)]
 if mode=='calibration':command.append('--calibrate')
 run(key,command)
 omitted=[]
 for p in target.glob('*.gz'):
  if '.assertions.' not in p.name:
   omitted.append(dict(name=p.name,bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()));p.unlink()
 (target/'omitted-raw-streams.json').write_text(json.dumps(omitted,indent=2)+'\n')
print('COMPLETE session parity',flush=True)
