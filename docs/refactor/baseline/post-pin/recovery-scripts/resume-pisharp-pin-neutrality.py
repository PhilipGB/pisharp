import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

repo = Path('/home/philip/Downloads/pisharp-bootstrap/pisharp')
output = Path('/tmp/pisharp-pin-neutrality-20261012')
folder = output / 'run-1'
pi = Path('/tmp/pisharp-frozen-pi1')
sys.path.insert(0, str(repo/'docs/refactor/tools'))
from terminal_assertions import assertions
from results import capture

baseline = json.loads((repo/'docs/refactor/baseline/semantic-baseline.json').read_text())
original = json.loads((output/'capture-metadata.json').read_text())['workingSource']
for relative, checksum in original.items():
    path = repo / relative
    assert (hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None) == checksum, relative

env = dict(os.environ, TERM='xterm-256color', DOTNET_PROCESSOR_COUNT='2', UV_THREADPOOL_SIZE='2',
           OPENBLAS_NUM_THREADS='2', OMP_NUM_THREADS='2', MKL_NUM_THREADS='2',
           NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')

def run(key, command, target_folder=folder):
    print(f'START {key}', flush=True)
    start=time.time()
    with (target_folder/f'{key}.log').open('w') as stream:
        result=subprocess.run(command,cwd=repo,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=3600)
    record=dict(id=key,command=command,cwd=str(repo),exitCode=result.returncode,
                seconds=time.time()-start,logger='stdout+stderr text')
    (target_folder/f'{key}.invocation.json').write_text(json.dumps(record,indent=2)+'\n')
    print(f'DONE {key} exit={result.returncode}',flush=True)
    return result.returncode

# Preserve the failed full conformance attempt, then repeat its unchanged scope.
archive=folder/'mcp-first-attempt'
archive.mkdir(exist_ok=True)
for name in ['mcp.log','mcp.invocation.json','mcp-results']:
    if (folder/name).exists():
        shutil.move(str(folder/name),str(archive/name))
assert run('mcp',['node','tools/parity/mcp-conformance/run.mjs','--keep-results']) == 0
match=re.search(r'Results kept in (.+)',(folder/'mcp.log').read_text())
assert match
shutil.copytree(match[1].strip(),folder/'mcp-results')

# Complete missing invocations and reproduce only completed cases whose semantic
# outcome is outside the frozen baseline set. Completed evidence stays archived.
known=capture(folder)
for fixture in sorted((repo/'tools/parity/terminal/fixtures').glob('*.json')):
    stem=fixture.stem
    for calibration in [True,False]:
        key=f'terminal-{stem}-'+('calibration' if calibration else 'paired')
        target=folder/key
        invocation=folder/f'{key}.invocation.json'
        report=target/'report.json'
        completed=invocation.exists() and report.exists()
        outside=any(value not in baseline['identities'].get(identity,[]) for identity,value in known.items()
                    if identity.startswith('terminal/'+key+'/')) if completed else True
        if completed and not outside:
            continue
        if target.exists():
            prior=folder/'interrupted-or-environment-attempts'
            prior.mkdir(exist_ok=True)
            shutil.move(str(target),str(prior/key))
            for suffix in ['log','invocation.json']:
                p=folder/f'{key}.{suffix}'
                if p.exists():
                    shutil.move(str(p),str(prior/p.name))
        command=['python3','tools/parity/terminal/run.py','--pi',str(pi),'--fixture',str(fixture),
                 '--output',str(target),'--pisharp-extension',
                 str(repo/'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll')]
        if calibration:
            command.append('--calibrate')
        run(key,command)
        omitted=[]
        for artifact in target.glob('*.gz'):
            if '.assertions.' not in artifact.name:
                omitted.append(dict(name=artifact.name,bytes=artifact.stat().st_size,
                                    sha256=hashlib.sha256(artifact.read_bytes()).hexdigest()))
                artifact.unlink()
        (target/'omitted-raw-streams.json').write_text(json.dumps(omitted,indent=2)+'\n')
    # Existing assertion results already match; regenerated fixtures need refreshed
    # checks, and fixtures interrupted before assertions still need their checks.
    assertions(repo,folder,fixture,run)

for relative, checksum in original.items():
    path=repo/relative
    assert (hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None)==checksum,relative
print('COMPLETE resumed neutrality scope',flush=True)
