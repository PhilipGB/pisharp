using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Classifiers;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
var bytes = int.Parse(args[0]);
var exitCode = int.Parse(args[1]);
var command = $"printf start; printf '%{bytes}s' '' | tr ' ' a; printf tail; exit {exitCode}";
var code = "const r=await tools.bash({command:" + JsonSerializer.Serialize(command) + "});text([r.output.length,r.truncated,r.exit_code,r.output.startsWith('start'),r.output.endsWith('tail'),typeof r.wall_time_seconds,typeof r.full_output_path]);";
var registry = new ExtensionRegistration();
CodemodeBuiltin.Configure(registry);
using var client = new ScriptClient(code);
var root = Path.Combine(Path.GetTempPath(), "pisharp-codemode-projection-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
 var conversation = new ConversationSession(root,"chat",null);
 var agent = new PiAgent(client,new CodingTools(root),selectedTools:["bash","codemode"],extensionToolRegistrations:registry.ToolDefinitions);
 var run = await ConversationRun.OpenAsync(agent,conversation);
 var events = new List<AgentLifecycleEvent>();
 await foreach(var item in run.RunEventsAsync("run")) events.Add(item);
 var result = events.Single(e=>e.Type=="tool_execution_finished"&&e.Tool=="codemode");
 var bash = events.Single(e=>e.Type=="tool_execution_finished"&&e.Tool=="bash");
 Console.WriteLine(JsonSerializer.Serialize(new {output=result.Text!.Trim(),isError=result.IsError==true,bashError=bash.IsError==true}));
 if(bash.Details is {} details && JsonSerializer.SerializeToElement(details).TryGetProperty("fullOutputPath",out var file)&&file.ValueKind==JsonValueKind.String)File.Delete(file.GetString()!);
} finally {Directory.Delete(root,true);}
sealed class ScriptClient(string code):IChatClient {
 int requests;
 public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken token=default)=>throw new NotSupportedException();
 public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,[EnumeratorCancellation]CancellationToken cancellationToken=default) {
  if(Interlocked.Increment(ref requests)==1)yield return new(ChatRole.Assistant,[new FunctionCallContent("script","codemode",new Dictionary<string,object?>{["code"]=code})]);else yield return new(ChatRole.Assistant,"done");await Task.CompletedTask;
 }
 public object? GetService(Type type,object? key=null)=>null;
 public void Dispose(){}
}
