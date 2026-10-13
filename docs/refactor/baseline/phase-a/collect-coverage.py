from pathlib import Path
import json,os,subprocess,time
out=Path('/tmp/pisharp-phase-a');repo=Path('/home/philip/Downloads/pisharp-bootstrap/pisharp');result=out/'coverage'
command=['dotnet','test','PiSharp.slnx','--no-build','--no-restore','--logger','trx;LogFileName=tests.trx','--results-directory',str(result),'--collect','XPlat Code Coverage']
env=dict(os.environ,DOTNET_PROCESSOR_COUNT='2',NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http');head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip();start=time.time()
with (out/'coverage.log').open('w') as stream:r=subprocess.run(command,cwd=repo,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=1800)
(out/'coverage.invocation.json').write_text(json.dumps(dict(id='diagnostic-coverage',sourceHead=head,command=command,cwd=str(repo),environment={k:env[k] for k in ['DOTNET_PROCESSOR_COUNT','NUGET_HTTP_CACHE_PATH']},exitCode=r.returncode,seconds=time.time()-start,logger='stdout+stderr/TRX/Cobertura',scope='Same full .NET scope as ordinary capture. Diagnostic collector only; ordinary 1382-row outcome proof is authoritative.'),indent=2)+'\n');print('COVERAGE_COMPLETE',r.returncode)
