import json,os,re,shutil,subprocess,time
from pathlib import Path
repo=Path('/home/philip/Downloads/pisharp-bootstrap/pisharp');out=Path('/tmp/pisharp-mcp-parity');out.mkdir(exist_ok=True)
env=dict(os.environ,DOTNET_PROCESSOR_COUNT='2')
def run(key,command):
 print('START '+key,flush=True);start=time.time()
 with (out/f'{key}.log').open('w') as stream:
  r=subprocess.run(command,cwd=repo,env=env,stdout=stream,stderr=subprocess.STDOUT,timeout=1800)
 (out/f'{key}.invocation.json').write_text(json.dumps(dict(id=key,command=command,cwd=str(repo),exitCode=r.returncode,seconds=time.time()-start,logger='stdout+stderr text'),indent=2)+'\n');print(f'DONE {key} exit={r.returncode}',flush=True);return r.returncode
assert run('mcp-build',['dotnet','build','tools/McpConformance/PiSharp.McpConformance.csproj','--no-restore','--warnaserror','-m:1','-p:UseSharedCompilation=false'])==0
assert run('mcp',['node','tools/parity/mcp-conformance/run.mjs','--keep-results'])==0
match=re.search(r'Results kept in (.+)',(out/'mcp.log').read_text());assert match;shutil.copytree(match[1].strip(),out/'mcp-results')
for name in ['mcp-credential-differential','radius-mcp-differential']:
 command=json.loads((repo/'docs/refactor/baseline/pilot-checkpoint/run-1'/f'{name}.invocation.json').read_text())['command'];command[command.index('--output')+1]=str(out/f'{name}.json');assert run(name,command)==0
print('COMPLETE MCP parity',flush=True)
