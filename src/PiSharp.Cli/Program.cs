using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Cli.Tui;
using PiSharp.Cli.Protocols;
using PiSharp.Cli.Sessions;

var agentDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("PISHARP_AGENT_DIR") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp", "agent"));
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
if (cli.Version)
{
    Console.WriteLine(typeof(CliArguments).Assembly.GetName().Version?.ToString(3) ?? "unknown");
    return;
}
if (cli.Help)
{
    Console.WriteLine("PiSharp (incomplete implementation)\nUsage: pisharp [--local | --provider <id>] [--model <id>] [--models <globs>] [--thinking <level>] [--api-key <key>] [--list-models [pattern]] [--system-prompt <text|file>] [--append-system-prompt <text|file>] [--no-context-files] [--no-extensions] [-e|--extension <path>] [--no-skills] [--skill <path>] [--no-prompt-templates] [--prompt-template <path>] [--offline] [--verbose] [-a|--approve|-na|--no-approve] [--mode text|interactive|print|json|rpc] [-p|--print] [-h|--help] [-v|--version] [-c|--continue | --session <path|project-id> | --fork <path|project-id> | --no-session] [--session-dir <dir>] [--name <label>] [prompt] [@files...]\n--tools <read,bash,edit,write,grep,find,ls> selects tools (grep/find/ls are opt-in); --exclude-tools <names> removes tools; --no-tools disables defaults (including extension tools); --no-builtin-tools disables only default built-ins.\nProviders and static model metadata may be configured in $PISHARP_AGENT_DIR/models.json. Credentials are read from environment or private auth.json; --api-key is runtime-only.\nOffline credential status: pisharp auth check --provider <id> [--model <configured-exact-id>] [--local]; explicit pisharp auth print-api-key --provider <id> prints an API key to stdout.\nStandalone private HTML: pisharp --export <PiSharp-session-file> [output.html]; never overwrites.\n--local uses http://192.168.0.97:8000/v1 and Qwen3.8-27B-GGUF (no API key required).\nInteractive: /model, /models, /settings, /thinking, /scoped-models, /login, /logout, /tree, /branch, /fork, /clone, /new, /sessions, /resume, /delete-session, /compact, /copy, /export, /export-jsonl, /import, /name, /session, /trust, /reload, /hotkeys, /quit.");
    return;
}
var invocationDirectory = Environment.CurrentDirectory;
string? configuredSessionDirectory;
try
{
    configuredSessionDirectory = Environment.GetEnvironmentVariable("PISHARP_SESSION_DIR");
    if (configuredSessionDirectory is not null)
        configuredSessionDirectory = Path.GetFullPath(configuredSessionDirectory, invocationDirectory);
    var startupTarget = PiSessionStartupTarget.Resolve(invocationDirectory, cli);
    cli = startupTarget.Arguments;
    Environment.CurrentDirectory = startupTarget.WorkingDirectory;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
    System.Text.Json.JsonException or ArgumentException or NotSupportedException)
{
    Console.Error.WriteLine($"Could not inspect session startup target: {error.Message}");
    Environment.ExitCode = 2;
    return;
}
var trustStore = new ProjectTrust(agentDirectory);
bool trusted;
UserSettings baseUserSettings;
UserSettings? projectSettings = null;
UserSettings userSettings;
try
{
    baseUserSettings = await UserSettings.LoadAsync(agentDirectory, Environment.GetEnvironmentVariable);
    userSettings = baseUserSettings;
    trusted = await trustStore.ResolveAsync(Environment.CurrentDirectory, cli.ProjectTrustOverride,
        cli.Mode == "interactive" && !cli.Print && !Console.IsInputRedirected && !Console.IsOutputRedirected, Console.In, Console.Error,
        defaultProjectTrust: baseUserSettings.DefaultProjectTrust ?? "ask");
    if (trusted)
    {
        projectSettings = await UserSettings.LoadProjectAsync(Environment.CurrentDirectory);
        userSettings = baseUserSettings.Overlay(projectSettings);
    }
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
        Environment.GetEnvironmentVariable, catalogHttp, cli.ApiKey, cli.ListModels ? null : cli.ScopedModels,
        offline: cli.Offline || Environment.GetEnvironmentVariable("PI_OFFLINE") is { } offlineFlag &&
            offlineFlag.ToLowerInvariant() is "1" or "true" or "yes");
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
    var models = await GetModelsAsync();
    var matching = models.Where(model => CliModelFilter.Matches(cli.ListModelsFilter, model.Provider, model.Id))
        .OrderBy(model => model.Provider, StringComparer.Ordinal).ThenBy(model => model.Id, StringComparer.Ordinal).ToArray();
    if (cli.ListModelsFilter is not null && matching.Length == 0)
        Console.WriteLine($"No models matching \"{cli.ListModelsFilter}\"");
    foreach (var model in matching)
        Console.WriteLine($"{model.Provider}/{model.Id}\t{(model.Available ? model.Status ?? "available" : model.UnavailableReason ?? "unavailable")}\t{model.ContextLength?.ToString() ?? ""}");
    return;
}
IChatClient chat;
try { chat = ProviderChatClientFactory.Create(selection); }
catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}
string instructions;
(string? System, string? Append) prompts;
ResourceCatalog resources;
ExtensionCatalog extensions;
try
{
    prompts = await CliPromptOverrides.ResolveAsync(cli,
        await ProjectPrompts.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted), Environment.CurrentDirectory);
    instructions = cli.NoContextFiles ? "" : await ContextInstructions.LoadAsync(Environment.CurrentDirectory, agentDirectory);
    resources = await ResourceCatalog.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted,
        discoverSkills: !cli.NoSkills, discoverPrompts: !cli.NoPromptTemplates,
        additionalSkills: cli.SkillPaths, additionalPrompts: cli.PromptTemplatePaths);
    extensions = ExtensionCatalog.Load(agentDirectory, Environment.CurrentDirectory, trusted, discover: !cli.NoExtensions,
        additionalPaths: cli.ExtensionPaths);
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
    agent = new PiAgent(chat, new CodingTools(Environment.CurrentDirectory, userSettings.ShellPath,
        selection.Model.InputLimits?.Images?.Resize), cli.Tools, cli.ExcludeTools, cli.NoTools,
    instructions, prompts.System, prompts.Append, extensionLease.Current.Registration.Tools,
    reasoning: ThinkingLevels.ToOptions(thinking), blockImages: userSettings.BlockImages == true, noBuiltinTools: cli.NoBuiltinTools,
    supportsImages: selection.Model.Input?.Contains("image", StringComparer.Ordinal) != false);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 2;
    return;
}
var store = new ConversationStore(Environment.CurrentDirectory,
    cli.SessionDirectory ?? configuredSessionDirectory ?? userSettings.SessionDirectory);
