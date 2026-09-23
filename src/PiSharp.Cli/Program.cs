using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Cli.Tui;

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
    Console.WriteLine("PiSharp (early vertical slice)\nUsage: pisharp [--local] [--print] [--continue | --session <path> | --no-session] [prompt]\n--tools <read,bash,edit,write,grep,find,ls> selects tools (grep/find/ls are opt-in); --exclude-tools <names> removes tools; --no-tools disables defaults.\n--local uses http://192.168.0.97:8000/v1 and Qwen3.8-27B-GGUF (no API key required).\nOverride with PISHARP_BASE_URL, PISHARP_MODEL, PISHARP_API_KEY. OPENAI_API_KEY is used only for OpenAI.\nInteractive: /tree, /branch <id>, /fork, /new, /name <label>, /model <id>, /session, /quit; Ctrl+C interrupts.");
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
PiAgent agent;
try { agent = new PiAgent(chat, new CodingTools(Environment.CurrentDirectory), cli.Tools, cli.ExcludeTools, cli.NoTools); }
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 2;
    return;
}
var store = new ConversationStore(Environment.CurrentDirectory);
var sessionPath = cli.NoSession ? null : cli.SessionPath is not null ? Path.GetFullPath(cli.SessionPath)
    : cli.Continue ? store.MostRecentPath() : null;
ConversationSession conversation;
ConversationRun conversationRun;
try
{
    conversation = sessionPath is not null && File.Exists(sessionPath)
        ? await store.LoadAsync(sessionPath)
        : new ConversationSession(Environment.CurrentDirectory, connection.Model, connection.Endpoint?.ToString());
    if (conversation.Endpoint != connection.Endpoint?.ToString())
        throw new InvalidDataException("Session endpoint differs from the current connection. Set PISHARP_BASE_URL to the saved endpoint first.");
    if (conversation.Model != connection.Model)
    {
        if (Environment.GetEnvironmentVariable("PISHARP_MODEL") is not null)
            throw new InvalidDataException("PISHARP_MODEL conflicts with the saved session model. Use /model after opening the session.");
        connection = connection with { Model = conversation.Model };
        agent = new PiAgent(client.GetChatClient(connection.Model).AsIChatClient(),
            new CodingTools(Environment.CurrentDirectory), cli.Tools, cli.ExcludeTools, cli.NoTools);
    }
    conversationRun = await ConversationRun.OpenAsync(agent, conversation);
    if (!cli.NoSession) sessionPath ??= store.NewPath(conversation);
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine($"Could not open session: {e.Message}");
    Environment.ExitCode = 2;
    return;
}
bool print = cli.Print || Console.IsInputRedirected || Console.IsOutputRedirected;
var prompt = cli.Prompt;
if (!print) Console.WriteLine($"PiSharp · {connection.Model} · {Environment.CurrentDirectory}\n/tree · /branch · /fork · /new · /name · /model · /session · /quit · Ctrl+C interrupts\n");
CancellationTokenSource? activeRun = null;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; activeRun?.Cancel(); };

async Task Run(string input)
{
    using var runCancel = new CancellationTokenSource();
    activeRun = runCancel;
    var started = false;
    try
    {
        await foreach (var update in conversationRun.RunStreamingAsync(input, runCancel.Token))
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
    finally
    {
        // Store the selected branch and any completed/aborted messages even when a turn fails.
        if (sessionPath is not null)
            try { await store.SaveAsync(conversation, sessionPath); }
            catch (Exception e) { Console.Error.WriteLine($"Could not save session: {e.Message}"); Environment.ExitCode = 1; }
        activeRun = null;
    }
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
    var editor = new TerminalEditor();
    while (true)
    {
        var line = editor.ReadLine();
        if (line is null || line.Trim() is "/quit" or "/exit") break;
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (line.StartsWith('/'))
        {
            try
            {
                var split = line.IndexOf(' ');
                var command = split < 0 ? line : line[..split];
                var argument = split < 0 ? "" : line[(split + 1)..].Trim();
                switch (command)
                {
                    case "/tree":
                        foreach (var node in conversation.Tree.Entries)
                            Console.WriteLine($"{(node.Id == conversation.Tree.HeadId ? '>' : ' ')} {node.Id[..12]} ← {node.ParentId?[..12] ?? "root"} {node.Type} {node.Timestamp:HH:mm:ss}");
                        break;
                    case "/branch":
                        var matches = conversation.Tree.Entries.Where(node => node.Id.StartsWith(argument, StringComparison.Ordinal)).ToArray();
                        if (argument.Length == 0 || matches.Length != 1) throw new ArgumentException("Specify a unique entry id prefix from /tree.");
                        await conversationRun.SelectAsync(matches[0].Id);
                        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        Console.WriteLine($"Selected {matches[0].Id[..12]}");
                        break;
                    case "/name":
                        conversation.Rename(argument.Length == 0 ? null : argument);
                        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        Console.WriteLine($"Name: {conversation.Name ?? "(none)"}");
                        break;
                    case "/session":
                        Console.WriteLine($"{sessionPath ?? "(ephemeral)"} · {conversation.Id} · head {conversation.Tree.HeadId ?? "(empty)"}");
                        break;
                    case "/model":
                        if (string.IsNullOrWhiteSpace(argument)) throw new ArgumentException("Specify a model ID.");
                        var nextAgent = new PiAgent(client.GetChatClient(argument).AsIChatClient(),
                            new CodingTools(Environment.CurrentDirectory), cli.Tools, cli.ExcludeTools, cli.NoTools);
                        var previousHead = conversation.Tree.HeadId;
                        try
                        {
                            conversation.SelectModel(argument, connection.Endpoint?.ToString());
                            var nextRun = await ConversationRun.OpenAsync(nextAgent, conversation);
                            if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                            agent = nextAgent;
                            conversationRun = nextRun;
                            connection = connection with { Model = argument };
                            Console.WriteLine($"Model: {argument}");
                        }
                        catch { conversation.RevertModel(connection.Model, connection.Endpoint?.ToString(), previousHead); throw; }
                        break;
                    case "/new":
                    case "/fork":
                        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        conversation = command == "/fork" ? conversation.Fork()
                            : new ConversationSession(Environment.CurrentDirectory, connection.Model, connection.Endpoint?.ToString());
                        conversationRun = await ConversationRun.OpenAsync(agent, conversation);
                        sessionPath = cli.NoSession ? null : store.NewPath(conversation);
                        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        Console.WriteLine($"{command[1..]}: {sessionPath ?? "(ephemeral)"}");
                        break;
                    default: Console.Error.WriteLine($"Unknown command: {command}"); break;
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"Session error: {e.Message}"); Environment.ExitCode = 1; }
            continue;
        }
        await Run(line);
    }
}
