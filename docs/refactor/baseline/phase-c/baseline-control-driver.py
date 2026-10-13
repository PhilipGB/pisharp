from pathlib import Path
import hashlib,json,os,subprocess,time
repo=Path('/home/philip/Downloads/pisharp-bootstrap/pisharp');control=Path('/tmp/pisharp-baseline-control-064bcc7b');out=Path('/tmp/pisharp-phase-c-checkpoint/baseline-variance-controls');out.mkdir(exist_ok=True)
sha=subprocess.check_output(['git','rev-parse','HEAD'],cwd=control,text=True).strip();assert sha=='064bcc7b90344cffde595f9aca1c5b850cefe567'
assert subprocess.run(['git','diff','--quiet','HEAD'],cwd=control).returncode==0
fixture=repo/'tools/parity/terminal/fixtures/llama-model-picker.json';dll=control/'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll'
env=dict(os.environ,DOTNET_PROCESSOR_COUNT='2',TERM='xterm-256color',UV_THREADPOOL_SIZE='2',OPENBLAS_NUM_THREADS='2',OMP_NUM_THREADS='2',MKL_NUM_THREADS='2',NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
def run(key,cmd,cwd):
 start=time.time()
 with (out/f'{key}.log').open('w') as stream:r=subprocess.run(cmd,cwd=cwd,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=1800)
 (out/f'{key}.invocation.json').write_text(json.dumps(dict(command=cmd,cwd=str(cwd),exitCode=r.returncode,seconds=time.time()-start),indent=2)+'\n');print(key,r.returncode,flush=True);return r.returncode
assert run('build',['dotnet','build','PiSharp.slnx','--no-restore','--warnaserror','-t:Rebuild','-m:1','-p:UseSharedCompilation=false'],control)==0
assert run('pin-verify',['python3','tools/parity/pi_reference.py','verify','--pi','/tmp/pisharp-frozen-pi1'],repo)==0
for stem in ['llama-load-replace-cancel','resource-skill-prompt-bash']:
 fixture=repo/'tools/parity/terminal/fixtures'/f'{stem}.json'
 command=['python3','tools/parity/terminal/run.py','--pi','/tmp/pisharp-frozen-pi1','--pisharp',str(dll),'--fixture',str(fixture),'--output',str(out/stem),'--pisharp-extension',str(control/'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll')]
 run(stem,command,repo)
 (out/f'{stem}.fixture-sha256.txt').write_text(hashlib.sha256(fixture.read_bytes()).hexdigest()+'\n')
assert subprocess.run(['git','diff','--quiet','HEAD'],cwd=control).returncode==0
(out/'source-disambiguation.json').write_text(json.dumps(dict(cliSha=sha,cliDll=str(dll),cliDllSha256=hashlib.sha256(dll.read_bytes()).hexdigest(),harnessSha=subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip(),fixtureSha256=hashlib.sha256(fixture.read_bytes()).hexdigest(),piSha='ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e',note='run.py report pisharpSha identifies the harness checkout. --pisharp explicitly selects the freshly rebuilt untouched baseline CLI; this receipt disambiguates their revisions.'),indent=2)+'\n')
for stem in ['llama-load-replace-cancel','resource-skill-prompt-bash']:
 r=json.loads((out/stem/'report.json').read_text())
 print(stem,json.dumps([{k:c[k] for k in ['id','match','renderDifferences','httpMatch','terminalMatch','comparedRenderFrameCount']} for c in r['cases']],indent=2),flush=True)