var piSessionImport = new PiSessionImportService(store, Environment.CurrentDirectory, cli.NoSession);
AutoCompactionPolicy? contextPolicy;
ModelPricing? modelPricing;
try
{
    contextPolicy = userSettings.ResolveCompaction(selection.Model.ContextLength, Environment.GetEnvironmentVariable, $"{selection.Provider.Id}/{selection.Model.Id}");
    modelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? selection.Model.Pricing;
}
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 2; return; }
Task<ConversationRun> OpenRunAsync(PiAgent runningAgent, ConversationSession session, string? path,
    string? runProvider = null, string? reasoningLevel = null) =>
    ConversationRun.OpenAsync(runningAgent, session, save: path is null ? null :
        token => store.SaveAsync(session, path, token), autoCompaction: contextPolicy, pricing: modelPricing,
        sessionFile: path, provider: runProvider ?? selection.Provider.Id, reasoningLevel: reasoningLevel ?? thinking,
        retryPolicy: userSettings.Retry?.ResolvePolicy() ?? AgentRunRetryPolicy.Default);
var sessionPath = cli.NoSession || cli.ForkSource is not null ? null : cli.SessionPath is not null &&
    (cli.SessionPath.Contains(Path.DirectorySeparatorChar) || cli.SessionPath.EndsWith(".session.json", StringComparison.Ordinal) ||
        cli.SessionPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
    ? Path.GetFullPath(cli.SessionPath) : cli.Continue ? store.MostRecentPath() : null;
ConversationSession conversation;
ConversationRun conversationRun;
try
{
    if (cli.SessionPath is not null && sessionPath is null)
        sessionPath = SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), cli.SessionPath).Path;
    if (cli.ForkSource is not null)
    {
        var sourcePath = cli.ForkSource.Contains(Path.DirectorySeparatorChar) || cli.ForkSource.EndsWith(".session.json", StringComparison.Ordinal) ||
            cli.ForkSource.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(cli.ForkSource)
            : SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), cli.ForkSource).Path;
        conversation = sourcePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? piSessionImport.ImportFile(sourcePath).Fork()
            : (await store.LoadAsync(sourcePath)).Fork();
    }
    else if (sessionPath is not null && sessionPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
    {
        conversation = piSessionImport.ImportFile(sessionPath);
        sessionPath = store.NewPath(conversation);
        await store.SaveAsync(conversation, sessionPath);
    }
    else
        conversation = sessionPath is not null && File.Exists(sessionPath)
            ? await store.LoadAsync(sessionPath)
            : new ConversationSession(Environment.CurrentDirectory, connection.Model, connection.Endpoint?.ToString(), selection.Provider.Id);
    if (conversation.Model == "unknown")
    {
        conversation.SelectModel(connection.Model, connection.Endpoint?.ToString(), selection.Provider.Id);
        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
    }
    var explicitConnection = cli.Local || cli.Provider is not null || cli.ModelOverride is not null ||
        Environment.GetEnvironmentVariable("PISHARP_MODEL") is not null || Environment.GetEnvironmentVariable("PISHARP_BASE_URL") is not null;
    var savedThinking = !explicitConnection && cli.Thinking is null
        ? PiJsonlSessionInterchange.GetThinkingLevel(conversation) : null;
    if (savedThinking is not null) thinking = savedThinking;
    var savedProvider = conversation.Provider ?? modelRuntime.Providers.FirstOrDefault(item =>
        string.Equals(item.Endpoint.ToString().TrimEnd('/'), conversation.Endpoint?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.Id ??
        (conversation.Endpoint is null ? "openai" : null);
    if (savedProvider is null)
        throw new InvalidDataException("Session provider cannot be resolved from models.json or the saved endpoint.");
    if (explicitConnection && (conversation.Model != connection.Model || conversation.Endpoint != connection.Endpoint?.ToString() ||
        conversation.Provider is not null && !conversation.Provider.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase)))
        throw new InvalidDataException("Requested provider/model conflicts with the saved session model or endpoint. Open it without explicit model flags and use /model after opening.");
    if (!explicitConnection && (conversation.Model != connection.Model || conversation.Endpoint != connection.Endpoint?.ToString() ||
        !savedProvider.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase) || savedThinking is not null))
    {
        selection = await modelRuntime.ResolveAsync(savedProvider, conversation.Model);
        connection = selection.Connection;
        thinking = ThinkingLevels.ValidateForModel(thinking, selection.Model.Reasoning);
        chat = ProviderChatClientFactory.Create(selection);
        contextPolicy = userSettings.ResolveCompaction(selection.Model.ContextLength, Environment.GetEnvironmentVariable, $"{selection.Provider.Id}/{selection.Model.Id}");
        modelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? selection.Model.Pricing;
        agent = new PiAgent(chat,
            new CodingTools(Environment.CurrentDirectory, userSettings.ShellPath,
                selection.Model.InputLimits?.Images?.Resize), cli.Tools, cli.ExcludeTools, cli.NoTools, instructions, prompts.System, prompts.Append,
            extensionLease.Current.Registration.Tools, reasoning: ThinkingLevels.ToOptions(thinking), blockImages: userSettings.BlockImages == true, noBuiltinTools: cli.NoBuiltinTools,
            supportsImages: selection.Model.Input?.Contains("image", StringComparer.Ordinal) != false);
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
var sessionController = new InteractiveSessionController(store, cli.NoSession,
    (branch, path) => OpenRunAsync(agent, branch, path));
bool print = cli.Print || cli.Mode == "print" || Console.IsInputRedirected || Console.IsOutputRedirected;
var prompt = cli.Prompt;
// Pi combines trimmed piped input before @file content and the positional prompt.
// RPC owns stdin as a command stream and must not consume it here.
string stdinContent;
try { stdinContent = cli.Mode != "rpc" && Console.IsInputRedirected ? await CliStdin.ReadAsync(Console.In) : ""; }
catch (InvalidDataException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}
if (!selection.Authenticated && cli.Mode != "rpc" &&
    (print || cli.Mode == "json" || !string.IsNullOrWhiteSpace(prompt)))
{
    Console.Error.WriteLine($"Provider '{selection.Provider.Id}' is not authenticated. Use /login {selection.Provider.Id} in an interactive terminal or configure {selection.Provider.ApiKeyEnvironment ?? "a credential"}.");
    Environment.ExitCode = 2;
    return;
}
TerminalEditor? editor = !print && cli.Mode is not ("json" or "rpc") ? new TerminalEditor(() =>
    resources.Skills.Select(item => "/skill:" + item.Name)
        .Concat(resources.Prompts.Select(item => "/" + item.Name))
        .Concat(extensionLease.Current.Registration.Commands.Keys.Select(name => "/" + name)).ToArray(), agentDirectory) : null;
var terminalThemeCatalog = new TerminalThemeCatalog(agentDirectory, trusted ? Environment.CurrentDirectory : null);
TerminalTheme ResolveConfiguredTheme(string? themeSetting, TerminalTheme.Rgb? terminalForeground = null,
    TerminalTheme.Rgb? terminalBackground = null)
{
    try { return terminalThemeCatalog.Resolve(themeSetting, terminalForeground, terminalBackground); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or FormatException)
    {
        Console.Error.WriteLine($"Could not load theme '{themeSetting}': {error.Message}. Using terminal appearance defaults.");
        return terminalThemeCatalog.Resolve(null, terminalForeground, terminalBackground);
    }
}
var initialTerminalTheme = ResolveConfiguredTheme(userSettings.Theme);
using var terminalScreen = editor is null ? null : new TerminalScreen(Console.Out, Console.Error,
    getColumns: null, getRows: null, imageRenderer: new TerminalImageRenderer(), theme: initialTerminalTheme,
    queryTerminalColors: !Console.IsInputRedirected && !Console.IsOutputRedirected);
terminalScreen?.SetThemeResolver((foreground, background) =>
    ResolveConfiguredTheme(userSettings.Theme, foreground, background));
terminalScreen?.Activate();
editor?.AttachScreen(terminalScreen);
void LoadSessionTranscript()
{
    if (terminalScreen is null) return;
    var history = new InteractiveTranscript(Console.Out, Console.Error, interactive: true,
        hideThinking: userSettings.HideThinkingBlock == true, screen: terminalScreen,
        toolRenderer: name => extensionLease.Current.Registration.GetToolRenderer(name),
        workingDirectory: conversation.WorkingDirectory);
    history.LoadHistory(conversation);
}
LoadSessionTranscript();
var terminalClipboard = new TerminalClipboard(writeTerminalControl: value =>
{
    if (terminalScreen is { IsActive: true }) terminalScreen.WriteControl(value);
    else Console.Write(value);
});
string IdleFooter() => $"{selection.Provider.Id}/{selection.Model.Id} · thinking {thinking} · Ctrl+L models · Ctrl+P cycle · Shift+Tab thinking · Enter send";
terminalScreen?.SetFooter(IdleFooter());
if (editor is not null && (cli.Verbose || userSettings.QuietStartup != true)) Console.WriteLine($"PiSharp · {selection.Provider.Id}/{connection.Model} · thinking {thinking} · {Environment.CurrentDirectory}\n/model · /settings · /thinking · /scoped-models · /login · /logout · /tree · /fork · /new · /session · /hotkeys · /quit · Escape interrupts; Enter steers; Alt+Enter follows up\n");
CancellationTokenSource? activeRun = null;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; activeRun?.Cancel(); };

async Task Run(string input, IReadOnlyList<DataContent>? images = null)
{
    using var runCancel = new CancellationTokenSource();
    activeRun = runCancel;
    using var monitorStop = new CancellationTokenSource();
    terminalScreen?.Activate();
    editor?.AttachScreen(terminalScreen);
    var monitor = Task.CompletedTask;
    var monitorStarted = false;
    var transcript = new InteractiveTranscript(Console.Out, Console.Error, interactive: !print,
        hideThinking: userSettings.HideThinkingBlock == true, screen: terminalScreen,
        toolRenderer: name => extensionLease.Current.Registration.GetToolRenderer(name),
        workingDirectory: Environment.CurrentDirectory);
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
            if (update.Type == "prompt_accepted" && editor is not null && !monitorStarted)
            {
                transcript.Render(update);
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
                }, () => conversationRun.ClearPendingPrompts().InDeliveryOrder, runCancel.Cancel, monitorStop.Token,
                    HandleEditorApplicationAction);
                continue;
            }
            transcript.Render(update);
            Environment.ExitCode = Math.Max(Environment.ExitCode, transcript.ExitCode);
        }
        transcript.FinishTurn();
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
        terminalScreen?.SetFooter(IdleFooter());
    }
}

