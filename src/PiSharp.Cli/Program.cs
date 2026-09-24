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

var agentDirectory = Environment.GetEnvironmentVariable("PISHARP_AGENT_DIR") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp", "agent");
if (args.Length > 0 && args[0] == "auth")
{
    Environment.ExitCode = await AuthStatusCommand.RunAsync(args[1..], agentDirectory,
        Environment.GetEnvironmentVariable, Console.Out, Console.Error);
    return;
}
if (args.Length > 0 && args[0] == "--export")
{
    Environment.ExitCode = await SessionExportCommand.RunAsync(args[1..], Console.Out, Console.Error);
    return;
}
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
    Console.WriteLine("PiSharp (incomplete implementation)\nUsage: pisharp [--local | --provider <id>] [--model <id>] [--models <globs>] [--thinking <level>] [--api-key <key>] [--list-models] [--system-prompt <text|file>] [--append-system-prompt <text|file>] [--no-context-files] [--approve|--no-approve] [--mode interactive|print|json|rpc] [--print] [--continue | --session <path|project-id> | --fork <path|project-id> | --no-session] [--session-dir <dir>] [--name <label>] [prompt] [@files...]\n--tools <read,bash,edit,write,grep,find,ls> selects tools (grep/find/ls are opt-in); --exclude-tools <names> removes tools; --no-tools disables defaults (including extension tools); --no-builtin-tools disables only default built-ins.\nProviders and static model metadata may be configured in $PISHARP_AGENT_DIR/models.json. Credentials are read from environment or private auth.json; --api-key is runtime-only.\nOffline credential status: pisharp auth check --provider <id> [--model <configured-exact-id>] [--local]; explicit pisharp auth print-api-key --provider <id> prints an API key to stdout.\nStandalone private HTML: pisharp --export <PiSharp-session-file> [output.html]; never overwrites.\n--local uses http://192.168.0.97:8000/v1 and Qwen3.8-27B-GGUF (no API key required).\nInteractive: /model, /models, /thinking, /scoped-models, /login, /logout, /tree, /branch, /fork, /clone, /new, /sessions, /resume, /delete-session, /compact, /export, /name, /session, /trust, /reload, /quit.");
    return;
}
var trustStore = new ProjectTrust(agentDirectory);
bool trusted;
UserSettings userSettings;
try
{
    userSettings = await UserSettings.LoadAsync(agentDirectory, Environment.GetEnvironmentVariable);
    trusted = await trustStore.ResolveAsync(Environment.CurrentDirectory, cli.ProjectTrustOverride,
        cli.Mode == "interactive" && !cli.Print && !Console.IsInputRedirected && !Console.IsOutputRedirected, Console.In, Console.Error,
        defaultProjectTrust: userSettings.DefaultProjectTrust ?? "ask");
    if (trusted)
        userSettings = userSettings.Overlay(await UserSettings.LoadProjectAsync(Environment.CurrentDirectory));
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}
cli = userSettings.ApplyDefaults(cli, Environment.GetEnvironmentVariable,
    preserveSessionModel: cli.Continue || cli.SessionPath is not null || cli.ForkSource is not null || cli.ListModels);
using var catalogHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
ProviderModelRuntime modelRuntime;
ModelSelection selection;
ConnectionSettings connection;
var thinking = cli.Thinking ?? "off";
try
{
    modelRuntime = await ProviderModelRuntime.CreateAsync(agentDirectory, cli.Local || userSettings.DefaultProvider == "local",
        Environment.GetEnvironmentVariable, catalogHttp, cli.ApiKey, cli.ScopedModels);
    selection = await modelRuntime.ResolveAsync(cli.Provider ?? (cli.Local ? "local" : null), cli.ModelOverride);
    thinking = ThinkingLevels.ValidateForModel(thinking, selection.Model.Reasoning);
    connection = selection.Connection;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}
async Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(CancellationToken token = default) =>
    await modelRuntime.ListModelsAsync(cli.Provider, token);
