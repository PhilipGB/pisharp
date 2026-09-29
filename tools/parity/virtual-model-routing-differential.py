import argparse,http.server,json,os,pathlib,queue,subprocess,tempfile,threading,time
parser=argparse.ArgumentParser(description='Replay virtual routing against built current Pi and PiSharp CLI processes.')
parser.add_argument('--pi',type=pathlib.Path,required=True,help='Pi checkout after npm ci and npm run build')
parser.add_argument('--pisharp',type=pathlib.Path,default=pathlib.Path(__file__).resolve().parents[2],help='PiSharp checkout after dotnet build (including test fixture assembly)')
parser.add_argument('--context-compaction',action='store_true',help='Route a later turn to a smaller physical window and compare canonical compaction')
parser.add_argument('--state-identity',action='store_true',help='Compare fresh equal state replacement with returning the same input state')
parser.add_argument('--thinking',choices=['minimal','max'],help='Compare exact logical thinking selection across provider requests')
parser.add_argument('--output',type=pathlib.Path,required=True)
args=parser.parse_args()
pi=args.pi.resolve()
sharp=args.pisharp.resolve()
outputs={}
context_scenario=args.context_compaction
for name in ['pi','pisharp']:
 root=pathlib.Path(tempfile.mkdtemp(prefix='virtual-diff-',dir=pi if name=='pi' else '/tmp'))
 agent=root/'agent';agent.mkdir(); log=root/'routes.jsonl'; wires=[]
 class Handler(http.server.BaseHTTPRequestHandler):
  def do_POST(self):
   body=json.loads(self.rfile.read(int(self.headers['Content-Length']))); index=len(wires);wires.append(body['model'])
   delta={'role':'assistant','tool_calls':[{'index':0,'id':'routed-call','type':'function','function':{'name':'echo_ext','arguments':'{"value":"routed"}'}}]} if index==0 else {'role':'assistant','content':'answer '+str(index)}
   if context_scenario:delta={'role':'assistant','content':'x'*36000 if index==0 else 'history summary' if index==1 else 'small answer'}
   chunk={'id':'response-'+str(index),'object':'chat.completion.chunk','created':1,'model':body['model'],'choices':[{'index':0,'delta':delta,'finish_reason':'tool_calls' if index==0 else 'stop'}],'usage':{'prompt_tokens':10,'completion_tokens':2,'total_tokens':12}}
   if context_scenario:chunk['choices'][0]['finish_reason']='stop'
   if context_scenario and index==0:chunk['usage']={'prompt_tokens':10,'completion_tokens':10000,'total_tokens':10010}
   data=('data: '+json.dumps(chunk)+'\n\ndata: [DONE]\n\n').encode()
   stream=body.get('stream',False)
   if not stream:data=json.dumps({'id':chunk['id'],'object':'chat.completion','created':1,'model':body['model'],'choices':[{'index':0,'message':delta,'finish_reason':'stop'}],'usage':chunk['usage']}).encode()
   self.send_response(200);self.send_header('Content-Type','text/event-stream' if stream else 'application/json');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
  def log_message(self,*args): pass
 server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler);threading.Thread(target=server.serve_forever,daemon=True).start()
 models={'providers':{'physical':{'baseUrl':f'http://127.0.0.1:{server.server_port}/v1','apiKey':'fixture-key','api':'openai-completions','models':[{'id':'small','contextWindow':32000,'maxTokens':1000},{'id':'large','contextWindow':64000,'maxTokens':1000}]}}}
 if context_scenario:
  models['providers']['physical']['models'][0]['contextWindow']=5000
  models['providers']['physical']['models'][1]['contextWindow']=50000
  (agent/'settings.json').write_text(json.dumps({'compaction':{'reserveTokens':0,'keepRecentTokens':1}}))
 (agent/'models.json').write_text(json.dumps(models));env=dict(os.environ)
 for key in ['PISHARP_API_KEY','PISHARP_BASE_URL','PISHARP_MODEL','PISHARP_AUTH_PATH','PISHARP_MODELS_PATH','PISHARP_SETTINGS_PATH']:env.pop(key,None)
 if name=='pi':
  ext=root/'router.ts';ext.write_text('''import { appendFileSync } from 'node:fs';
import { Type } from '@earendil-works/pi-ai';
export default function(pi) {
pi.registerTool({name:'echo_ext',label:'Echo',description:'Echo fixture',parameters:Type.Object({value:Type.String()}),async execute(id,params) {return {content:[{type:'text',text:'extension: '+params.value}],details:{}};}});
pi.registerVirtualModel({provider:'test-router',id:'auto',name:'Auto',thinkingLevels:['off','minimal','max'],route(request,ctx) {const identity=process.env.PISHARP_VIRTUAL_FIXTURE_STATE==='1';const state=identity?(request.state?.phase??0):(request.state??0);appendFileSync(process.env.PISHARP_VIRTUAL_FIXTURE_LOG,JSON.stringify({reason:request.reason,state,previous:request.previous?.model.id??null,...(process.env.PISHARP_VIRTUAL_FIXTURE_THINKING?{thinking:request.thinkingLevel}:{})})+'\\n');return {model:ctx.modelRegistry.find('physical',process.env.PISHARP_VIRTUAL_FIXTURE_CONTEXT==='1'?(request.reason==='direct'||state===0?'large':'small'):(request.reason==='continuation'?'large':'small')),thinkingLevel:'off',state:identity?(request.previous?.model.id==='large'?request.state:{phase:1}):state+1};}});
}''')
  env['PI_CODING_AGENT_DIR']=str(agent);cmd=['node',str(pi/'packages/coding-agent/dist/cli.js')]
 else:
  ext=sharp/'tests/PiSharp.Tests/bin/Debug/net10.0/PiSharp.Tests.dll';env['PISHARP_AGENT_DIR']=str(agent);cmd=['dotnet',str(sharp/'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')]
 env['PISHARP_VIRTUAL_FIXTURE_LOG']=str(log)
 if context_scenario:env['PISHARP_VIRTUAL_FIXTURE_CONTEXT']='1'
 if args.thinking:env['PISHARP_VIRTUAL_FIXTURE_THINKING']=args.thinking
 if args.state_identity:env['PISHARP_VIRTUAL_FIXTURE_STATE']='1'
 cmd+=['--mode','rpc','--provider','test-router','--model','auto','--offline','--no-session','--no-extensions','--extension',str(ext),'--tools','echo_ext']
 if args.thinking:cmd+=['--thinking',args.thinking]
 p=subprocess.Popen(cmd,cwd=root,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True); q=queue.Queue(); errors=[]; records=[]
 def readout():
  for line in p.stdout:q.put(json.loads(line))
  q.put(None)
 def readerr():errors.extend(p.stderr.readlines())
 threading.Thread(target=readout,daemon=True).start();threading.Thread(target=readerr,daemon=True).start()
 def send(obj):p.stdin.write(json.dumps(obj)+'\n');p.stdin.flush()
 def until(predicate):
  deadline=time.monotonic()+45
  while True:
   record=q.get(timeout=max(.1,deadline-time.monotonic()))
   if record is None:raise RuntimeError('ended '+str(errors))
   records.append(record)
   if record.get('type')=='event' and record.get('data',{}).get('Type')=='turn_failed':raise RuntimeError(str(record))
   if record.get('type')=='response' and record.get('success')==False:raise RuntimeError(str(record))
   if predicate(record):return record
 try:
  send({'type':'prompt','message':'tool please'});until(lambda x:x['type']=='agent_end')
  if name=='pisharp':
   until(lambda x:x['type']=='agent_settled')
   while True:
    send({'type':'get_state','id':'settled'})
    if not until(lambda x:x.get('id')=='settled')['data']['isStreaming']:break
  send({'type':'prompt','message':'next turn','streamingBehavior':'followUp'});until(lambda x:x['type']=='agent_end')
  send({'type':'get_state','id':'state'});state=until(lambda x:x.get('id')=='state')['data']['model']
  send({'type':'get_messages','id':'messages'});messages=until(lambda x:x.get('id')=='messages')['data']['messages']
  if context_scenario or args.state_identity:
   send({'type':'get_entries','id':'entries'});entries=until(lambda x:x.get('id')=='entries')['data']['entries']
  outputs[name]={'selected':{'provider':state['provider'],'model':state['id']},'wireModels':wires,'assistants':[{'provider':m['provider'],'model':m['model'],'api':m['api']} for m in messages if m['role']=='assistant'],'routes':[json.loads(line) for line in log.read_text().splitlines()]}
  if args.state_identity:outputs[name]['stateEntries']=sum(entry['type']=='custom' and entry.get('customType')=='pi.virtual-model-state' for entry in entries)
  if context_scenario:outputs[name]['compactions']=sum(entry['type']=='compaction' for entry in entries)
 finally:
  p.stdin.close()
  try:p.wait(timeout=5)
  except subprocess.TimeoutExpired:p.kill();p.wait()
  server.shutdown()
args.output.write_text(json.dumps(outputs,indent=2)+'\n')
print(json.dumps(outputs,indent=2))
assert outputs['pi']==outputs['pisharp'], 'differential mismatch'