async Task ReplaceModelRuntime(ModelSelection nextSelection, string nextThinking, bool recordModelChange,
    bool requireAuthenticated = true)
{
    if (requireAuthenticated && !nextSelection.Authenticated)
        throw new InvalidOperationException($"Provider '{nextSelection.Provider.Id}' is not authenticated. Use /login {nextSelection.Provider.Id}.");
    nextThinking = ThinkingLevels.ValidateForModel(nextThinking, nextSelection.Model.Reasoning);
    var nextConnection = nextSelection.Connection;
    var nextChat = ProviderChatClientFactory.Create(nextSelection);
    var nextPolicy = userSettings.ResolveCompaction(nextSelection.Model.ContextLength, Environment.GetEnvironmentVariable, $"{nextSelection.Provider.Id}/{nextSelection.Model.Id}");
    var nextPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? nextSelection.Model.Pricing;
    var nextAgent = new PiAgent(nextChat,
        new CodingTools(Environment.CurrentDirectory, userSettings.ShellPath,
            nextSelection.Model.InputLimits?.Images?.Resize), cli.Tools, cli.ExcludeTools, cli.NoTools,
        instructions, prompts.System, prompts.Append, extensionLease.Current.Registration.Tools,
        reasoning: ThinkingLevels.ToOptions(nextThinking), blockImages: userSettings.BlockImages == true, noBuiltinTools: cli.NoBuiltinTools,
        supportsImages: nextSelection.Model.Input?.Contains("image", StringComparer.Ordinal) != false);
    var previousHead = conversation.Tree.HeadId;
    var previousSelection = selection;
    var previousConnection = connection;
    var previousPolicy = contextPolicy;
    var previousPricing = modelPricing;
    try
    {
        if (recordModelChange)
            conversation.SelectModel(nextConnection.Model, nextConnection.Endpoint?.ToString(), nextSelection.Provider.Id);
        if (!nextThinking.Equals(thinking, StringComparison.Ordinal))
            conversation.AppendThinkingLevelChange(nextThinking);
        contextPolicy = nextPolicy;
        modelPricing = nextPricing;
        var nextRun = await OpenRunAsync(nextAgent, conversation, sessionPath, nextSelection.Provider.Id, nextThinking);
        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
        selection = nextSelection;
        connection = nextConnection;
        chat = nextChat;
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
        else conversation.Tree.Select(previousHead);
        throw;
    }
}

