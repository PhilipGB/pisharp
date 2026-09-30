using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Classifiers;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
var fail = args[0] == "true";
var code = "const m=await models.getModelOfType('classifier','fixture','one');text(typeof m.headers);m.baseUrl='https://attacker.invalid';m.headers={authorization:'guest'};const r=await models.classify(m,{state:{value:7},questions:{q:{type:'bool',instructions:'safe?',criteria:{}}}});text(r.answers.q.probability);text([r.usage.input,r.usage.output,r.usage.totalTokens,r.usage.cost.total]);" + (fail ? "throw new Error('after billing');" : "");
var registry = new ExtensionRegistration();
CodemodeBuiltin.Configure(registry);
using var client = new ScriptClient(code);
var root = Path.Combine(Path.GetTempPath(), "pisharp-codemode-projection-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
 var conversation = new ConversationSession(root,"chat",null);
 var agent = new PiAgent(client,new CodingTools(root),selectedTools:["codemode"],noBuiltinTools:true,extensionToolRegistrations:registry.ToolDefinitions,codemodeModels:new Models());
 var run = await ConversationRun.OpenAsync(agent,conversation);
 var events = new List<AgentLifecycleEvent>();
 await foreach(var item in run.RunEventsAsync("run")) events.Add(item);
 var result = events.Single(e=>e.Type=="tool_execution_finished"&&e.Tool=="codemode");
 var billed = conversation.ActiveUsage().Single();
 Console.WriteLine(JsonSerializer.Serialize(new {output=string.Join('\n',result.Text!.Split('\n').Where(s=>s is "undefined" or "0.8" || s.StartsWith("[100,", StringComparison.Ordinal))),isError=result.IsError==true,input=billed.InputTokens,outputTokens=billed.OutputTokens,totalTokens=billed.TotalTokens,cost=billed.Cost,calls=result.NestedToolCalls!.Calls.Select(c=>new{name=c.Name,status=c.Status,cost=c.Cost})}));
} finally {Directory.Delete(root,true);}
sealed class Models : ICodemodeModels {
 public IReadOnlyList<JsonElement> GetModels(string type,string? provider=null)=>[JsonSerializer.SerializeToElement(new{type="classifier",provider="fixture",id="one",headers=new{authorization="host-secret"}})];
 public Task<IReadOnlyList<JsonElement>> GetAvailableAsync(string type,string? provider,CancellationToken token)=>Task.FromResult(GetModels(type,provider));
 public Task<ClassifierResult> ClassifyAsync(string provider,string id,ClassifierContext context,CancellationToken token) {
  if(provider!="fixture"||id!="one"||context.State.GetProperty("value").GetInt32()!=7)throw new InvalidOperationException("Incorrect host authority or state");
  return Task.FromResult(new ClassifierResult("typesafe-system-one",provider,id,new Dictionary<string,ClassifierAnswer>{["q"]=new ClassifierBoolAnswer(.8)},"stop",new UsageRecord(id,"classifier",100,10,0,0,110,.00012m)));
 }
}
sealed class ScriptClient(string code):IChatClient {
 int requests;
 public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken token=default)=>throw new NotSupportedException();
 public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,[EnumeratorCancellation]CancellationToken cancellationToken=default) {
  if(Interlocked.Increment(ref requests)==1)yield return new(ChatRole.Assistant,[new FunctionCallContent("script","codemode",new Dictionary<string,object?>{["code"]=code})]);else yield return new(ChatRole.Assistant,"done");await Task.CompletedTask;
 }
 public object? GetService(Type type,object? key=null)=>null;
 public void Dispose(){}
}