if (cli.ListModels)
{
    foreach (var model in await GetModelsAsync())
        Console.WriteLine($"{model.Provider}/{model.Id}\t{(model.Available ? model.Status ?? "available" : model.UnavailableReason ?? "unavailable")}\t{model.ContextLength?.ToString() ?? ""}");
    return;
}
OpenAIClient CreateClient(ModelSelection selected)
{
    var options = new OpenAIClientOptions();
    if (selected.Connection.Endpoint is not null) options.Endpoint = selected.Connection.Endpoint;
    return new OpenAIClient(new ApiKeyCredential(selected.ApiKey), options);
}
var client = CreateClient(selection);
IChatClient chat = client.GetChatClient(connection.Model).AsIChatClient();
string instructions;
(string? System, string? Append) prompts;
ResourceCatalog resources;
ExtensionCatalog extensions;
try
{
    prompts = await CliPromptOverrides.ResolveAsync(cli,
        await ProjectPrompts.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted), Environment.CurrentDirectory);
    instructions = cli.NoContextFiles ? "" : await ContextInstructions.LoadAsync(Environment.CurrentDirectory, agentDirectory);
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
    instructions, prompts.System, prompts.Append, extensionLease.Current.Registration.Tools,
    reasoning: ThinkingLevels.ToOptions(thinking), blockImages: userSettings.BlockImages == true, noBuiltinTools: cli.NoBuiltinTools);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 2;
    return;
}
var store = new ConversationStore(Environment.CurrentDirectory, cli.SessionDirectory ??
    Environment.GetEnvironmentVariable("PISHARP_SESSION_DIR") ?? userSettings.SessionDirectory);
AutoCompactionPolicy? contextPolicy;
ModelPricing? modelPricing;
try
{
    contextPolicy = userSettings.ResolveCompaction(selection.Model.ContextLength, Environment.GetEnvironmentVariable, $"{selection.Provider.Id}/{selection.Model.Id}");
    modelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? selection.Model.Pricing;
}
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 2; return; }
Task<ConversationRun> OpenRunAsync(PiAgent runningAgent, ConversationSession session, string? path) =>
    ConversationRun.OpenAsync(runningAgent, session, save: path is null ? null :
        token => store.SaveAsync(session, path, token), autoCompaction: contextPolicy, pricing: modelPricing);
var sessionPath = cli.NoSession || cli.ForkSource is not null ? null : cli.SessionPath is not null &&
    (cli.SessionPath.Contains(Path.DirectorySeparatorChar) || cli.SessionPath.EndsWith(".session.json", StringComparison.Ordinal))
    ? Path.GetFullPath(cli.SessionPath) : cli.Continue ? store.MostRecentPath() : null;