Task<IReadOnlyList<ModelDescriptor>> GetRpcModelsAsync(string? providerId, CancellationToken token) =>
    modelRuntime.ListModelsAsync(providerId, token);

async Task<ModelDescriptor> SelectRpcModelAsync(ModelDescriptor model, CancellationToken token)
{
    var providerId = model.Provider ?? throw new InvalidOperationException("The model catalogue did not identify the selected provider.");
    var provider = modelRuntime.GetProvider(providerId);
    var auth = await modelRuntime.ResolveAuthAsync(providerId, useRuntimeOverride: true, token);
    var nextSelection = new ModelSelection(provider, model, auth.Key, auth.Authenticated, auth.Source);
    var nextThinking = model.Reasoning == true ? thinking : "off";
    await ReplaceModelRuntime(nextSelection, nextThinking, recordModelChange: true);
    return selection.Model;
}

async Task<string> SetRpcThinkingLevelAsync(string level, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    var normalized = ThinkingLevels.Normalize(level);
    var nextThinking = selection.Model.Reasoning == true ? normalized : "off";
    if (nextThinking.Equals(thinking, StringComparison.Ordinal)) return thinking;
    await ReplaceModelRuntime(selection, nextThinking, recordModelChange: false);
    return thinking;
}

Task<string> SetRpcThinkingLevelDuringRunAsync(string level, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    var normalized = ThinkingLevels.Normalize(level);
    var nextThinking = selection.Model.Reasoning == true ? normalized : "off";
    if (nextThinking.Equals(thinking, StringComparison.Ordinal)) return Task.FromResult(thinking);
    if (!conversationRun.SetThinkingLevelDuringRun(nextThinking, ThinkingLevels.ToOptions(nextThinking)))
        throw new InvalidOperationException("The active run is already settling.");
    thinking = nextThinking;
    return Task.FromResult(thinking);
}

