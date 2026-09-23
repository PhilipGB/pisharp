using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Cli;
using PiSharp.Runtime;

CliArguments cli;
try { cli = CliArguments.Parse(args); }
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 2;
    return;
}
if (cli.Help)
{
    Console.WriteLine("PiSharp (early vertical slice)\nUsage: pisharp [--local] [--print] [--continue | --session <path> | --no-session] [prompt]\n--local uses http://192.168.0.97:8000/v1 and Qwen3.8-27B-GGUF (no API key required).\nOverride with PISHARP_BASE_URL, PISHARP_MODEL, PISHARP_API_KEY. OPENAI_API_KEY is used only for OpenAI.\nInteractive: /quit to exit, Ctrl+C to cancel current run.");
    return;
}
ConnectionSettings connection;
try { connection = ConnectionSettings.Resolve(cli.Local, Environment.GetEnvironmentVariable); }
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
var snapshots = new SessionSnapshots(Environment.CurrentDirectory, connection.Model, connection.Endpoint?.ToString());
var snapshotPath = cli.NoSession ? null : cli.SessionPath is not null ? Path.GetFullPath(cli.SessionPath)
    : cli.Continue ? snapshots.MostRecentPath() : null;
Microsoft.Agents.AI.AgentSession session;
try
{
    session = snapshotPath is not null
        ? await snapshots.LoadAsync(agent, snapshotPath)
        : await agent.CreateSessionAsync();
    if (!cli.NoSession) snapshotPath ??= snapshots.NewPath();
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine($"Could not open session: {e.Message}");
    Environment.ExitCode = 2;
    return;
}
bool print = cli.Print || Console.IsInputRedirected || Console.IsOutputRedirected;
var prompt = cli.Prompt;
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
        if (snapshotPath is not null) await snapshots.SaveAsync(agent, session, snapshotPath, runCancel.Token);
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
