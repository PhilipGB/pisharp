import hashlib,json,os,shutil,subprocess,sys,time
from pathlib import Path
repo=Path('/home/philip/Downloads/pisharp-bootstrap/pisharp')
output=Path('/tmp/pisharp-pilot-checkpoint'); folder=output/'run-1'
sys.path.insert(0,str(repo/'docs/refactor/tools'))
from results import capture,compare
from terminal_assertions import assertions
base=json.loads((repo/'docs/refactor/baseline/semantic-baseline.json').read_text())
manifest=json.loads((output/'capture-metadata.json').read_text())['workingSource']
def verify_source():
    for p,sha in manifest.items():
        f=repo/p
        assert (hashlib.sha256(f.read_bytes()).hexdigest() if f.is_file() else None)==sha,p
verify_source()
assert len(list(folder.glob('terminal-*/report.json')))==66
first=compare(base,capture(folder),{})
(output/'initial-pilot-comparison.json').write_text(json.dumps(first,indent=2)+'\n')
failures=first['failures']
assert all(f['identity'].startswith('terminal/') and f['reason']=='new semantic outcome' for f in failures),failures
keys=sorted({f['identity'].split('/')[1] for f in failures})
archive=folder/'outside-baseline-first-attempts';archive.mkdir(exist_ok=True)
env=dict(os.environ,DOTNET_PROCESSOR_COUNT='2')
def run(key,command,target_folder=folder):
    print('START '+key,flush=True);start=time.time()
    with (target_folder/f'{key}.log').open('w') as stream:
        r=subprocess.run(command,cwd=repo,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=3600)
    (target_folder/f'{key}.invocation.json').write_text(json.dumps(dict(id=key,command=command,cwd=str(repo),exitCode=r.returncode,seconds=time.time()-start,logger='stdout+stderr text'),indent=2)+'\n')
    print(f'DONE {key} exit={r.returncode}',flush=True)
for key in keys:
    for p in [folder/key,folder/f'{key}.log',folder/f'{key}.invocation.json']:
        shutil.move(str(p),str(archive/p.name))
    calibration=key.endswith('-calibration');stem=key[len('terminal-'):-(len('-calibration') if calibration else len('-paired'))]
    fixture=repo/'tools/parity/terminal/fixtures'/f'{stem}.json';target=folder/key
    command=['python3','tools/parity/terminal/run.py','--pi','/tmp/pisharp-frozen-pi1','--fixture',str(fixture),'--output',str(target),'--pisharp-extension',str(repo/'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll')]
    if calibration:command.append('--calibrate')
    run(key,command)
    omitted=[]
    for p in target.glob('*.gz'):
        if '.assertions.' not in p.name:
            omitted.append(dict(name=p.name,bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()));p.unlink()
    (target/'omitted-raw-streams.json').write_text(json.dumps(omitted,indent=2)+'\n')
    assertions(repo,folder,fixture,run)
verify_source()
result=compare(base,capture(folder),{})
(output/'pilot-preservation-comparison.json').write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps({k:v for k,v in result.items() if k!='failures'},indent=2),flush=True)
if not result['pass_']:
    print(json.dumps(result['failures'],indent=2));raise SystemExit(1)
print('COMPLETE pilot preservation comparison',flush=True)