async Task SetRpcAutoRetryEnabledAsync(bool enabled, CancellationToken token)
{
    var settingsPath = UserSettings.GetSettingsPath(agentDirectory, Environment.GetEnvironmentVariable);
    await UserSettingsWriter.SetAsync(settingsPath, "retry.enabled", enabled ? "true" : "false",
        userScope: true, token);
    baseUserSettings = await UserSettings.LoadAsync(agentDirectory, Environment.GetEnvironmentVariable, token);
    userSettings = baseUserSettings.Overlay(projectSettings ?? new UserSettings());
    conversationRun.SetAutoRetryEnabled(enabled);
}

var terminalModelPicker = editor is null ? null : new TerminalModelPicker(modelRuntime, editor);
var terminalSessionPicker = editor is null ? null : new TerminalSessionPicker(store, editor);
var terminalForkPicker = editor is null ? null : new TerminalForkPicker(editor);
var terminalSettingsPicker = editor is null ? null : new TerminalSettingsPicker(editor, () => terminalThemeCatalog.GetAvailableNames());
async Task SelectModelAsync()
{
    if (terminalModelPicker is null) return;
    var nextSelection = await terminalModelPicker.ShowAsync(selection, cli.Provider);
    if (nextSelection is null) return;
    var nextThinking = nextSelection.Model.Reasoning == true ? thinking : "off";
    await ReplaceModelRuntime(nextSelection, nextThinking, recordModelChange: true);
    Console.WriteLine($"Model: {selection.Provider.Id}/{selection.Model.Id} · thinking {thinking}");
}

async Task ResumeSessionAsync(SessionListing listing)
{
    var resumed = await sessionController.ResumeAsync(conversation, sessionPath, listing,
        connection.Model, connection.Endpoint?.ToString(), selection.Provider.Id);
    if (resumed is null)
    {
        Console.WriteLine("Already in this session.");
        return;
    }
    conversation = resumed.Conversation;
    conversationRun = resumed.Run;
    sessionPath = resumed.Path;
    LoadSessionTranscript();
    Console.WriteLine($"Resumed {conversation.Id[..12]} · {conversation.Name ?? "(unnamed)"}");
}

async Task SelectSessionAsync()
{
    if (cli.NoSession) throw new InvalidOperationException("Cannot resume in --no-session mode.");
    if (terminalSessionPicker is null)
    {
        Console.WriteLine("Use /sessions, then /resume <ID-prefix|exact-name> in interactive mode.");
        return;
    }
    var listing = await terminalSessionPicker.ShowAsync(sessionPath);
    if (listing is not null) await ResumeSessionAsync(listing);
}

async Task ForkFromUserAsync(string id)
{
    var branch = await sessionController.ForkAtUserAsync(conversation, sessionPath, id);
    conversation = branch.Conversation;
    sessionPath = branch.Path;
    conversationRun = branch.Run;
    LoadSessionTranscript();
    if (branch.Prompt is { } draft) editor?.Prefill(draft);
    Console.WriteLine($"Forked {branch.SourceEntryId![..12]} to {branch.Path ?? "(ephemeral)"}. Edit and submit the draft prompt.");
}

async Task SelectForkAsync()
{
    var forkable = conversation.ForkableUserMessages();
    if (forkable.Count == 0)
    {
        Console.WriteLine("No text-only user messages on this branch.");
        return;
    }
    if (terminalForkPicker is null)
    {
        foreach (var item in forkable)
            Console.WriteLine($"{item.Id[..12]} · {new string(item.Text.Replace('\n', ' ').Take(90).Select(c => char.IsControl(c) ? ' ' : c).ToArray())}");
        Console.WriteLine("Use /fork <user-message-id> to edit a copy of its prompt in a new session.");
        return;
    }
    var selected = terminalForkPicker.Show(forkable);
    if (selected is { } message) await ForkFromUserAsync(message.Id);
}