ConversationSession conversation;
ConversationRun conversationRun;
try
{
    if (cli.SessionPath is not null && sessionPath is null)
        sessionPath = SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), cli.SessionPath).Path;
    if (cli.ForkSource is not null)
    {
        var sourcePath = cli.ForkSource.Contains(Path.DirectorySeparatorChar) || cli.ForkSource.EndsWith(".session.json", StringComparison.Ordinal)
            ? Path.GetFullPath(cli.ForkSource)
            : SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), cli.ForkSource).Path;
        conversation = (await store.LoadAsync(sourcePath)).Fork();
    }
    else
        conversation = sessionPath is not null && File.Exists(sessionPath)
            ? await store.LoadAsync(sessionPath)
            : new ConversationSession(Environment.CurrentDirectory, connection.Model, connection.Endpoint?.ToString(), selection.Provider.Id);
    var explicitConnection = cli.Local || cli.Provider is not null || cli.ModelOverride is not null ||
        Environment.GetEnvironmentVariable("PISHARP_MODEL") is not null || Environment.GetEnvironmentVariable("PISHARP_BASE_URL") is not null;
    var savedProvider = conversation.Provider ?? modelRuntime.Providers.FirstOrDefault(item =>
        string.Equals(item.Endpoint.ToString().TrimEnd('/'), conversation.Endpoint?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.Id ??
        (conversation.Endpoint is null ? "openai" : null);
    if (savedProvider is null)
        throw new InvalidDataException("Session provider cannot be resolved from models.json or the saved endpoint.");
    if (explicitConnection && (conversation.Model != connection.Model || conversation.Endpoint != connection.Endpoint?.ToString() ||
        conversation.Provider is not null && !conversation.Provider.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase)))
        throw new InvalidDataException("Requested provider/model conflicts with the saved session model or endpoint. Open it without explicit model flags and use /model after opening.");
    if (!explicitConnection && (conversation.Model != connection.Model || conversation.Endpoint != connection.Endpoint?.ToString() ||
        !savedProvider.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase)))
    {
        selection = await modelRuntime.ResolveAsync(savedProvider, conversation.Model);
        connection = selection.Connection;
        thinking = ThinkingLevels.ValidateForModel(thinking, selection.Model.Reasoning);
        client = CreateClient(selection);
        contextPolicy = userSettings.ResolveCompaction(selection.Model.ContextLength, Environment.GetEnvironmentVariable, $"{selection.Provider.Id}/{selection.Model.Id}");
        modelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? selection.Model.Pricing;
        agent = new PiAgent(client.GetChatClient(connection.Model).AsIChatClient(),
            new CodingTools(Environment.CurrentDirectory), cli.Tools, cli.ExcludeTools, cli.NoTools, instructions, prompts.System, prompts.Append,
            extensionLease.Current.Registration.Tools, reasoning: ThinkingLevels.ToOptions(thinking), blockImages: userSettings.BlockImages == true, noBuiltinTools: cli.NoBuiltinTools);
    }
    if (cli.SessionName is not null) conversation.Rename(cli.SessionName);
    if (!cli.NoSession) sessionPath ??= store.NewPath(conversation);
    var initialPath = sessionPath;
    conversationRun = await OpenRunAsync(agent, conversation, initialPath);
    if ((cli.SessionName is not null || cli.ForkSource is not null) && sessionPath is not null)
        await store.SaveAsync(conversation, sessionPath);
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine($"Could not open session: {e.Message}");
    Environment.ExitCode = 2;
    return;
}
bool print = cli.Print || cli.Mode == "print" || Console.IsInputRedirected || Console.IsOutputRedirected;
var prompt = cli.Prompt;
// Pi combines trimmed piped input before @file content and the positional prompt.
// RPC owns stdin as a command stream and must not consume it here.
var stdinContent = cli.Mode != "rpc" && Console.IsInputRedirected ? (await Console.In.ReadToEndAsync()).Trim() : "";
if (!selection.Authenticated && (print || cli.Mode is "json" or "rpc" || !string.IsNullOrWhiteSpace(prompt)))
{
    Console.Error.WriteLine($"Provider '{selection.Provider.Id}' is not authenticated. Use /login {selection.Provider.Id} in an interactive terminal or configure {selection.Provider.ApiKeyEnvironment ?? "a credential"}.");
    Environment.ExitCode = 2;
    return;
}
TerminalEditor? editor = !print && cli.Mode is not ("json" or "rpc") ? new TerminalEditor(() =>
    resources.Skills.Select(item => "/skill:" + item.Name)
        .Concat(resources.Prompts.Select(item => "/" + item.Name))
        .Concat(extensionLease.Current.Registration.Commands.Keys.Select(name => "/" + name)).ToArray()) : null;
if (editor is not null) Console.WriteLine($"PiSharp · {selection.Provider.Id}/{connection.Model} · thinking {thinking} · {Environment.CurrentDirectory}\n/model · /thinking · /scoped-models · /login · /logout · /tree · /fork · /new · /session · /quit · Escape interrupts; Enter steers; Alt+Enter follows up\n");
CancellationTokenSource? activeRun = null;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; activeRun?.Cancel(); };

