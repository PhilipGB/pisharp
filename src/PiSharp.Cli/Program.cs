using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Cli;

var model = Environment.GetEnvironmentVariable("PISHARP_MODEL") ?? "gpt-4o-mini";
var endpoint = Environment.GetEnvironmentVariable("PISHARP_BASE_URL");
var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? Environment.GetEnvironmentVariable("PISHARP_API_KEY");
if (args.Contains("--help"))
{
    Console.WriteLine("PiSharp (early vertical slice)\nUsage: pisharp [--print] [prompt]\nSet OPENAI_API_KEY (or PISHARP_API_KEY), PISHARP_MODEL, optionally PISHARP_BASE_URL for OpenAI-compatible Chat Completions.\nInteractive: /quit to exit, Ctrl+C to cancel current run.");
    return;
}
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("Missing OPENAI_API_KEY or PISHARP_API_KEY. Use --help for setup.");
    Environment.ExitCode = 2;
    return;
}

var options = new OpenAIClientOptions();
if (!string.IsNullOrWhiteSpace(endpoint)) options.Endpoint = new Uri(endpoint);
var client = new OpenAIClient(new ApiKeyCredential(key), options);
IChatClient chat = client.GetChatClient(model).AsIChatClient();
var agent = new PiAgent(chat, new CodingTools(Environment.CurrentDirectory));
var session = await agent.CreateSessionAsync();
bool print = args.Contains("--print") || Console.IsInputRedirected || Console.IsOutputRedirected;
var prompt = string.Join(" ", args.Where(a => a != "--print"));
if (!print) Console.WriteLine($"PiSharp · {model} · {Environment.CurrentDirectory}\n/quit to exit · Ctrl+C to interrupt\n");
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
