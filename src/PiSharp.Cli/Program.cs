using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Cli.Tui;
using PiSharp.Cli.Protocols;

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
    Console.WriteLine("PiSharp (incomplete implementation)\nUsage: pisharp [--local] [--approve|--no-approve] [--mode interactive|print|json|rpc] [--print] [--continue | --session <path> | --no-session] [--session-dir <dir>] [--list-models] [prompt]\n--tools <read,bash,edit,write,grep,find,ls> selects tools (grep/find/ls are opt-in); --exclude-tools <names> removes tools; --no-tools disables defaults.\n--local uses http://192.168.0.97:8000/v1 and Qwen3.8-27B-GGUF (no API key required).\nOverride with PISHARP_BASE_URL, PISHARP_MODEL, PISHARP_API_KEY. OPENAI_API_KEY is used only for OpenAI.\nInteractive: /tree, /branch <id>, /fork, /clone, /new, /sessions, /resume <id>, /compact, /export [path], /name <label>, /model <id>, /models, /session, /trust yes|no|forget, /reload, /quit; Ctrl+C interrupts.");
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

using var catalogHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
async Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(CancellationToken token = default) =>
    await ModelCatalog.ListAsync(catalogHttp, connection.Endpoint, connection.ApiKey, token);
if (cli.ListModels)
{
    try
    {
        foreach (var model in await GetModelsAsync()) Console.WriteLine($"{model.Id}\t{model.Status ?? ""}\t{model.ContextLength?.ToString() ?? ""}");
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
    { Console.Error.WriteLine($"Could not list models: {error.Message}"); Environment.ExitCode = 1; }
    return;
}
var options = new OpenAIClientOptions();
if (connection.Endpoint is not null) options.Endpoint = connection.Endpoint;
var client = new OpenAIClient(new ApiKeyCredential(connection.ApiKey), options);
IChatClient chat = client.GetChatClient(connection.Model).AsIChatClient();
var agentDirectory = Environment.GetEnvironmentVariable("PISHARP_AGENT_DIR") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp", "agent");
var trustStore = new ProjectTrust(agentDirectory);
bool trusted;
string instructions;
(string? System, string? Append) prompts;
ResourceCatalog resources;
ExtensionCatalog extensions;
try
{
    trusted = await trustStore.ResolveAsync(Environment.CurrentDirectory, cli.ProjectTrustOverride,
        cli.Mode == "interactive" && !cli.Print && !Console.IsInputRedirected && !Console.IsOutputRedirected, Console.In, Console.Error);
    prompts = await ProjectPrompts.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted);
    instructions = await ContextInstructions.LoadAsync(Environment.CurrentDirectory, agentDirectory);
    resources = await ResourceCatalog.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted);
    extensions = ExtensionCatalog.Load(agentDirectory, Environment.CurrentDirectory, trusted);
    instructions += "\n" + resources.SystemInstructions();
}
catch (Exception e) when (e is not OperationCanceledException)
{
    Console.Error.WriteLine($"Could not load project resources or extensions: {e.Message}");
    Environment.ExitCode = 2;
    return;
}
using var extensionLease = new ExtensionLease(extensions);
PiAgent agent;
try
{
    agent = new PiAgent(chat, new CodingTools(Environment.CurrentDirectory), cli.Tools, cli.ExcludeTools, cli.NoTools,
    instructions, prompts.System, prompts.Append, extensionLease.Current.Registration.Tools);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 2;
    return;
}
var store = new ConversationStore(Environment.CurrentDirectory, cli.SessionDirectory ??
    Environment.GetEnvironmentVariable("PISHARP_SESSION_DIR"));
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
            new CodingTools(Environment.CurrentDirectory), cli.Tools, cli.ExcludeTools, cli.NoTools, instructions, prompts.System, prompts.Append,
            extensionLease.Current.Registration.Tools);
    }
    if (!cli.NoSession) sessionPath ??= store.NewPath(conversation);
    var initialPath = sessionPath;
    conversationRun = await ConversationRun.OpenAsync(agent, conversation, save: initialPath is null ? null :
        token => store.SaveAsync(conversation, initialPath, token));
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine($"Could not open session: {e.Message}");
    Environment.ExitCode = 2;
    return;
}
bool print = cli.Print || cli.Mode == "print" || Console.IsInputRedirected || Console.IsOutputRedirected;
var prompt = cli.Prompt;
if (!print && cli.Mode is not ("json" or "rpc")) Console.WriteLine($"PiSharp · {connection.Model} · {Environment.CurrentDirectory}\n/tree · /branch · /fork · /new · /name · /model · /session · /trust · /reload · /quit · Ctrl+C interrupts\n");
CancellationTokenSource? activeRun = null;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; activeRun?.Cancel(); };