async Task Run(string input, IReadOnlyList<DataContent>? images = null)
{
    using var runCancel = new CancellationTokenSource();
    activeRun = runCancel;
    using var monitorStop = new CancellationTokenSource();
    var monitor = Task.CompletedTask;
    var monitorStarted = false;
    var started = false;
    try
    {
        if (!selection.Authenticated)
            throw new InvalidOperationException($"Provider '{selection.Provider.Id}' is not authenticated. Use /login {selection.Provider.Id} before sending a prompt.");
        if (images is { Count: > 0 } && userSettings.BlockImages != true && selection.Model.Input is { } modalities &&
            !modalities.Contains("image", StringComparer.Ordinal))
            throw new InvalidOperationException($"Model '{selection.Model.Id}' is not declared image-capable; refusing to send image attachments.");
        var expanded = await resources.ResolveInputAsync(input, runCancel.Token);
        var contents = new List<AIContent> { new TextContent(expanded) };
        if (images is not null) contents.AddRange(images);
        await foreach (var update in conversationRun.RunEventsAsync(new ChatMessage(ChatRole.User, contents), expanded, runCancel.Token))
        {
            switch (update.Type)
            {
                case "prompt_accepted" when editor is not null && !monitorStarted:
                    monitorStarted = true;
                    monitor = editor.MonitorRunAsync(async (text, followUp, token) =>
                    {
                        try
                        {
                            var queued = await resources.ResolveInputAsync(text, token);
                            var accepted = followUp ? conversationRun.TryFollowUp(queued) : conversationRun.TrySteer(queued);
                            if (accepted) Console.Error.WriteLine(followUp ? "Queued follow-up." : "Queued steering message.");
                            return accepted;
                        }
                        catch (Exception error) when (error is ArgumentException or IOException)
                        {
                            Console.Error.WriteLine($"Could not queue input: {error.Message}");
                            return false;
                        }
                    }, () => conversationRun.ClearPendingPrompts().InDeliveryOrder, runCancel.Cancel, monitorStop.Token);
                    break;
                case "model_text_delta" when !string.IsNullOrEmpty(update.Text):
                    Console.Write(update.Text);
                    started = true;
                    break;
                case "reasoning_delta" when !print && !string.IsNullOrEmpty(update.Text):
                    Console.Error.Write(update.Text);
                    break;
                case "usage" when !print && !string.IsNullOrEmpty(update.Text):
                    Console.Error.WriteLine($"\nUsage: {update.Text}");
                    break;
                case "context_compacted" when !print:
                    Console.Error.WriteLine(update.Text);
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
        monitorStop.Cancel();
        try { await monitor; }
        catch (OperationCanceledException) { }
        // Store the selected branch and any completed/aborted messages even when a turn fails.
        if (sessionPath is not null)
            try { await store.SaveAsync(conversation, sessionPath); }
            catch (Exception e) { Console.Error.WriteLine($"Could not save session: {e.Message}"); Environment.ExitCode = 1; }
        activeRun = null;
    }
}

async Task ReplaceModelRuntime(ModelSelection nextSelection, string nextThinking, bool recordModelChange,
    bool requireAuthenticated = true)
{
    if (requireAuthenticated && !nextSelection.Authenticated)
        throw new InvalidOperationException($"Provider '{nextSelection.Provider.Id}' is not authenticated. Use /login {nextSelection.Provider.Id}.");
    nextThinking = ThinkingLevels.ValidateForModel(nextThinking, nextSelection.Model.Reasoning);
    var nextConnection = nextSelection.Connection;
    var nextClient = CreateClient(nextSelection);
    var nextPolicy = userSettings.ResolveCompaction(nextSelection.Model.ContextLength, Environment.GetEnvironmentVariable, $"{nextSelection.Provider.Id}/{nextSelection.Model.Id}");
    var nextPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? nextSelection.Model.Pricing;
    var nextAgent = new PiAgent(nextClient.GetChatClient(nextConnection.Model).AsIChatClient(),
        new CodingTools(Environment.CurrentDirectory), cli.Tools, cli.ExcludeTools, cli.NoTools,
        instructions, prompts.System, prompts.Append, extensionLease.Current.Registration.Tools,
        reasoning: ThinkingLevels.ToOptions(nextThinking), blockImages: userSettings.BlockImages == true, noBuiltinTools: cli.NoBuiltinTools);
    var previousHead = conversation.Tree.HeadId;
    var previousSelection = selection;
    var previousConnection = connection;
    var previousPolicy = contextPolicy;
    var previousPricing = modelPricing;
    try
    {
        if (recordModelChange)
            conversation.SelectModel(nextConnection.Model, nextConnection.Endpoint?.ToString(), nextSelection.Provider.Id);
        contextPolicy = nextPolicy;
        modelPricing = nextPricing;
        var nextRun = await OpenRunAsync(nextAgent, conversation, sessionPath);
        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
        selection = nextSelection;
        connection = nextConnection;
        client = nextClient;
        agent = nextAgent;
        conversationRun = nextRun;
        thinking = nextThinking;
    }
    catch
    {
        contextPolicy = previousPolicy;
        modelPricing = previousPricing;
        if (recordModelChange)
            conversation.RevertModel(previousConnection.Model, previousConnection.Endpoint?.ToString(), previousHead,
                previousSelection.Provider.Id);
        throw;
    }
}

async Task ReloadResources()
{
    var nextResources = await ResourceCatalog.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted);
    var nextContext = (cli.NoContextFiles ? "" : await ContextInstructions.LoadAsync(Environment.CurrentDirectory, agentDirectory)) +
        "\n" + nextResources.SystemInstructions();
    var nextPrompts = await CliPromptOverrides.ResolveAsync(cli,
        await ProjectPrompts.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted), Environment.CurrentDirectory);
    var nextExtensions = ExtensionCatalog.Load(agentDirectory, Environment.CurrentDirectory, trusted);
    try
    {
        var nextAgent = new PiAgent(client.GetChatClient(connection.Model).AsIChatClient(),
            new CodingTools(Environment.CurrentDirectory), cli.Tools, cli.ExcludeTools, cli.NoTools,
            nextContext, nextPrompts.System, nextPrompts.Append, nextExtensions.Registration.Tools,
            reasoning: ThinkingLevels.ToOptions(thinking), blockImages: userSettings.BlockImages == true, noBuiltinTools: cli.NoBuiltinTools);
        var path = sessionPath;
        var nextRun = await OpenRunAsync(nextAgent, conversation, path);
        extensionLease.Replace(nextExtensions);
        instructions = nextContext;
        resources = nextResources;
        prompts = nextPrompts;
        agent = nextAgent;
        conversationRun = nextRun;
    }
    catch { nextExtensions.Dispose(); throw; }
}
string? ReadSecret()
{
    if (Console.IsInputRedirected) return Console.ReadLine();
    var value = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return value.ToString(); }
        if (key.Key is ConsoleKey.Escape) { Console.Error.WriteLine(); return null; }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length > 0) value.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
    }
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
    if (string.IsNullOrWhiteSpace(stdinContent + prompt) && (cli.FileArguments?.Count ?? 0) == 0) { Console.Error.WriteLine("A prompt is required in JSON mode."); Environment.ExitCode = 2; }
    else
    {
        CliFileArguments.PromptFiles? expanded = null;
        try
        {
            var resolved = await resources.ResolveInputAsync(prompt);
            expanded = await CliFileArguments.ProcessFilesAsync(resolved, cli.FileArguments, Environment.CurrentDirectory);
            expanded = expanded with { Text = stdinContent + expanded.Text };
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            await protocol.RejectAsync(e.Message);
            Environment.ExitCode = 1;
        }
        if (expanded is not null && string.IsNullOrWhiteSpace(expanded.Text) && expanded.Images.Count == 0)
        {
            await protocol.RejectAsync("A nonempty prompt is required.");
            Environment.ExitCode = 2;
        }
        else if (expanded is not null)
        {
            var contents = new List<AIContent> { new TextContent(expanded.Text) };
            contents.AddRange(expanded.Images);
            if (!await protocol.RunAsync(conversationRun, new ChatMessage(ChatRole.User, contents), expanded.Text)) Environment.ExitCode = 1;
        }
        if (sessionPath is not null)
            try { await store.SaveAsync(conversation, sessionPath); }
            catch (Exception e) { Console.Error.WriteLine($"Could not save session: {e.Message}"); Environment.ExitCode = 1; }
    }
    return;
}
if (print)
{
    CliFileArguments.PromptFiles promptFiles;
    try { promptFiles = await CliFileArguments.ProcessFilesAsync(prompt, cli.FileArguments, Environment.CurrentDirectory); }
    catch (Exception e) when (e is IOException or ArgumentException) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; return; }
    promptFiles = promptFiles with { Text = stdinContent + promptFiles.Text };
    if (string.IsNullOrWhiteSpace(promptFiles.Text) && promptFiles.Images.Count == 0) { Console.Error.WriteLine("A prompt is required in print mode."); Environment.ExitCode = 2; }
    else await Run(promptFiles.Text, promptFiles.Images);
}
else
{
    if (!string.IsNullOrWhiteSpace(prompt) || (cli.FileArguments?.Count ?? 0) > 0)
    {
        CliFileArguments.PromptFiles promptFiles;
        try { promptFiles = await CliFileArguments.ProcessFilesAsync(prompt, cli.FileArguments, Environment.CurrentDirectory); }
        catch (Exception e) when (e is IOException or ArgumentException) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; return; }
        if (!string.IsNullOrWhiteSpace(promptFiles.Text) || promptFiles.Images.Count > 0)
            await Run(promptFiles.Text, promptFiles.Images);
    }
    while (true)
    {
        var line = editor!.ReadLine();
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
                        var listings = SessionCatalog.Search(await SessionCatalog.ListAsync(store), argument);
                        foreach (var item in listings)
                            Console.WriteLine($"{item.Id[..12]} · {item.Name ?? "(unnamed)"} · {item.Model} · {item.MessageCount} messages · {item.ModifiedAt:yyyy-MM-dd HH:mm}");
                        if (listings.Count == 0) Console.WriteLine("No saved sessions in this project.");
                        break;
                    case "/delete-session":
                        if (cli.NoSession) throw new InvalidOperationException("There are no saved sessions in --no-session mode.");
                        var victim = SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), argument);
                        if (victim.Path == sessionPath) throw new InvalidOperationException("Switch sessions before deleting the active session.");
                        Console.WriteLine($"Delete {victim.Id[..12]} · {victim.Name ?? "(unnamed)"}? Type delete {victim.Id[..12]} to confirm:");
                        if (editor.ReadLine()?.Trim() != "delete " + victim.Id[..12])
                        {
                            Console.WriteLine("Deletion cancelled.");
                            break;
                        }
                        await store.DeleteAsync(victim);
                        Console.WriteLine($"Deleted {victim.Id[..12]} permanently.");
                        break;
                    case "/resume":
                        if (cli.NoSession) throw new InvalidOperationException("Cannot resume in --no-session mode.");
                        if (argument.Length == 0) { Console.WriteLine("Use /sessions, then /resume <ID-prefix|exact-name>."); break; }
                        var listing = SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), argument);
                        if (listing.Path == sessionPath) { Console.WriteLine("Already in this session."); break; }
                        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        var resumedConversation = await store.LoadAsync(listing.Path);
                        if (resumedConversation.Model != connection.Model || resumedConversation.Endpoint != connection.Endpoint?.ToString() ||
                            resumedConversation.Provider is not null && !resumedConversation.Provider.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Session uses another provider, model, or endpoint; open it directly with --session.");
                        var resumedRun = await OpenRunAsync(agent, resumedConversation, listing.Path);
                        conversation = resumedConversation;
                        conversationRun = resumedRun;
                        sessionPath = listing.Path;
                        Console.WriteLine($"Resumed {conversation.Id[..12]} · {conversation.Name ?? "(unnamed)"}");
                        break;
                    case "/models":
                        foreach (var model in (await GetModelsAsync()).Where(item => string.IsNullOrEmpty(argument) ||
                            item.Id.Contains(argument, StringComparison.OrdinalIgnoreCase) ||
                            (item.Provider?.Contains(argument, StringComparison.OrdinalIgnoreCase) ?? false)))
                            Console.WriteLine($"{model.Provider}/{model.Id} · {(model.Available ? model.Status ?? "available" : model.UnavailableReason ?? "unavailable")}" +
                                $"{(model.Reasoning == true ? " · reasoning" : "")}{(model.ContextLength is null ? "" : " · " + model.ContextLength + " tokens")}");
                        break;
                    case "/scoped-models":
                        if (argument.Length > 0)
                            modelRuntime.SetScope(argument.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        Console.WriteLine(modelRuntime.Scope.Count == 0 ? "Scoped models: all" :
                            "Scoped models: " + string.Join(", ", modelRuntime.Scope));
                        foreach (var scoped in await GetModelsAsync())
                            Console.WriteLine($"  {scoped.Provider}/{scoped.Id}{(scoped.Available ? "" : " · unavailable")}");
                        break;
                    case "/thinking":
                        if (argument.Length == 0)
                        {
                            Console.WriteLine($"Thinking: {thinking}; available: " +
                                (selection.Model.Reasoning == true ? string.Join(", ", ThinkingLevels.All) : "off"));
                            break;
                        }
                        await ReplaceModelRuntime(selection, argument, recordModelChange: false);
                        Console.WriteLine($"Thinking: {thinking}");
                        break;
                    case "/login":
                        var loginParts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var loginProvider = loginParts.ElementAtOrDefault(0) ?? selection.Provider.Id;
                        var loginType = loginParts.ElementAtOrDefault(1) ?? "api-key";
                        if (loginParts.Length > 2 || loginType is not ("api-key" or "oauth"))
                            throw new ArgumentException("Use /login [provider] [api-key|oauth]. The secret is prompted and must not be included in the command.");
                        var loginProfile = modelRuntime.GetProvider(loginProvider);
                        if (loginType == "oauth" && !loginProfile.OAuthSupported)
                            throw new InvalidOperationException($"Provider '{loginProvider}' has no configured OAuth adapter; browser authorization is not implemented.");
                        Console.Error.Write($"{(loginType == "oauth" ? "OAuth access token" : "API key")} for {loginProvider}: ");
                        var secret = ReadSecret();
                        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("Credential cannot be empty.");
                        if (loginType == "oauth") await modelRuntime.LoginOAuthAsync(loginProvider, secret);
                        else await modelRuntime.LoginApiKeyAsync(loginProvider, secret);
                        Console.WriteLine($"Authenticated {loginProvider} with {loginType}; credential value was not displayed.");
                        if (loginProvider.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase))
                            await ReplaceModelRuntime(await modelRuntime.ResolveAsync(selection.Provider.Id, selection.Model.Id), thinking, false);
                        break;
                    case "/logout":
                        var logoutProvider = argument.Length == 0 ? selection.Provider.Id : argument;
                        var removed = await modelRuntime.LogoutAsync(logoutProvider);
                        Console.WriteLine(removed ? $"Logged out {logoutProvider}." : $"No stored credential for {logoutProvider}.");
                        if (logoutProvider.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase))
                            await ReplaceModelRuntime(await modelRuntime.ResolveAsync(selection.Provider.Id, selection.Model.Id),
                                thinking, false, requireAuthenticated: false);
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
                        var stats = SessionStatistics.Calculate(conversation);
                        Console.WriteLine($"{sessionPath ?? "(ephemeral)"} · {stats.Id} · {stats.Name ?? "(unnamed)"} · {stats.Model} · head {conversation.Tree.HeadId ?? "(empty)"}");
                        var billing = stats.BilledTokens is null ? "provider usage unavailable" :
                            $"{stats.BilledTokens} billed tokens" + (stats.Cost is null ? " · cost unavailable" : $" · ${stats.Cost:0.######}");
                        Console.WriteLine($"{stats.ActiveMessages} active messages · {stats.UserTurns} turns · {stats.ToolCalls} calls/{stats.ToolResults} results · {stats.Entries} entries/{stats.Leaves} leaves · ~{stats.EstimatedContextTokens} context tokens (estimate) · {billing}");
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
                        if (string.IsNullOrWhiteSpace(argument))
                        {
                            Console.WriteLine($"Model: {selection.Provider.Id}/{selection.Model.Id} · thinking {thinking} · auth {selection.AuthSource}");
                            break;
                        }
                        var nextSelection = await modelRuntime.ResolveAsync(null, argument);
                        var compatibleThinking = nextSelection.Model.Reasoning == true ? thinking : "off";
                        await ReplaceModelRuntime(nextSelection, compatibleThinking, recordModelChange: true);
                        Console.WriteLine($"Model: {selection.Provider.Id}/{selection.Model.Id} · thinking {thinking}");
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
                        var forkRun = await OpenRunAsync(agent, forked, forkPath);
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
                            : new ConversationSession(Environment.CurrentDirectory, connection.Model, connection.Endpoint?.ToString(), selection.Provider.Id);
                        sessionPath = cli.NoSession ? null : store.NewPath(conversation);
                        var newPath = sessionPath;
                        conversationRun = await OpenRunAsync(agent, conversation, newPath);
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
