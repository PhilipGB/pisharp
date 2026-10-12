#!/usr/bin/env python3
"""Finish both baseline passes after correcting capture invocation mistakes.

Original instrumented test results remain coverage evidence. Corrected invocations
run the unchanged logical test/fixture scope; no product/test files are changed.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
from terminal_assertions import assertions

cli=argparse.ArgumentParser()
cli.add_argument('--repo',type=Path,required=True)
cli.add_argument('--pi',type=Path,required=True)
cli.add_argument('--output',type=Path,required=True)
cli.add_argument('--wait-pid',type=int,help='Wait for the verified original capture process to exit before supplemental work')
args=cli.parse_args()
if args.wait_pid:
    process=Path(f'/proc/{args.wait_pid}/cmdline')
    command=process.read_bytes()
    assert b'/tmp/pisharp-refactor-20261012/tools/capture.py' in command, 'Unexpected process identity'
    print(f'WAIT original capture process {args.wait_pid}',flush=True)
    while process.exists():
        try:
            if process.read_bytes()!=command:
                break
        except FileNotFoundError:
            break
        time.sleep(10)
    print('Original capture is terminal; starting supplemental runs',flush=True)
repo,pi,output=args.repo,args.pi,args.output
selection=json.loads((output/'source-selection.json').read_text())
assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip()==selection['pisharp']
assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=pi,text=True).strip()==selection['pi']
env=dict(os.environ,TERM='xterm-256color',DOTNET_PROCESSOR_COUNT='2',UV_THREADPOOL_SIZE='2',
         OPENBLAS_NUM_THREADS='2',OMP_NUM_THREADS='2',MKL_NUM_THREADS='2',NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
def run(key,command,folder,cwd=None):
    print(f'START {folder.name}/{key}',flush=True)
    start=time.time()
    with (folder/f'{key}.log').open('w') as stream:
        result=subprocess.run(command,cwd=cwd or repo,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=1800)
    record=dict(id=key,command=command,cwd=str(cwd or repo),exitCode=result.returncode,seconds=round(time.time()-start,3),logger='stdout+stderr text')
    (folder/f'{key}.invocation.json').write_text(json.dumps(record,indent=2)+'\n')
    print(f'DONE {folder.name}/{key} exit={result.returncode}',flush=True)
    return result.returncode

setup=output/'setup'
assert run('pi-install',['npm','ci','--ignore-scripts','--no-audit','--no-fund'],setup,cwd=pi)==0
assert run('pi-build',['npm','run','build:offline'],setup,cwd=pi)==0
assert subprocess.run(['git','diff','--quiet'],cwd=pi).returncode==0

for n in [1,2]:
    folder=output/f'run-{n}'
    run('dotnet-standard',['dotnet','test','PiSharp.slnx','--no-build','--no-restore','--logger',
        'trx;LogFileName=tests.trx','--results-directory',str(folder/'dotnet-standard')],folder)
    # Rebuild all upstream package outputs at the frozen SHA before any probe
    # that may resolve a workspace dependency through dist. Terminal runs load
    # source through Pi's explicit source-resolver and need no repeat for this.
    for script in sorted((repo/'tools/parity').glob('*differential.py')):
        invocations=sorted(folder.glob(script.stem+'*.invocation.json'))
        for invocation in invocations:
            if '.before-pi-build.' in invocation.name:
                continue
            key=json.loads(invocation.read_text())['id']
            for suffix in ['log','invocation.json','json']:
                path=folder/f'{key}.{suffix}'
                if path.exists():
                    shutil.copyfile(path,folder/f'{key}.before-pi-build.{suffix}')
            run(key,json.loads(invocation.read_text())['command'],folder)
    for fixture in sorted((repo/'tools/parity/terminal/fixtures').glob('*.json')):
        data=json.loads(fixture.read_text())
        if data.get('pisharpExtension'):
            for calibrate in [True,False]:
                key=f'terminal-{fixture.stem}-'+('calibration' if calibrate else 'paired')
                # Invalid first attempts are setup diagnostics, not semantic test outcomes.
                for suffix in ['log','invocation.json']:
                    path=folder/f'{key}.{suffix}'
                    shutil.copyfile(path,folder/f'{key}.invalid-attempt.{suffix}')
                target=folder/key
                command=['python3','tools/parity/terminal/run.py','--pi',str(pi),'--fixture',str(fixture),
                    '--output',str(target),'--pisharp-extension',str(repo/'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll')]
                if calibrate:
                    command+=['--calibrate']
                run(key,command,folder)
                omitted=[]
                for artifact in target.glob('*.gz'):
                    if '.assertions.' not in artifact.name:
                        omitted.append(dict(name=artifact.name,bytes=artifact.stat().st_size,
                            sha256=hashlib.sha256(artifact.read_bytes()).hexdigest()))
                        artifact.unlink()
                (target/'omitted-raw-streams.json').write_text(json.dumps(omitted,indent=2)+'\n')
        assertions(repo,folder,fixture,run)
    print(f'COMPLETE supplemental run-{n}',flush=True)
