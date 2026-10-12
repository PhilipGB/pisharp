import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
repo=Path('/home/philip/Downloads/pisharp-bootstrap/pisharp')
output=Path('/tmp/pisharp-pin-neutrality-20261012')
folder=output/'run-1'
pi=Path('/tmp/pisharp-frozen-pi1')
sys.path.insert(0,str(repo/'docs/refactor/tools'))
from terminal_assertions import assertions
archive=folder/'resumption-setup-attempts'
archive.mkdir(exist_ok=True)
env=dict(os.environ,DOTNET_PROCESSOR_COUNT='2')
def save(key):
    for p in [folder/key,*[folder/f'{key}.{suffix}' for suffix in ['log','invocation.json','json']]]:
        if p.exists():shutil.move(str(p),str(archive/p.name))
def run(key,command,target_folder=folder):
    print('START '+key,flush=True)
    start=time.time()
    with (target_folder/f'{key}.log').open('w') as stream:
        r=subprocess.run(command,cwd=repo,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=3600)
    (target_folder/f'{key}.invocation.json').write_text(json.dumps(dict(id=key,command=command,cwd=str(repo),exitCode=r.returncode,seconds=time.time()-start,logger='stdout+stderr text'),indent=2)+'\n')
    print(f'DONE {key} exit={r.returncode}',flush=True)
    return r.returncode
# Same complete fixture definitions; only harness-owned persistence and captured trace handling changed.
for stem in ['project-trust-extension','transcript-markdown-resume']:
    fixture=repo/'tools/parity/terminal/fixtures'/f'{stem}.json'
    for calibration in [True,False]:
        key=f'terminal-{stem}-'+('calibration' if calibration else 'paired')
        save(key)
        target=folder/key
        command=['python3','tools/parity/terminal/run.py','--pi',str(pi),'--fixture',str(fixture),'--output',str(target),'--pisharp-extension',str(repo/'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll')]
        if calibration:command.append('--calibrate')
        run(key,command)
        omitted=[]
        for artifact in target.glob('*.gz'):
            if '.assertions.' not in artifact.name:
                omitted.append(dict(name=artifact.name,bytes=artifact.stat().st_size,sha256=hashlib.sha256(artifact.read_bytes()).hexdigest()))
                artifact.unlink()
        (target/'omitted-raw-streams.json').write_text(json.dumps(omitted,indent=2)+'\n')
    save('assert-'+stem)
    assertions(repo,folder,fixture,run)
assert run('python',['python3','-m','unittest','discover','-v','-s','tools/parity/terminal','-p','test_*.py'])==0
assert run('pin-verify',['python3','tools/parity/pi_reference.py','verify','--pi',str(pi)])==0
comparison=['python3','docs/refactor/tools/results.py','--baseline','docs/refactor/baseline/semantic-baseline.json','--candidate',str(folder),'--output',str(output/'pin-neutrality-comparison.json')]
assert subprocess.run(comparison,cwd=repo).returncode==0
print('COMPLETE pin neutrality',flush=True)
