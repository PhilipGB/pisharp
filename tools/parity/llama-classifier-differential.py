#!/usr/bin/env python3
"""Compare native llama.cpp classifier results through the same HTTP fixture."""
import argparse, pathlib, tempfile, os
parser=argparse.ArgumentParser()
parser.add_argument('--pi', required=True, help='Current Pi checkout')
parser.add_argument('--output', required=True)
args=parser.parse_args()
repo=pathlib.Path(__file__).resolve().parents[2]
work=pathlib.Path(tempfile.mkdtemp(prefix='pisharp-llama-parity-'))
work.joinpath("Program.cs").write_text('using System.Text.Json;\nusing PiSharp.Runtime.Classifiers;\nusing var http = new HttpClient();\nvar context = new ClassifierContext(JsonSerializer.SerializeToElement(new { text = "fixture" }), new Dictionary<string, ClassifierQuestion> {\n    ["safe"] = new ClassifierBoolQuestion("safe?", new Dictionary<string,string> { ["true"]="safe", ["false"]="unsafe" }),\n    ["choice"] = new ClassifierChoiceQuestion("choose", new Dictionary<string,string> { ["a"]="first", ["b"]="second" }),\n    ["score"] = new ClassifierScoreQuestion("score", ["low", "high"])\n});\nvar result = await new LlamaClassifierClient(http).ClassifyAsync(new("fixture", "model", "llama-cpp-classify", new Uri(args[0]+"/v1")),context,null);\nConsole.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions(JsonSerializerDefaults.Web)));\n')
work.joinpath("probe.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>\n'.replace("{repo}", str(repo)))
work.joinpath("pi.mjs").write_text("import { classify } from '{pi}/packages/ai/src/api/llama-cpp-classify.ts';\nconst context={state:{text:'fixture'},questions:{safe:{type:'bool',instructions:'safe?',criteria:{true:'safe',false:'unsafe'}},choice:{type:'choice',instructions:'choose',criteria:{a:'first',b:'second'}},score:{type:'score',instructions:'score',criteria:['low','high']}}};\nconsole.log(JSON.stringify(await classify({provider:'fixture',id:'model',api:'llama-cpp-classify',baseUrl:process.argv[2]+'/v1'},context)));\n".replace("{pi}", str(pathlib.Path(args.pi).resolve())))
import http.server,json,threading,subprocess,math
class Server(http.server.BaseHTTPRequestHandler):
 def log_message(self,*args):pass
 def do_POST(self):
  if self.headers.get('Transfer-Encoding')=='chunked':
   chunks=[]
   while True:
    size=int(self.rfile.readline().strip(),16)
    if size==0:self.rfile.readline();break
    chunks.append(self.rfile.read(size));self.rfile.read(2)
   raw=b''.join(chunks)
  else:raw=self.rfile.read(int(self.headers['Content-Length']))
  body=json.loads(raw)
  assert body['model']=='model'
  if self.path=='/tokenize':
   text=body['content'];token={'Yes':2,'No':3,'A':4,'B':5,'0':6,'1':7}
   response={'tokens':[1]+([token[text[1:]]] if text.startswith('\n') and len(text)>1 else [])}
  elif self.path=='/apply-template':
   assert body['chat_template_kwargs']=={'enable_thinking':False}
   response={'prompt':body['messages'][1]['content']+'<think>'}
  else:
   assert self.path=='/completion' and body['n_predict']==1 and body['post_sampling_probs']==False
   assert body['prompt'].endswith('<think></think>')
   probs=[{'id':i,'logprob':math.log(.75 if i%2==0 else .25)} for i in range(2,8)]
   response={'completion_probabilities':[{'top_logprobs':probs if body['n_probs']>256 else probs[::2]}]}
  data=json.dumps(response).encode();self.send_response(200);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
env=dict(os.environ)
env.setdefault('NUGET_HTTP_CACHE_PATH','/tmp/pisharp-nuget-http')
subprocess.run(['dotnet','build',str(work/'probe.csproj'),'-p:UseSharedCompilation=false','-m:1'],env=env,check=True,stdout=subprocess.DEVNULL)
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Server);threading.Thread(target=server.serve_forever,daemon=True).start()
url=f'http://127.0.0.1:{server.server_port}'
pi=json.loads(subprocess.check_output(['node',str(work/'pi.mjs'),url]))
cs=json.loads(subprocess.check_output(['dotnet',str(work/'bin/Debug/net10.0/probe.dll'),url]))
for result in [pi,cs]:result.pop('timestamp');result.pop('usage',None);result.pop('errorMessage',None)
def equal(a,b):
 if isinstance(a,dict):return a.keys()==b.keys() and all(equal(a[k],b[k]) for k in a)
 if isinstance(a,float):return math.isclose(a,b,abs_tol=1e-12)
 return a==b
assert equal(pi,cs),(pi,cs)
pathlib.Path(args.output).write_text(json.dumps({'match':True,'pi':pi,'pisharp':cs},indent=2)+'\n')
server.shutdown()