async Task<(UserSettings User, UserSettings? Project)> SaveSettingAsync(bool projectScope, string setting, string? value)
{
    if (projectScope && !trusted)
        throw new InvalidOperationException("Trust this project before editing its settings.");
    var settingsPath = projectScope
        ? Path.Combine(Environment.CurrentDirectory, ".pi", "settings.json")
        : UserSettings.GetSettingsPath(agentDirectory, Environment.GetEnvironmentVariable);
    await UserSettingsWriter.SetAsync(settingsPath, setting, value, userScope: !projectScope);
    if (projectScope)
        projectSettings = await UserSettings.LoadProjectAsync(Environment.CurrentDirectory);
    else
        baseUserSettings = await UserSettings.LoadAsync(agentDirectory, Environment.GetEnvironmentVariable);
    userSettings = baseUserSettings.Overlay(projectSettings ?? new UserSettings());
    if (setting is "images.blockImages" or "compaction.enabled")
        await ReplaceModelRuntime(selection, thinking, recordModelChange: false);
    if (setting == "theme")
    {
        terminalThemeCatalog = new TerminalThemeCatalog(agentDirectory, trusted ? Environment.CurrentDirectory : null);
        terminalScreen?.SetTheme(ResolveConfiguredTheme(userSettings.Theme));
    }
    Console.WriteLine($"Saved {(projectScope ? "project" : "user")} setting {setting} = {value ?? "(default)"}.");
    return (baseUserSettings, projectSettings);
}

async Task SelectSettingsAsync()
{
    if (terminalSettingsPicker is null) return;
    await terminalSettingsPicker.ShowAsync(baseUserSettings, projectSettings, SaveSettingAsync);
    Console.WriteLine("Settings closed.");
}

async Task CopyLastAssistantAsync()
{
    var selected = terminalScreen?.SelectedText;
    var text = selected ?? conversation.ActiveMessages()
        .LastOrDefault(message => message.Role == ChatRole.Assistant)?.Text?.Trim();
    if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("No selected text or agent messages to copy yet.");
    await terminalClipboard.CopyTextAsync(text);
    Console.WriteLine(selected is null ? "Copied last agent message to clipboard" : "Copied selected text to clipboard");
}

async Task PasteClipboardTextAsync()
{
    var text = await terminalClipboard.ReadTextAsync();
    if (!string.IsNullOrEmpty(text)) editor?.InsertTextAtCursor(text);
}

async Task PasteClipboardImageAsync()
{
    var image = await terminalClipboard.ReadImageAsync();
    if (image is not null)
    {
        editor?.InsertTextAtCursor(await ClipboardImageStore.SaveAsync(image));
        return;
    }
    await PasteClipboardTextAsync();
}

async Task HandleEditorApplicationAction(string action)
{
    try
    {
        switch (action)
        {
            case "app.thinking.cycle":
                if (selection.Model.Reasoning != true)
                {
                    Console.WriteLine("Current model does not support thinking.");
                    return;
                }
                var thinkingIndex = Array.FindIndex(ThinkingLevels.All.ToArray(), level =>
                    level.Equals(thinking, StringComparison.OrdinalIgnoreCase));
                var nextThinking = ThinkingLevels.All[(thinkingIndex + 1 + ThinkingLevels.All.Count) % ThinkingLevels.All.Count];
                await ReplaceModelRuntime(selection, nextThinking, recordModelChange: false);
                Console.WriteLine($"Thinking level: {thinking}");
                break;
            case "app.model.select":
                await SelectModelAsync();
                break;
            case "app.settings.open":
                await SelectSettingsAsync();
                break;
            case "app.editor.external":
                var editorCommand = ExternalEditor.ResolveCommand(userSettings.ExternalEditor, Environment.GetEnvironmentVariable);
                Console.WriteLine($"Launching external editor: {editorCommand}");
                var result = await ExternalEditor.EditAsync(editor?.Draft ?? "", editorCommand);
                if (result.Success) editor?.Prefill(result.Content);
                else Console.Error.WriteLine($"External editor exited with status {result.ExitCode}; the draft was preserved.");
                break;
            case "app.message.copy":
                await CopyLastAssistantAsync();
                break;
            case "app.clipboard.pasteImage":
                await PasteClipboardImageAsync();
                break;
            case "app.session.resume":
                await SelectSessionAsync();
                break;
            case "app.session.fork":
                await SelectForkAsync();
                break;
            case "app.model.cycleForward":
            case "app.model.cycleBackward":
                var models = (await GetModelsAsync()).Where(model => model.Available).ToArray();
                if (models.Length == 0)
                {
                    Console.WriteLine("No available models in the current scope.");
                    return;
                }
                if (models.Length == 1)
                {
                    Console.WriteLine(modelRuntime.Scope.Count > 0 ? "Only one model in scope." : "Only one model available.");
                    return;
                }
                var currentIndex = Array.FindIndex(models, model =>
                    model.Provider?.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase) == true &&
                    model.Id.Equals(selection.Model.Id, StringComparison.OrdinalIgnoreCase));
                if (currentIndex < 0) currentIndex = 0;
                var direction = action == "app.model.cycleForward" ? 1 : -1;
                var nextIndex = (currentIndex + direction + models.Length) % models.Length;
                var nextModel = models[nextIndex];
                if (string.IsNullOrWhiteSpace(nextModel.Provider))
                    throw new InvalidOperationException("The model catalogue did not identify the selected provider.");
                var nextSelection = await modelRuntime.ResolveAsync(nextModel.Provider, nextModel.Id);
                var compatibleThinking = nextSelection.Model.Reasoning == true ? thinking : "off";
                await ReplaceModelRuntime(nextSelection, compatibleThinking, recordModelChange: true);
                Console.WriteLine($"Model: {selection.Provider.Id}/{selection.Model.Id} · thinking {thinking}");
                break;
        }
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Shortcut action failed: {error.Message}");
    }
    finally { terminalScreen?.SetFooter(IdleFooter()); }
}

