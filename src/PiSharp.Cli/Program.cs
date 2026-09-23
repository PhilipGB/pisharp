using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Cli;

if (args.Contains("--help"))
{
    Console.WriteLine("PiSharp (early vertical slice)\nUsage: pisharp [--local] [--print] [prompt]\n--local uses http://192.168.0.98:8000/v1 and Qwen3.8-27B-GGUF (no API key required).\nOverride with PISHARP_BASE_URL, PISHARP_MODEL, PISHARP_API_KEY. OPENAI_API_KEY is used only for OpenAI.\nInteractive: /quit to exit, Ctrl+C to cancel current run.");
    return;
}
ConnectionSettings connection;
try { connection = ConnectionSettings.Resolve(args.Contains("--local"), Environment.GetEnvironmentVariable); }
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 2;
    return;
}

var options = new OpenAIClientOptions();
if (connection.Endpoint is not null) options.Endpoint = connection.Endpoint;
var client = new OpenAIClient(new ApiKeyCredential(connection.ApiKey), options);
IChatClient chat = client.GetChatClient(connection.Model).AsIChatClient();
var agent = new PiAgent(chat, new CodingTools(Environment.CurrentDirectory));
var session = await agent.CreateSessionAsync();
bool print = args.Contains("--print") || Console.IsInputRedirected || Console.IsOutputRedirected;
var prompt = string.Join(" ", args.Where(a => a != "--print" && a != "--local"));
if (!print) Console.WriteLine($"PiSharp · {connection.Model} · {Environment.CurrentDirectory}\n/quit to exit · Ctrl+C to interrupt\n");
CancellationTokenSource? activeRun = null;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; activeRun?.Cancel(); };

async Task Run(string input)
{
    using var runCancel = new CancellationTokenSource();
    activeRun = runCancel;
    var started = false;
    try
    {
        await foreach (var update in agent.RunStreamingAsync(input, session, runCancel.Token))
        {
            if (update.Contents is not null)
                foreach (var content in update.Contents)
                    if (!print && content is FunctionCallContent call)
                        Console.Error.WriteLine($"\n→ {call.Name}({call.Arguments})");
                    else if (!print && content is FunctionResultContent result)
                        Console.Error.WriteLine($"← {result.Result}");
            if (!string.IsNullOrEmpty(update.Text))
            {
                Console.Write(update.Text);
                started = true;
            }
        }
        if (started) Console.WriteLine();
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Interrupted."); }
    catch (Exception ex) { Console.Error.WriteLine($"Agent error: {ex.Message}"); Environment.ExitCode = 1; }
    finally { activeRun = null; }
}

if (print)
{
    if (string.IsNullOrWhiteSpace(prompt) && Console.IsInputRedirected) prompt = await Console.In.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(prompt)) { Console.Error.WriteLine("A prompt is required in print mode."); Environment.ExitCode = 2; }
    else await Run(prompt);
}
else
{
    if (!string.IsNullOrWhiteSpace(prompt)) await Run(prompt);
    while (true)
    {
        Console.Write("❯ ");
        var line = Console.ReadLine();
        if (line is null || line.Trim() is "/quit" or "/exit") break;
        if (!string.IsNullOrWhiteSpace(line)) await Run(line);
    }
}