async Task Run(string input)
{
    using var runCancel = new CancellationTokenSource();
    activeRun = runCancel;
    var started = false;
    try
    {
        var expanded = await resources.ResolveInputAsync(input, runCancel.Token);
        await foreach (var update in conversationRun.RunEventsAsync(expanded, runCancel.Token))
        {
            switch (update.Type)
            {
                case "model_text_delta" when !string.IsNullOrEmpty(update.Text):
                    Console.Write(update.Text);
                    started = true;
                    break;
                case "tool_execution_started" when !print:
                    Console.Error.WriteLine($"\n→ {update.Tool} ({update.OperationId})");
                    break;
                case "tool_execution_finished" when !print:
                    Console.Error.WriteLine($"← {(update.IsError == true ? update.Error : update.Text)}");
                    break;
                case "turn_failed" or "prompt_rejected":
                    Console.Error.WriteLine($"Agent error: {update.Error ?? update.Type}");
                    Environment.ExitCode = 1;
                    break;
                case "turn_interrupted":
                    Console.Error.WriteLine("Interrupted.");
                    break;
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

async Task ReloadResources()
{
    var nextResources = await ResourceCatalog.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted);
    var nextContext = await ContextInstructions.LoadAsync(Environment.CurrentDirectory, agentDirectory) +
        "\n" + nextResources.SystemInstructions();
    var nextPrompts = await ProjectPrompts.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted);
    var nextExtensions = ExtensionCatalog.Load(agentDirectory, Environment.CurrentDirectory, trusted);
    try
    {
        var nextAgent = new PiAgent(client.GetChatClient(connection.Model).AsIChatClient(),
            new CodingTools(Environment.CurrentDirectory), cli.Tools, cli.ExcludeTools, cli.NoTools,
            nextContext, nextPrompts.System, nextPrompts.Append, nextExtensions.Registration.Tools);
        var path = sessionPath;
        var nextRun = await ConversationRun.OpenAsync(nextAgent, conversation, save: path is null ? null :
            token => store.SaveAsync(conversation, path, token));
        extensionLease.Replace(nextExtensions);
        instructions = nextContext;
        resources = nextResources;
        prompts = nextPrompts;
        agent = nextAgent;
        conversationRun = nextRun;
    }
    catch { nextExtensions.Dispose(); throw; }
}
if (cli.Mode == "rpc")
{
    await new RpcMode(Console.In, Console.Out, conversationRun, sessionPath is null ? null :
        cancellationToken => store.SaveAsync(conversation, sessionPath, cancellationToken), resources, GetModelsAsync).ServeAsync();
    return;
}
if (cli.Mode == "json")
{
    var protocol = new JsonEventMode(Console.Out);
    await protocol.HeaderAsync(conversation);
    if (string.IsNullOrWhiteSpace(prompt) && Console.IsInputRedirected) prompt = await Console.In.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(prompt)) { Console.Error.WriteLine("A prompt is required in JSON mode."); Environment.ExitCode = 2; }
    else
    {
        string? expanded = null;
        try { expanded = await resources.ResolveInputAsync(prompt); }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            await protocol.RejectAsync(e.Message);
            Environment.ExitCode = 1;
        }
        if (expanded is not null && !await protocol.RunAsync(conversationRun, expanded)) Environment.ExitCode = 1;
        if (sessionPath is not null)
            try { await store.SaveAsync(conversation, sessionPath); }
            catch (Exception e) { Console.Error.WriteLine($"Could not save session: {e.Message}"); Environment.ExitCode = 1; }
    }
    return;
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
    var editor = new TerminalEditor(() => resources.Skills.Select(item => "/skill:" + item.Name)
        .Concat(resources.Prompts.Select(item => "/" + item.Name))
        .Concat(extensionLease.Current.Registration.Commands.Keys.Select(name => "/" + name)).ToArray());
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
                    case "/export":
                        var exportPath = argument.Length == 0 ? Path.Combine(Environment.CurrentDirectory,
                            $"pisharp-{conversation.Id[..12]}.html") : Path.GetFullPath(argument);
                        await SessionExport.ExportHtmlAsync(conversation, exportPath);
                        Console.WriteLine($"Exported private HTML to {exportPath}. Review before sharing.");
                        break;
                    case "/sessions":
                        var listings = await SessionCatalog.ListAsync(store);
                        foreach (var item in listings)
                            Console.WriteLine($"{item.Id[..12]} · {item.Name ?? "(unnamed)"} · {item.Model} · {item.MessageCount} messages · {item.ModifiedAt:yyyy-MM-dd HH:mm}");
                        if (listings.Count == 0) Console.WriteLine("No saved sessions in this project.");
                        break;
                    case "/resume":
                        if (cli.NoSession) throw new InvalidOperationException("Cannot resume in --no-session mode.");
                        if (argument.Length == 0) { Console.WriteLine("Use /sessions, then /resume <ID-prefix|exact-name>."); break; }
                        var listing = SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), argument);
                        if (listing.Path == sessionPath) { Console.WriteLine("Already in this session."); break; }
                        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        var resumedConversation = await store.LoadAsync(listing.Path);
                        if (resumedConversation.Model != connection.Model || resumedConversation.Endpoint != connection.Endpoint?.ToString())
                            throw new InvalidOperationException("Session uses another model or endpoint; open it directly with --session.");
                        var resumedRun = await ConversationRun.OpenAsync(agent, resumedConversation,
                            save: token => store.SaveAsync(resumedConversation, listing.Path, token));
                        conversation = resumedConversation;
                        conversationRun = resumedRun;
                        sessionPath = listing.Path;
                        Console.WriteLine($"Resumed {conversation.Id[..12]} · {conversation.Name ?? "(unnamed)"}");
                        break;
                    case "/models":
                        foreach (var model in (await GetModelsAsync()).Where(item =>
                            string.IsNullOrEmpty(argument) || item.Id.Contains(argument, StringComparison.OrdinalIgnoreCase)))
                            Console.WriteLine($"{model.Id}{(model.Status is null ? "" : " · " + model.Status)}{(model.ContextLength is null ? "" : " · " + model.ContextLength + " tokens")}");
                        break;
                    case "/compact":
                        Console.WriteLine(await conversationRun.CompactAsync(argument) ? "Context compacted; full history retained." :
                            "Nothing to compact (at least two completed user turns are required).");
                        break;
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
                    case "/trust":
                        if (argument.Length == 0)
                        {
                            Console.WriteLine($"Project resources: {(trusted ? "trusted" : "not trusted")}; saved decision: {(await trustStore.GetAsync(Environment.CurrentDirectory))?.ToString() ?? "none"}");
                            break;
                        }
                        bool? decision = argument switch
                        {
                            "yes" or "allow" => true,
                            "no" or "deny" => false,
                            "forget" => null,
                            _ => throw new ArgumentException("Use /trust yes, /trust no, or /trust forget.")
                        };
                        await trustStore.SetAsync(Environment.CurrentDirectory, decision);
                        trusted = decision ?? await trustStore.ResolveAsync(Environment.CurrentDirectory, null,
                            false, Console.In, Console.Error);
                        await ReloadResources();
                        Console.WriteLine($"Project resources: {(trusted ? "trusted" : "not trusted")}");
                        break;
                    case "/reload":
                        await ReloadResources();
                        Console.WriteLine("Project resources reloaded.");
                        break;
                    case "/model":
                        if (string.IsNullOrWhiteSpace(argument)) throw new ArgumentException("Specify a model ID.");
                        var nextAgent = new PiAgent(client.GetChatClient(argument).AsIChatClient(),
                            new CodingTools(Environment.CurrentDirectory), cli.Tools, cli.ExcludeTools, cli.NoTools, instructions, prompts.System, prompts.Append, extensionLease.Current.Registration.Tools);
                        var previousHead = conversation.Tree.HeadId;
                        try
                        {
                            conversation.SelectModel(argument, connection.Endpoint?.ToString());
                            var nextRun = await ConversationRun.OpenAsync(nextAgent, conversation, save: sessionPath is null ? null :
                                token => store.SaveAsync(conversation, sessionPath, token));
                            if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                            agent = nextAgent;
                            conversationRun = nextRun;
                            connection = connection with { Model = argument };
                            Console.WriteLine($"Model: {argument}");
                        }
                        catch { conversation.RevertModel(connection.Model, connection.Endpoint?.ToString(), previousHead); throw; }
                        break;
                    case "/fork":
                        var forkable = conversation.ForkableUserMessages();
                        if (argument.Length == 0)
                        {
                            foreach (var item in forkable)
                                Console.WriteLine($"{item.Id[..12]} · {new string(item.Text.Replace('\n', ' ').Take(90).Select(c => char.IsControl(c) ? ' ' : c).ToArray())}");
                            Console.WriteLine(forkable.Count == 0 ? "No text-only user messages on this branch." : "Use /fork <user-message-id> to edit a copy of its prompt in a new session.");
                            break;
                        }
                        var candidates = forkable.Where(item => item.Id.StartsWith(argument, StringComparison.Ordinal)).ToArray();
                        if (candidates.Length != 1) throw new ArgumentException("Specify a unique user message id prefix from /fork.");
                        var (forked, draft) = conversation.ForkAtUser(candidates[0].Id);
                        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        var forkPath = cli.NoSession ? null : store.NewPath(forked);
                        var forkRun = await ConversationRun.OpenAsync(agent, forked, save: forkPath is null ? null :
                            token => store.SaveAsync(forked, forkPath, token));
                        if (forkPath is not null) await store.SaveAsync(forked, forkPath);
                        conversation = forked;
                        sessionPath = forkPath;
                        conversationRun = forkRun;
                        editor.Prefill(draft);
                        Console.WriteLine($"Forked {candidates[0].Id[..12]} to {forkPath ?? "(ephemeral)"}. Edit and submit the draft prompt.");
                        break;
                    case "/new":
                    case "/clone":
                        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        conversation = command == "/clone" ? conversation.Fork()
                            : new ConversationSession(Environment.CurrentDirectory, connection.Model, connection.Endpoint?.ToString());
                        sessionPath = cli.NoSession ? null : store.NewPath(conversation);
                        var newPath = sessionPath;
                        conversationRun = await ConversationRun.OpenAsync(agent, conversation, save: newPath is null ? null :
                            token => store.SaveAsync(conversation, newPath, token));
                        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        Console.WriteLine($"{command[1..]}: {sessionPath ?? "(ephemeral)"}");
                        break;
                    default:
                        if (command.StartsWith("/skill:", StringComparison.Ordinal))
                            await Run(line);
                        else if (resources.Prompts.Any(item => "/" + item.Name == command))
                            await Run(line);
                        else if (extensionLease.Current.Registration.Commands.TryGetValue(command[1..], out var handler))
                            Console.WriteLine(await handler(argument, CancellationToken.None));
                        else Console.Error.WriteLine($"Unknown command: {command}");
                        break;
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"Session error: {e.Message}"); Environment.ExitCode = 1; }
            continue;
        }
        await Run(line);
    }
}