async Task ReloadResources()
{
    var nextBaseUserSettings = await UserSettings.LoadAsync(agentDirectory, Environment.GetEnvironmentVariable);
    var nextProjectSettings = trusted ? await UserSettings.LoadProjectAsync(Environment.CurrentDirectory) : null;
    var nextUserSettings = nextBaseUserSettings.Overlay(nextProjectSettings ?? new UserSettings());
    var nextResources = await ResourceCatalog.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted,
        discoverSkills: !cli.NoSkills, discoverPrompts: !cli.NoPromptTemplates,
        additionalSkills: cli.SkillPaths, additionalPrompts: cli.PromptTemplatePaths);
    var nextContext = (cli.NoContextFiles ? "" : await ContextInstructions.LoadAsync(Environment.CurrentDirectory, agentDirectory)) +
        "\n" + nextResources.SystemInstructions();
    var nextPrompts = await CliPromptOverrides.ResolveAsync(cli,
        await ProjectPrompts.LoadAsync(Environment.CurrentDirectory, agentDirectory, trusted), Environment.CurrentDirectory);
    var nextExtensions = ExtensionCatalog.Load(agentDirectory, Environment.CurrentDirectory, trusted, discover: !cli.NoExtensions,
        additionalPaths: cli.ExtensionPaths);
    var previousContextPolicy = contextPolicy;
    try
    {
        var nextAgent = new PiAgent(chat,
            new CodingTools(Environment.CurrentDirectory, nextUserSettings.ShellPath,
                selection.Model.InputLimits?.Images?.Resize), cli.Tools, cli.ExcludeTools, cli.NoTools,
            nextContext, nextPrompts.System, nextPrompts.Append, nextExtensions.Registration.Tools,
            reasoning: ThinkingLevels.ToOptions(thinking), blockImages: nextUserSettings.BlockImages == true, noBuiltinTools: cli.NoBuiltinTools,
            supportsImages: selection.Model.Input?.Contains("image", StringComparer.Ordinal) != false);
        contextPolicy = nextUserSettings.ResolveCompaction(selection.Model.ContextLength, Environment.GetEnvironmentVariable,
            $"{selection.Provider.Id}/{selection.Model.Id}");
        var path = sessionPath;
        var nextRun = await OpenRunAsync(nextAgent, conversation, path);
        extensionLease.Replace(nextExtensions);
        baseUserSettings = nextBaseUserSettings;
        projectSettings = nextProjectSettings;
        userSettings = nextUserSettings;
        terminalThemeCatalog = new TerminalThemeCatalog(agentDirectory, trusted ? Environment.CurrentDirectory : null);
        terminalScreen?.SetTheme(ResolveConfiguredTheme(nextUserSettings.Theme));
        instructions = nextContext;
        resources = nextResources;
        prompts = nextPrompts;
        agent = nextAgent;
        conversationRun = nextRun;
    }
    catch
    {
        contextPolicy = previousContextPolicy;
        nextExtensions.Dispose();
        throw;
    }
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
        cancellationToken => store.SaveAsync(conversation, sessionPath, cancellationToken), resources, GetRpcModelsAsync,
        extensionLease.Current.Registration, () => selection.Authenticated ? null :
            $"Provider '{selection.Provider.Id}' is not authenticated. Use /login {selection.Provider.Id} or configure {selection.Provider.ApiKeyEnvironment ?? "a credential"}.",
        SelectRpcModelAsync, () => conversationRun, () => thinking, () => modelRuntime.Scope.Count > 0,
        SetRpcThinkingLevelAsync, () => ThinkingLevels.AvailableForModel(selection.Model.Reasoning),
        () => selection.Model.Reasoning == true, () => ProviderChatClientFactory.ResolveProtocol(selection),
        SetRpcThinkingLevelDuringRunAsync, SetRpcAutoRetryEnabledAsync).ServeAsync();
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
        var line = await editor!.ReadLineAsync(HandleEditorApplicationAction);
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
                    case "/export-jsonl":
                        var jsonlPath = argument.Length == 0
                            ? Path.Combine(Environment.CurrentDirectory, $"pisharp-{conversation.Id[..12]}.jsonl")
                            : Path.GetFullPath(SessionPathArgument.Parse(argument));
                        await PiJsonlSessionInterchange.ExportToFileAsync(conversation, jsonlPath);
                        Console.WriteLine($"Exported private Pi JSONL to {jsonlPath}.");
                        break;
                    case "/import":
                        if (argument.Length == 0) throw new ArgumentException("Usage: /import <path.jsonl>");
                        var inputPath = Path.GetFullPath(SessionPathArgument.Parse(argument));
                        Console.WriteLine($"Replace the active session with {TerminalSafeText.Normalize(inputPath)}? Type import to confirm:");
                        var importConfirmation = await editor.ReadLineAsync(HandleEditorApplicationAction, enableApplicationActions: false);
                        if (importConfirmation?.Trim() != "import")
                        {
                            Console.WriteLine("Import cancelled.");
                            break;
                        }
                        var imported = piSessionImport.ImportFile(inputPath);
                        var importedSelection = imported.Model == "unknown" ? selection :
                            await modelRuntime.ResolveAsync(imported.Provider, imported.Model, includeOutOfScope: true);
                        var importedThinking = PiJsonlSessionInterchange.GetThinkingLevel(imported) ?? thinking;
                        importedThinking = ThinkingLevels.ValidateForModel(importedThinking, importedSelection.Model.Reasoning);
                        if (imported.Model == "unknown")
                            imported.SelectModel(importedSelection.Model.Id, importedSelection.Connection.Endpoint?.ToString(), importedSelection.Provider.Id);
                        if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        var previousConversation = conversation;
                        var previousPath = sessionPath;
                        var previousRun = conversationRun;
                        var importedPath = piSessionImport.CreateDestinationPath(imported);
                        if (importedPath is not null) await store.SaveAsync(imported, importedPath);
                        conversation = imported;
                        sessionPath = importedPath;
                        try
                        {
                            if (importedSelection.Provider.Id != selection.Provider.Id || importedSelection.Model.Id != selection.Model.Id ||
                                importedThinking != thinking)
                                await ReplaceModelRuntime(importedSelection, importedThinking, recordModelChange: false);
                            else conversationRun = await OpenRunAsync(agent, conversation, sessionPath);
                        }
                        catch
                        {
                            conversation = previousConversation;
                            sessionPath = previousPath;
                            conversationRun = previousRun;
                            if (importedPath is not null && File.Exists(importedPath)) File.Delete(importedPath);
                            throw;
                        }
                        LoadSessionTranscript();
                        Console.WriteLine($"Imported Pi session {conversation.Id} · {conversation.ActiveMessages().Count} active messages · {sessionPath ?? "(ephemeral)"}");
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
                        var confirmation = await editor.ReadLineAsync(HandleEditorApplicationAction, enableApplicationActions: false);
                        if (confirmation?.Trim() != "delete " + victim.Id[..12])
                        {
                            Console.WriteLine("Deletion cancelled.");
                            break;
                        }
                        await store.DeleteAsync(victim);
                        Console.WriteLine($"Deleted {victim.Id[..12]} permanently.");
                        break;
                    case "/resume":
                        if (cli.NoSession) throw new InvalidOperationException("Cannot resume in --no-session mode.");
                        if (argument.Length == 0) await SelectSessionAsync();
                        else await ResumeSessionAsync(SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), argument));
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
                    case "/copy":
                        if (argument.Length != 0) throw new ArgumentException("/copy does not accept arguments.");
                        await CopyLastAssistantAsync();
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
                        LoadSessionTranscript();
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
                        editor.ReloadKeybindings();
                        Console.WriteLine("Project resources and themes reloaded.");
                        break;
                    case "/hotkeys":
                        Console.WriteLine(editor.Hotkeys);
                        break;
                    case "/settings":
                        if (argument.Length != 0) throw new ArgumentException("/settings does not accept arguments.");
                        await SelectSettingsAsync();
                        break;
                    case "/model":
                        if (string.IsNullOrWhiteSpace(argument))
                        {
                            await SelectModelAsync();
                            break;
                        }
                        var nextSelection = await modelRuntime.ResolveAsync(null, argument, includeOutOfScope: true);
                        var compatibleThinking = nextSelection.Model.Reasoning == true ? thinking : "off";
                        await ReplaceModelRuntime(nextSelection, compatibleThinking, recordModelChange: true);
                        Console.WriteLine($"Model: {selection.Provider.Id}/{selection.Model.Id} · thinking {thinking}");
                        break;
                    case "/fork":
                        if (argument.Length == 0)
                        {
                            await SelectForkAsync();
                            break;
                        }
                        await ForkFromUserAsync(argument);
                        break;
                    case "/new":
                    case "/clone":
                        if (command == "/clone")
                        {
                            var branch = await sessionController.CloneAsync(conversation, sessionPath);
                            conversation = branch.Conversation;
                            sessionPath = branch.Path;
                            conversationRun = branch.Run;
                        }
                        else
                        {
                            if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                            conversation = new ConversationSession(Environment.CurrentDirectory, connection.Model,
                                connection.Endpoint?.ToString(), selection.Provider.Id);
                            sessionPath = cli.NoSession ? null : store.NewPath(conversation);
                            conversationRun = await OpenRunAsync(agent, conversation, sessionPath);
                            if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                        }
                        LoadSessionTranscript();
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
            terminalScreen?.SetFooter(IdleFooter());
            continue;
        }
        await Run(line);
    }
}
