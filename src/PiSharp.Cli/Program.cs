using System.Text.Json;
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
using PiSharp.Cli.Startup;

using var terminalIO = new CliTerminalIO();
var standardInput = terminalIO.Input;

var agentDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("PISHARP_AGENT_DIR") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp", "agent"));
var cli = await CliStartupCommands.ParseOrRunAsync(args, agentDirectory, Console.Out, Console.Error);
if (cli is null) return;
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
var currentDirectory = Environment.CurrentDirectory;
var trustStore = new ProjectTrust(agentDirectory);
ProjectRuntimeConfiguration projectConfiguration;
var hasProjectTrustUi = cli.Mode == "interactive" && !cli.Print &&
    !Console.IsInputRedirected && !Console.IsOutputRedirected;
var projectTrustMode = cli.Print ? "print" : cli.Mode;
var projectTrustUi = new ProjectTrustStartupUi(hasProjectTrustUi, projectTrustMode, Console.Out, Console.Error);
try
{
    projectConfiguration = await ProjectRuntimeConfiguration.LoadAsync(currentDirectory, agentDirectory, cli,
        trustStore, hasProjectTrustUi, standardInput, Console.Error, extensionUi: projectTrustUi);
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}
using var projectConfigurationOwner = projectConfiguration;
var trusted = projectConfiguration.Trusted;
var baseUserSettings = projectConfiguration.BaseUserSettings;
var projectSettings = projectConfiguration.ProjectSettings;
var userSettings = projectConfiguration.Settings;
var usesDefaultToolsSetting = DefaultToolReloadPolicy.UsesSettingsDefaults(cli);
var explicitThinking = cli.Thinking;
cli = DefaultToolReloadPolicy.ApplyStartupDefaults(cli, userSettings, Environment.GetEnvironmentVariable,
    preserveSessionModel: cli.Continue || cli.SessionPath is not null || cli.ForkSource is not null || cli.ListModels);
using var catalogHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
ProviderModelRuntime modelRuntime;
try
{
    modelRuntime = await ProviderModelRuntime.CreateAsync(agentDirectory, cli.Local || userSettings.DefaultProvider == "local",
        Environment.GetEnvironmentVariable, catalogHttp, cli.ApiKey, cli.ListModels ? null : cli.ScopedModels,
            offline: cli.Offline || Environment.GetEnvironmentVariable("PI_OFFLINE") is { } offlineFlag &&
            offlineFlag.ToLowerInvariant() is "1" or "true" or "yes");
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}
ProjectRuntimeContext projectRuntime;
try
{
    projectRuntime = await ProjectRuntimeContext.LoadAsync(projectConfiguration, agentDirectory, cli,
        configuredSessionDirectory, providerTokenResolver: modelRuntime.GetApiKeyForProviderAsync);
}
catch (Exception e) when (e is not OperationCanceledException)
{
    Console.Error.WriteLine($"Could not load project runtime: {e.Message}");
    Environment.ExitCode = 2;
    return;
}
using var projectRuntimeOwner = projectRuntime;
modelRuntime.SetVirtualModelRegistry(projectRuntime.Extensions.Registration.VirtualModels);
ModelSelection selection;
ConnectionSettings connection;
var thinking = cli.Thinking ?? "off";
try
{
    selection = await modelRuntime.ResolveAsync(cli.Provider ?? (cli.Local ? "local" : null), cli.ModelOverride);
    thinking = explicitThinking ?? userSettings.GetModelThinkingLevel(selection) ?? cli.Thinking ?? ThinkingLevels.Default;
    thinking = ThinkingLevels.ValidateForModel(thinking, selection.Model.Reasoning, selection.Model.ThinkingLevelMap);
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
using var codemodeHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var codemodeModels = new ProviderCodemodeModels(modelRuntime, codemodeHttp);
var modelRuntimeController = new ModelRuntimeController(modelRuntime, () => userSettings,
    Environment.GetEnvironmentVariable);
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
try { chat = ProviderChatClientFactory.Create(selection, userSettings.Retry?.Provider, userSettings.HttpIdleTimeoutMs); }
catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}
var resources = projectRuntime.Resources;
var instructions = projectRuntime.Instructions;
var prompts = projectRuntime.Prompts;
var store = projectRuntime.Store;
using var extensionLease = new ExtensionLease(projectRuntime.TransferExtensions());
PiAgent agent;
try
{
    agent = projectRuntime.CreateAgent(chat, selection, thinking, cli, userSettings,
        modelRuntimeController.CreateVirtualModelRouter(selection), codemodeModels);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 2;
    return;
}
AutoCompactionPolicy? contextPolicy;
ModelPricing? modelPricing;
try
{
    contextPolicy = userSettings.ResolveCompactionPolicy(selection.Model.ContextLength, Environment.GetEnvironmentVariable, $"{selection.Provider.Id}/{selection.Model.Id}");
    modelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? selection.Model.Pricing;
}
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 2; return; }
Task<ConversationRun> OpenRunAsync(PiAgent runningAgent, ConversationSession session, string? path,
    string? runProvider = null, string? reasoningLevel = null,
    ProjectRuntimeContext? targetProject = null, ModelSelection? targetSelection = null,
    IReadOnlyList<string>? activeToolNamesOverride = null)
{
    var runProject = targetProject ?? projectRuntime;
    var runModel = targetSelection ?? selection;
    var runSettings = targetProject is null ? userSettings : targetProject.Settings;
    var runCompaction = targetProject is null ? contextPolicy : runSettings.ResolveCompactionPolicy(runModel.Model.ContextLength,
        Environment.GetEnvironmentVariable, $"{runModel.Provider.Id}/{runModel.Model.Id}");
    var runPricing = targetProject is null ? modelPricing :
        ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? runModel.Model.Pricing;
    return ConversationRun.OpenAsync(runningAgent, session, save: path is null ? null :
        token => runProject.Store.SaveAsync(session, path, token), autoCompaction: runCompaction, pricing: runPricing,
        sessionFile: path, provider: runProvider ?? runModel.Provider.Id, reasoningLevel: reasoningLevel ?? thinking,
        retryPolicy: runSettings.Retry?.ResolvePolicy() ?? AgentRunRetryPolicy.Default,
        steeringMode: runSettings.SteeringMode ?? PromptDeliveryMode.OneAtATime,
        followUpMode: runSettings.FollowUpMode ?? PromptDeliveryMode.OneAtATime,
        autoCompactionEnabled: runSettings.AutoCompactionEnabled(Environment.GetEnvironmentVariable),
        keepRecentTokens: runSettings.ResolveCompactionKeepRecentTokens(
            $"{runModel.Provider.Id}/{runModel.Model.Id}"),
        physicalContextResolver: modelRuntimeController.ResolvePhysicalContextAsync,
        providerApi: ProviderChatClientFactory.ResolveProtocol(runModel),
        activeToolNamesOverride: activeToolNamesOverride);
}
var sessionPath = SessionStartupController.ResolvePath(cli, store);
ConversationSession conversation;
ConversationRun conversationRun;
try
{
    var startupConversation = await SessionStartupController.LoadAsync(cli, projectRuntime, selection, currentDirectory, sessionPath);
    conversation = startupConversation.Conversation;
    sessionPath = startupConversation.Path;
    var startupModel = await SessionStartupController.ResolveModelAsync(conversation, selection, thinking,
        cli, explicitThinking, userSettings, modelRuntime);
    selection = startupModel.Selection;
    thinking = startupModel.Thinking;
    if (startupModel.RebuildRuntime)
    {
        connection = selection.Connection;
        thinking = ThinkingLevels.ValidateForModel(thinking, selection.Model.Reasoning, selection.Model.ThinkingLevelMap);
        chat = ProviderChatClientFactory.Create(selection, userSettings.Retry?.Provider, userSettings.HttpIdleTimeoutMs);
        contextPolicy = userSettings.ResolveCompactionPolicy(selection.Model.ContextLength, Environment.GetEnvironmentVariable, $"{selection.Provider.Id}/{selection.Model.Id}");
        modelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? selection.Model.Pricing;
        agent = projectRuntime.CreateAgent(chat, selection, thinking, cli, userSettings,
            modelRuntimeController.CreateVirtualModelRouter(selection), codemodeModels);
    }
    using var initialNameChange = cli.SessionName is null ? null : conversation.BeginSessionNameChange(cli.SessionName);
    if (!cli.NoSession) sessionPath ??= store.NewPath(conversation);
    var initialPath = sessionPath;
    conversationRun = await OpenRunAsync(agent, conversation, initialPath);
    if ((cli.SessionName is not null || cli.ForkSource is not null) && sessionPath is not null)
        await store.SaveAsync(conversation, sessionPath);
    initialNameChange?.Commit();
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine($"Could not open session: {e.Message}");
    Environment.ExitCode = 2;
    return;
}
var sessionController = new InteractiveSessionController(store, cli.NoSession,
    (branch, path) => OpenRunAsync(agent, branch, path));
var projectSessionRuntimeFactory = new ProjectSessionRuntimeFactory(agentDirectory, cli,
    configuredSessionDirectory, trustStore, modelRuntime, codemodeModels);
bool print = cli.Print || cli.Mode == "print" || Console.IsInputRedirected || Console.IsOutputRedirected;
var prompt = cli.Prompt;
// Pi combines trimmed piped input before @file content and the positional prompt.
// RPC owns stdin as a command stream and must not consume it here.
string stdinContent;
try { stdinContent = cli.Mode != "rpc" && Console.IsInputRedirected ? await CliStdin.ReadAsync(standardInput) : ""; }
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
string? DescribeSlashCommand(string command)
{
    string? description;
    ResourceSourceInfo? sourceInfo;
    if (command.StartsWith("/skill:", StringComparison.Ordinal))
    {
        var skillName = command[7..];
        var skill = resources.Skills.FirstOrDefault(item => item.Name == skillName);
        if (skill is null) return null;
        description = skill.Description;
        sourceInfo = skill.SourceInfo;
    }
    else if (command.StartsWith('/') && command.Length > 1 &&
        resources.Prompts.FirstOrDefault(item => "/" + item.Name == command) is { } prompt)
    {
        description = prompt.Description;
        sourceInfo = prompt.SourceInfo;
    }
    else if (command.StartsWith('/') &&
        extensionLease.Current.Registration.CommandInfo.TryGetValue(command[1..], out var extensionCommand))
    {
        description = extensionCommand.Description;
        sourceInfo = extensionCommand.SourceInfo;
    }
    else return null;

    var scope = sourceInfo.Scope == "user" ? "u" : sourceInfo.Scope == "project" ? "p" : "t";
    var source = sourceInfo.Source.Trim();
    var tag = source is "auto" or "local" or "cli" ? scope :
        source.StartsWith("npm:", StringComparison.Ordinal) ? $"{scope}:{source}" : scope;
    var visibleDescription = description?.Replace('\n', ' ').Replace('\r', ' ').Trim();
    return string.IsNullOrWhiteSpace(visibleDescription) ? $"[{tag}]" : $"[{tag}] {visibleDescription}";
}
TerminalEditor? editor = !print && cli.Mode is not ("json" or "rpc") ? new TerminalEditor(() =>
{
    var skillCommands = userSettings.SkillCommandsEnabled
        ? resources.Skills.Select(item => "/skill:" + item.Name)
        : Enumerable.Empty<string>();
    return skillCommands
        .Concat(resources.Prompts.Select(item => "/" + item.Name))
        .Concat(extensionLease.Current.Registration.Commands.Keys.Select(name => "/" + name)).ToArray();
},
    agentDirectory, () => currentDirectory, initiallyExpanded: cli.Verbose,
    describeCommand: DescribeSlashCommand) : null;
string? cliThemeOverride = editor is null ? null : cli.UseTheme;
string? ActiveThemeSetting() => cliThemeOverride ?? userSettings.Theme;
TerminalThemeCatalog CreateTerminalThemeCatalog() => new(agentDirectory, trusted ? currentDirectory : null,
    trueColorOverride: userSettings.TerminalTrueColorOverride,
    userThemePaths: baseUserSettings.Themes, projectThemePaths: projectSettings?.Themes,
    discoverThemes: !cli.NoThemes, explicitThemePaths: (cli.ThemePaths ?? [])
        .Concat(projectRuntime.ExtensionResources.ThemePaths.Select(path => path.Path)).ToArray(),
    explicitThemeBaseDirectory: invocationDirectory);
var terminalThemeCatalog = CreateTerminalThemeCatalog();
TerminalTheme ResolveConfiguredTheme(string? themeSetting, TerminalColorState? terminalColors = null)
{
    var colors = terminalColors ?? new TerminalColorState();
    try { return terminalThemeCatalog.Resolve(themeSetting, colors); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or FormatException)
    {
        Console.Error.WriteLine($"Could not load theme '{themeSetting}': {error.Message}. Using terminal appearance defaults.");
        return terminalThemeCatalog.Resolve(null, colors);
    }
}
bool ThemeFollowsTerminalAppearance(string? themeSetting) => themeSetting is null or "" or "system" ||
    themeSetting.Count(character => character == '/') == 1;
var initialTerminalTheme = ResolveConfiguredTheme(ActiveThemeSetting());
var startupPresentation = new TerminalStartupPresentation(userSettings.QuietStartup, cli.Verbose,
    action => editor?.GetDisplayKeys(action));
if (editor is not null && startupPresentation.ShowDetails && modelRuntime.Scope.Count > 0)
{
    var cycleKey = editor.GetDisplayKeys("app.model.cycleForward");
    var cycleHint = string.IsNullOrWhiteSpace(cycleKey) ? "" : $" ({cycleKey} to cycle)";
    Console.WriteLine(initialTerminalTheme.Style("dim",
        $"Model scope: {string.Join(", ", modelRuntime.Scope)}{cycleHint}"));
}
using var terminalScreen = editor is null ? null : new TerminalScreen(terminalIO.Output,
    terminalIO.Error,
    getColumns: null, getRows: null, imageRenderer: new TerminalImageRenderer(), theme: initialTerminalTheme,
    queryTerminalColors: !Console.IsInputRedirected && !Console.IsOutputRedirected,
    followTerminalAppearance: ThemeFollowsTerminalAppearance(ActiveThemeSetting()), deferInitialRender: true,
    minimumRenderInterval: TimeSpan.FromMilliseconds(16));
terminalScreen?.SetThemeResolver(colors => ResolveConfiguredTheme(ActiveThemeSetting(), colors));
terminalScreen?.SetMarkdownCodeBlockIndent(userSettings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
var hideThinkingBlock = userSettings.HideThinkingBlock == true;
InteractiveTranscript? activeTranscript = null;
terminalScreen?.SetThinkingBlocksHidden(hideThinkingBlock);
terminalScreen?.Activate();
using var terminalMode = editor is null ? null : TerminalMode.Enter(terminalScreen);
void ConfigureMcpAuthorizationUrlPresenter(ProjectRuntimeContext context)
{
    Action<string, Uri>? presenter = terminalScreen is null ? null : (serverName, url) =>
        terminalScreen.AppendMcpAuthorizationNotification(serverName, url,
            editor?.GetDisplayKeys("app.message.copy"), editor?.GetDisplayKeys("app.interrupt"));
    Action<string>? completed = terminalScreen is null ? null : terminalScreen.ClearMcpAuthorizationNotification;
    context.SetMcpAuthorizationUrlPresenter(presenter, completed);
}
ConfigureMcpAuthorizationUrlPresenter(projectRuntime);
editor?.AttachScreen(terminalScreen);
editor?.CompleteInitialTerminalColorQuery();
string IdleFooter(int? providerCount = null) => TerminalModelStatus.FormatIdleFooter(selection.Model, thinking, conversation,
    conversationRun.ContextWindowTokens, userSettings.AutoCompactionEnabled(Environment.GetEnvironmentVariable),
    terminalScreen?.TerminalWidth ?? 80, providerCount ?? modelRuntime.AvailableModelProviderCount,
    conversationRun.EstimatedContextTokens);
terminalScreen?.SetFooter(IdleFooter(providerCount: 1), render: false);
terminalScreen?.RenderInitial();
void RefreshTerminalTheme()
{
    if (terminalScreen is null) return;
    terminalThemeCatalog = CreateTerminalThemeCatalog();
    terminalScreen.SetThemeResolver(colors => ResolveConfiguredTheme(ActiveThemeSetting(), colors));
    terminalScreen.SetTheme(ResolveConfiguredTheme(ActiveThemeSetting(), terminalScreen.CurrentTerminalColors));
    terminalScreen.SetTerminalAppearanceFollowing(ThemeFollowsTerminalAppearance(ActiveThemeSetting()));
}
void LoadSessionTranscript()
{
    if (terminalScreen is null) return;
    var history = new InteractiveTranscript(Console.Out, Console.Error, interactive: true,
        hideThinking: hideThinkingBlock, screen: terminalScreen,
        toolRenderer: name => extensionLease.Current.Registration.GetToolRenderer(name),
        workingDirectory: conversation.WorkingDirectory);
    history.LoadHistory(conversation);
}
LoadSessionTranscript();
var startupTrustNotice = !trusted && ProjectTrust.HasProtectedResources(currentDirectory)
    ? "This project is not trusted. Project .pi resources and packages are ignored. Use /trust to save a trust decision, then restart PiSharp."
    : null;
if (editor is not null)
{
    IReadOnlyList<TerminalStartupDetail> CurrentStartupDetails() => TerminalStartupDetails.Build(
        projectRuntime.ContextFiles, resources, extensionLease.Current, currentDirectory, terminalThemeCatalog.Diagnostics);
    terminalScreen?.SetStartupHeader((theme, columns, expanded) =>
        startupPresentation.BuildLogo(theme));
    terminalScreen?.SetStartupContent((theme, columns, expanded) =>
        startupPresentation.BuildContent(theme, columns, CurrentStartupDetails(), startupTrustNotice, expanded));
}
InteractiveTranscript CreateInteractiveTranscript(bool interactive = true) => new(Console.Out, Console.Error, interactive,
    hideThinking: hideThinkingBlock, screen: terminalScreen,
    toolRenderer: name => extensionLease.Current.Registration.GetToolRenderer(name),
    workingDirectory: currentDirectory);
var terminalClipboard = new TerminalClipboard(writeTerminalControl: value =>
{
    if (terminalScreen is { IsActive: true }) terminalScreen.WriteControl(value);
    else Console.Write(value);
});
terminalScreen?.SetFooter(IdleFooter(providerCount: 1));
terminalScreen?.SetFooter(IdleFooter());
CancellationTokenSource? activeRun = null;
if (editor is null) Console.CancelKeyPress += (_, e) => { e.Cancel = true; activeRun?.Cancel(); };

async Task Run(string input, IReadOnlyList<DataContent>? images = null)
{
    using var runCancel = new CancellationTokenSource();
    activeRun = runCancel;
    using var execution = new InteractivePromptExecution(editor, terminalScreen, () => Console.Error,
        () => resources, () => conversationRun, () => IdleFooter(), ExecuteInteractiveBashAsync, HandleEditorApplicationAction);
    terminalScreen?.Activate();
    editor?.AttachScreen(terminalScreen);
    var transcript = CreateInteractiveTranscript(interactive: !print);
    activeTranscript = transcript;
    try
    {
        await execution.ExecuteAsync(input, images, selection, userSettings, transcript, runCancel);
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Interrupted."); }
    catch (Exception ex) { Console.Error.WriteLine($"Agent error: {ex.Message}"); Environment.ExitCode = 1; }
    finally
    {
        await execution.StopMonitoringAsync();
        // Store the selected branch and any completed/aborted messages even when a turn fails.
        if (sessionPath is not null)
            try { await store.SaveAsync(conversation, sessionPath); }
            catch (Exception e) { Console.Error.WriteLine($"Could not save session: {e.Message}"); Environment.ExitCode = 1; }
        activeRun = null;
        if (ReferenceEquals(activeTranscript, transcript)) activeTranscript = null;
        terminalScreen?.SetWorkingStatus(null, render: false);
        terminalScreen?.SetFooter(IdleFooter());
    }
}

async Task ExecuteInteractiveBashAsync(string command, bool excludeFromContext, CancellationToken cancellationToken)
{
    await InteractiveBashExecutor.ExecuteAsync(conversationRun, command, excludeFromContext,
        CreateInteractiveTranscript(), sessionPath is null ? null : token => store.SaveAsync(conversation, sessionPath, token),
        cancellationToken);
}

async Task AdoptPreparedModelRuntime(PreparedModelRuntime prepared, bool recordModelChange)
{
    var nextSelection = prepared.Selection;
    var nextThinking = prepared.Thinking;
    var nextConnection = prepared.Connection;
    var nextChat = prepared.ChatClient;
    var nextPolicy = prepared.ContextPolicy;
    var nextPricing = prepared.Pricing;
    var nextAgent = projectRuntime.CreateAgent(nextChat, nextSelection, nextThinking, cli, userSettings,
        prepared.VirtualModelRouter, codemodeModels);
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
        var nextRun = await OpenRunAsync(nextAgent, conversation, sessionPath, nextSelection.Provider.Id, nextThinking,
            targetSelection: nextSelection);
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

Task ReplaceModelRuntime(ModelSelection nextSelection, string nextThinking, bool recordModelChange,
    bool requireAuthenticated = true) => AdoptPreparedModelRuntime(
        modelRuntimeController.Prepare(nextSelection, nextThinking, requireAuthenticated), recordModelChange);

async Task ApplyModelSelectionAsync(ModelSelection nextSelection, string nextThinking)
{
    await modelRuntimeController.ApplySelectionAsync(nextSelection, nextThinking, agent, conversationRun,
        prepared =>
        {
            selection = prepared.Selection;
            connection = prepared.Connection;
            chat = prepared.ChatClient;
            contextPolicy = prepared.ContextPolicy;
            modelPricing = prepared.Pricing;
            thinking = prepared.Thinking;
        }, prepared => AdoptPreparedModelRuntime(prepared, recordModelChange: true));
}

async Task<IReadOnlyList<ModelDescriptor>> GetRpcModelsAsync(string? providerId, CancellationToken token) =>
    (await modelRuntime.ListModelsAsync(providerId, token, includeOutOfScope: true)).Where(model => model.Available).ToArray();

async Task<IReadOnlyList<ModelDescriptor>> GetRpcCycleModelsAsync(string? providerId, CancellationToken token) =>
    (await modelRuntime.ListModelsAsync(providerId, token)).Where(model => model.Available).ToArray();

async Task<ModelDescriptor> SelectRpcModelAsync(ModelDescriptor model, CancellationToken token)
{
    var nextSelection = await modelRuntimeController.ResolveRpcModelAsync(model, token);
    var nextThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(nextSelection, thinking);
    await ApplyModelSelectionAsync(nextSelection, nextThinking);
    return selection.Model;
}

async Task<string> SetRpcThinkingLevelAsync(string level, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    var normalized = ThinkingLevels.Normalize(level);
    var nextThinking = ThinkingLevels.ValidateForModel(normalized, selection.Model.Reasoning,
        selection.Model.ThinkingLevelMap);
    if (nextThinking.Equals(thinking, StringComparison.Ordinal)) return thinking;
    await ReplaceModelRuntime(selection, nextThinking, recordModelChange: false);
    return thinking;
}

Task<string> SetRpcThinkingLevelDuringRunAsync(string level, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    var normalized = ThinkingLevels.Normalize(level);
    var nextThinking = ThinkingLevels.ValidateForModel(normalized, selection.Model.Reasoning,
        selection.Model.ThinkingLevelMap);
    if (nextThinking.Equals(thinking, StringComparison.Ordinal)) return Task.FromResult(thinking);
    if (!conversationRun.SetThinkingLevelDuringRun(nextThinking,
        ThinkingLevels.ToOptions(nextThinking, selection.Model.ThinkingLevelMap)))
        throw new InvalidOperationException("The active run is already settling.");
    thinking = nextThinking;
    return Task.FromResult(thinking);
}

var rpcUserSettings = new RpcUserSettingsController(agentDirectory, Environment.GetEnvironmentVariable,
    () => projectSettings,
    (globalSettings, effectiveSettings) =>
    {
        baseUserSettings = globalSettings;
        userSettings = effectiveSettings;
    },
    () => conversationRun);

var terminalModelPicker = editor is null ? null : new TerminalModelPicker(modelRuntime, editor,
    () => userSettings.DefaultProvider is { } provider && userSettings.DefaultModel is { } model
        ? provider + "\0" + model : null);
var terminalSessionPicker = editor is null ? null : new TerminalSessionPicker(store, editor);
var terminalForkPicker = editor is null ? null : new TerminalForkPicker(editor);
var terminalSettingsPicker = editor is null ? null : new TerminalSettingsPicker(editor, () => terminalThemeCatalog.GetAvailableNames());
var terminalTrustPicker = editor is null ? null : new TerminalTrustPicker(editor);
var terminalLlamaManager = editor is null ? null : new TerminalLlamaManager(modelRuntime, editor, catalogHttp,
    Environment.GetEnvironmentVariable);
var terminalProviderLogin = editor is null ? null : new TerminalProviderLogin(modelRuntime, editor, agentDirectory,
    Environment.GetEnvironmentVariable,
    () => currentDirectory, () => selection.Provider.Id,
    async () => await ReplaceModelRuntime(await modelRuntime.ResolveAsync(selection.Provider.Id, selection.Model.Id), thinking, false),
    ReloadResources, () => HandleEditorApplicationAction("app.message.copy"));
void ShowModelChangeStatus(bool persistAsDefault = false)
{
    var label = persistAsDefault
        ? $"Default model: {selection.Provider.Id}/{selection.Model.Id}"
        : $"Model: {selection.Model.Id}";
    if (terminalScreen is { IsActive: true } screen)
        screen.ShowModelChangeStatus(IdleFooter(), label);
    else Console.WriteLine($"Model: {selection.Provider.Id}/{selection.Model.Id} · thinking {thinking}");
}
async Task SelectModelAsync()
{
    if (terminalModelPicker is null) return;
    var picked = await terminalModelPicker.ShowAsync(selection);
    if (picked is null) return;
    var nextThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(picked.Selection, thinking);
    await ApplyModelSelectionAsync(picked.Selection, nextThinking);
    if (picked.PersistAsDefault)
    {
        await SaveSettingAsync(projectScope: false, "defaultProvider", selection.Provider.Id);
        await SaveSettingAsync(projectScope: false, "defaultModel", selection.Model.Id);
    }
    ShowModelChangeStatus(picked.PersistAsDefault);
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
    var forkable = conversation.UserMessagesForForking();
    if (forkable.Count == 0)
    {
        Console.WriteLine("No user messages with text are available to fork.");
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

async Task<(UserSettings User, UserSettings? Project)> SaveSettingAsync(bool projectScope, string setting,
    string? value) => await SaveSettingCoreAsync(projectScope, setting, value, announce: true);

async Task<(UserSettings User, UserSettings? Project)> SaveSettingCoreAsync(bool projectScope, string setting,
    string? value, bool announce)
{
    var savedSettings = await UserSettingsPersistence.SaveAsync(agentDirectory, currentDirectory,
        projectScope, trusted, setting, value, baseUserSettings, projectSettings, Environment.GetEnvironmentVariable);
    baseUserSettings = savedSettings.User;
    projectSettings = savedSettings.Project;
    userSettings = savedSettings.Effective;
    if (setting == "hideThinkingBlock")
    {
        hideThinkingBlock = userSettings.HideThinkingBlock == true;
        terminalScreen?.SetThinkingBlocksHidden(hideThinkingBlock);
        activeTranscript?.SetHideThinkingBlock(hideThinkingBlock);
    }
    if (setting == "theme") cliThemeOverride = null;
    projectConfiguration = projectConfiguration.WithSettings(baseUserSettings, projectSettings, userSettings);
    if (setting == "steeringMode")
        conversationRun.SetSteeringMode(userSettings.SteeringMode ?? PromptDeliveryMode.OneAtATime);
    else if (setting == "followUpMode")
        conversationRun.SetFollowUpMode(userSettings.FollowUpMode ?? PromptDeliveryMode.OneAtATime);
    if (setting is "images.blockImages" or "compaction.enabled" or "retry.provider.maxRetries" or
        "retry.provider.maxRetryDelayMs" or "retry.provider.timeoutMs" or "httpIdleTimeoutMs")
        await ReplaceModelRuntime(selection, thinking, recordModelChange: false);
    if (setting is "theme" or "terminal.trueColor")
    {
        RefreshTerminalTheme();
    }
    else if (setting == "markdown.codeBlockIndent")
        terminalScreen?.SetMarkdownCodeBlockIndent(userSettings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
    if (announce)
    {
        var displayValue = setting == "httpProxy" ? value is null ? "(default)" : "(configured)" : value ?? "(default)";
        Console.WriteLine($"Saved {(projectScope ? "project" : "user")} setting {setting} = {displayValue}.");
    }
    return (baseUserSettings, projectSettings);
}

async Task SelectSettingsAsync()
{
    if (terminalSettingsPicker is null) return;
    await terminalSettingsPicker.ShowAsync(baseUserSettings, projectSettings, SaveSettingAsync,
        activeThemeSetting: ActiveThemeSetting);
    Console.WriteLine("Settings closed.");
}

async Task HandleEditorApplicationAction(string action)
{
    try
    {
        switch (action)
        {
            case "app.thinking.toggle":
                var hide = !hideThinkingBlock;
                await SaveSettingCoreAsync(projectScope: false, "hideThinkingBlock", hide ? "true" : "false",
                    announce: false);
                hideThinkingBlock = hide;
                terminalScreen?.SetThinkingBlocksHidden(hide);
                activeTranscript?.SetHideThinkingBlock(hide);
                terminalScreen?.AppendThemedStatusLine($"Thinking blocks: {(hide ? "hidden" : "visible")}");
                break;
            case "app.thinking.cycle":
                if (selection.Model.Reasoning != true)
                {
                    Console.WriteLine("Current model does not support thinking.");
                    return;
                }
                var availableThinkingLevels = ThinkingLevels.AvailableForModel(selection.Model.Reasoning,
                    selection.Model.ThinkingLevelMap);
                if (availableThinkingLevels.Count == 0)
                {
                    Console.WriteLine("Current model has no available thinking levels.");
                    return;
                }
                var thinkingIndex = Array.FindIndex(availableThinkingLevels.ToArray(), level =>
                    level.Equals(thinking, StringComparison.OrdinalIgnoreCase));
                var nextThinking = availableThinkingLevels[(thinkingIndex + 1 + availableThinkingLevels.Count) % availableThinkingLevels.Count];
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
                if (terminalScreen?.ActiveAuthorizationUrl is { } authorizationUrl)
                    await TerminalClipboardActions.CopyAuthorizationUrlAsync(terminalClipboard, terminalScreen, authorizationUrl);
                else
                    await TerminalClipboardActions.CopyLastAssistantAsync(terminalClipboard, conversation, terminalScreen, Console.Out);
                break;
            case "app.clipboard.pasteImage":
                await TerminalClipboardActions.PasteImageAsync(terminalClipboard, editor);
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
                var compatibleThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(nextSelection, thinking);
                await ApplyModelSelectionAsync(nextSelection, compatibleThinking);
                ShowModelChangeStatus();
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
    ProjectRuntimeContext? nextProject = null;
    var previousContextPolicy = contextPolicy;
    try
    {
        using var nextConfiguration = await ProjectRuntimeConfiguration.LoadAsync(currentDirectory, agentDirectory, cli,
            trustStore, interactiveTrust: false, Console.In, Console.Error, trustedOverride: trusted);
        var reloadPlan = DefaultToolReloadPolicy.Resolve(cli, usesDefaultToolsSetting, userSettings,
            nextConfiguration.Settings, conversationRun.ActiveToolNames, Environment.GetEnvironmentVariable);
        nextProject = await ProjectRuntimeContext.LoadAsync(nextConfiguration, agentDirectory, reloadPlan.Arguments,
            configuredSessionDirectory, providerTokenResolver: modelRuntime.GetApiKeyForProviderAsync,
            resourceDiscoveryReason: ExtensionResourceDiscoveryReason.Reload,
            activeToolNames: reloadPlan.ActiveToolNames);
        ConfigureMcpAuthorizationUrlPresenter(nextProject);
        modelRuntime.SetVirtualModelRegistry(nextProject.Extensions.Registration.VirtualModels);
        var nextController = new ModelRuntimeController(modelRuntime, () => nextConfiguration.Settings,
            Environment.GetEnvironmentVariable);
        var reloadAgentArguments = usesDefaultToolsSetting
            ? reloadPlan.Arguments with { Tools = null }
            : reloadPlan.Arguments;
        var nextAgent = nextProject.CreateAgent(chat, selection, thinking, reloadAgentArguments, nextConfiguration.Settings,
            nextController.CreateVirtualModelRouter(selection), codemodeModels);
        var nextContextPolicy = nextConfiguration.Settings.ResolveCompactionPolicy(selection.Model.ContextLength, Environment.GetEnvironmentVariable,
            $"{selection.Provider.Id}/{selection.Model.Id}");
        var nextModelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? selection.Model.Pricing;
        var nextRun = await OpenRunAsync(nextAgent, conversation, sessionPath, targetProject: nextProject,
            activeToolNamesOverride: reloadPlan.ActiveToolNames);
        extensionLease.Replace(nextProject.TransferExtensions());
        projectRuntime = nextProject;
        projectConfiguration = nextConfiguration;
        trusted = nextConfiguration.Trusted;
        baseUserSettings = nextConfiguration.BaseUserSettings;
        projectSettings = nextConfiguration.ProjectSettings;
        userSettings = nextConfiguration.Settings;
        cli = reloadPlan.Arguments;
        resources = nextProject.Resources;
        instructions = nextProject.Instructions;
        prompts = nextProject.Prompts;
        store = nextProject.Store;
        contextPolicy = nextContextPolicy;
        modelPricing = nextModelPricing;
        sessionController = new InteractiveSessionController(store, cli.NoSession,
            (branch, path) => OpenRunAsync(agent, branch, path));
        projectSessionRuntimeFactory = new ProjectSessionRuntimeFactory(agentDirectory, cli,
            configuredSessionDirectory, trustStore, modelRuntime, codemodeModels);
        terminalSessionPicker = editor is null ? null : new TerminalSessionPicker(store, editor);
        RefreshTerminalTheme();
        terminalScreen?.SetMarkdownCodeBlockIndent(userSettings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
        agent = nextAgent;
        conversationRun = nextRun;
        LoadSessionTranscript();
    }
    catch
    {
        contextPolicy = previousContextPolicy;
        nextProject?.Dispose();
        throw;
    }
}
void AdoptProjectSession(ProjectSessionRuntime replacement)
{
    var nextProject = replacement.Project;
    ConfigureMcpAuthorizationUrlPresenter(nextProject);
    extensionLease.Replace(nextProject.TransferExtensions());
    projectRuntime = nextProject;
    projectConfiguration = nextProject.Configuration;
    currentDirectory = nextProject.WorkingDirectory;
    trusted = nextProject.Trusted;
    baseUserSettings = nextProject.Configuration.BaseUserSettings;
    projectSettings = nextProject.ProjectSettings;
    userSettings = nextProject.Settings;
    resources = nextProject.Resources;
    instructions = nextProject.Instructions;
    prompts = nextProject.Prompts;
    store = nextProject.Store;
    conversation = replacement.Conversation;
    conversationRun = replacement.Run;
    sessionPath = replacement.Path;
    selection = replacement.Selection;
    connection = selection.Connection;
    chat = replacement.Chat;
    agent = replacement.Agent;
    thinking = replacement.Thinking;
    contextPolicy = replacement.ContextPolicy;
    modelPricing = replacement.Pricing;
    sessionController = new InteractiveSessionController(store, cli.NoSession,
        (branch, path) => OpenRunAsync(agent, branch, path));
    terminalSessionPicker = editor is null ? null : new TerminalSessionPicker(store, editor);
    RefreshTerminalTheme();
    terminalScreen?.SetMarkdownCodeBlockIndent(userSettings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
    terminalScreen?.SetFooter(IdleFooter());
    LoadSessionTranscript();
}

async Task<bool> SwitchProjectSessionAsync(string sessionReference, CancellationToken cancellationToken)
{
    var targetPath = Path.GetFullPath(sessionReference, currentDirectory);
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (sessionPath is not null && Path.GetFullPath(sessionPath).Equals(targetPath, comparison)) return false;
    if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath, cancellationToken);
    using var replacement = await projectSessionRuntimeFactory.OpenAsync(targetPath, currentDirectory,
        selection, thinking, keepSourceSessionDirectory: true, cancellationToken: cancellationToken);
    AdoptProjectSession(replacement);
    return false;
}

Task ValidateRpcSessionSwitchAsync(string sessionReference, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    projectSessionRuntimeFactory.ValidateExistingSwitchTarget(sessionReference, currentDirectory);
    return Task.CompletedTask;
}

if (cli.Mode == "rpc")
{
    void AdoptRpcSession(SessionBranchResult replacement)
    {
        conversation = replacement.Conversation;
        conversationRun = replacement.Run;
        sessionPath = replacement.Path;
    }

    async Task<bool> StartRpcSessionAsync(string? parentSession, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var created = await sessionController.NewAsync(conversation, sessionPath, parentSession);
        AdoptRpcSession(created);
        return false;
    }

    async Task<string?> ForkRpcSessionAsync(string entryId, ConversationSession branchSnapshot, string prompt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var forked = await sessionController.ForkPreparedAsync(conversation, sessionPath, entryId, branchSnapshot, prompt);
        AdoptRpcSession(forked);
        return forked.Prompt;
    }

    async Task<string?> PrepareRpcCloneAsync(ConversationSession branchSnapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await sessionController.PrepareCloneAsync(conversation, sessionPath, branchSnapshot, cancellationToken);
    }

    async Task<bool> CloneRpcSessionAsync(ConversationSession branchSnapshot, string? branchPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cloned = await sessionController.ActivatePreparedCloneAsync(branchSnapshot, branchPath);
        AdoptRpcSession(cloned);
        return false;
    }

    await new RpcMode(Console.In, Console.Out, conversationRun,
        cancellationToken => conversationRun.PersistAsync(cancellationToken),
        resources, GetRpcModelsAsync,
        extensionLease.Current.Registration, () => selection.Authenticated ? null :
            $"Provider '{selection.Provider.Id}' is not authenticated. Use /login {selection.Provider.Id} or configure {selection.Provider.ApiKeyEnvironment ?? "a credential"}.",
        SelectRpcModelAsync, () => conversationRun, () => thinking, () => modelRuntime.Scope.Count > 0,
        SetRpcThinkingLevelAsync, () => ThinkingLevels.AvailableForModel(selection.Model.Reasoning,
            selection.Model.ThinkingLevelMap),
        () => selection.Model.Reasoning == true, () => ProviderChatClientFactory.ResolveProtocol(selection),
        SetRpcThinkingLevelDuringRunAsync, rpcUserSettings.SetAutoRetryEnabledAsync, StartRpcSessionAsync,
        ForkRpcSessionAsync, CloneRpcSessionAsync, SwitchProjectSessionAsync,
        () => resources, () => extensionLease.Current.Registration,
        rpcUserSettings.SetPromptDeliveryModeAsync,
        getModelSnapshot: () => RpcModelProjector.Project(selection),
        persistAutoCompactionEnabled: rpcUserSettings.SetAutoCompactionEnabledAsync,
        projectModel: model => RpcModelProjector.Project(model,
            model.Provider is { } providerId ? modelRuntime.GetProvider(providerId) : null),
        validateSwitchSession: ValidateRpcSessionSwitchAsync,
        prepareCloneSession: PrepareRpcCloneAsync,
        discoverCycleModels: GetRpcCycleModelsAsync,
        getSystemMessage: () => RpcSystemMessageProjector.Project(agent)).ServeAsync();
    return;
}
if (cli.Mode == "json")
{
    await JsonPromptMode.RunAsync(conversation, conversationRun, store, sessionPath,
        resources, prompt, stdinContent, cli, currentDirectory, Console.Out, Console.Error);
    return;
}
if (print)
{
    CliFileArguments.PromptFiles promptFiles;
    try { promptFiles = await CliFileArguments.ProcessFilesAsync(prompt, cli.FileArguments, currentDirectory); }
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
        try { promptFiles = await CliFileArguments.ProcessFilesAsync(prompt, cli.FileArguments, currentDirectory); }
        catch (Exception e) when (e is IOException or ArgumentException) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; return; }
        if (!string.IsNullOrWhiteSpace(promptFiles.Text) || promptFiles.Images.Count > 0)
            await Run(promptFiles.Text, promptFiles.Images);
    }
    while (true)
    {
        var line = await editor!.ReadLineAsync(HandleEditorApplicationAction);
        if (line is null || line.Trim() is "/quit" or "/exit") break;
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (InteractiveBashExecutor.TryParse(line, out var bashCommand, out var excludeFromContext))
        {
            using var bashCancellation = new CancellationTokenSource();
            activeRun = bashCancellation;
            try { await ExecuteInteractiveBashAsync(bashCommand, excludeFromContext, bashCancellation.Token); }
            finally { activeRun = null; }
            continue;
        }
        if (line.StartsWith('/'))
        {
            try
            {
                var split = line.IndexOf(' ');
                var command = split < 0 ? line : line[..split];
                var argument = split < 0 ? "" : line[(split + 1)..].Trim();
                var sessionCommands = new InteractiveSessionCommands(store, currentDirectory, cli.NoSession,
                    Console.Out, () => editor.ReadLineAsync(HandleEditorApplicationAction, enableApplicationActions: false),
                    LoadSessionTranscript);
                if (!await sessionCommands.TryExecuteAsync(command, argument, conversation, conversationRun, sessionPath))
                {
                    switch (command)
                    {
                        case "/import":
                            {
                                if (argument.Length == 0) throw new ArgumentException("Usage: /import <path.jsonl>");
                                var inputPath = Path.GetFullPath(SessionPathArgument.Parse(argument), currentDirectory);
                                if (!inputPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                                    throw new ArgumentException("Import requires a Pi .jsonl session file.");
                                Console.WriteLine($"Replace the active session with {TerminalSafeText.Normalize(inputPath)}? Type import to confirm:");
                                var importConfirmation = await editor.ReadLineAsync(HandleEditorApplicationAction, enableApplicationActions: false);
                                if (importConfirmation?.Trim() != "import")
                                {
                                    Console.WriteLine("Import cancelled.");
                                    break;
                                }
                                if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
                                using var importedRuntime = await projectSessionRuntimeFactory.OpenAsync(inputPath,
                                    currentDirectory, selection, thinking);
                                AdoptProjectSession(importedRuntime);
                                Console.WriteLine($"Imported Pi session {conversation.Id} · {conversation.ActiveMessages().Count} active messages · {sessionPath ?? "(ephemeral)"}");
                                break;
                            }
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
                                var available = ThinkingLevels.AvailableForModel(selection.Model.Reasoning,
                                    selection.Model.ThinkingLevelMap);
                                Console.WriteLine($"Thinking: {thinking}; available: " +
                                    (available.Count == 0 ? "none" : string.Join(", ", available)));
                                break;
                            }
                            await ReplaceModelRuntime(selection, argument, recordModelChange: false);
                            Console.WriteLine($"Thinking: {thinking}");
                            break;
                        case "/login":
                            await terminalProviderLogin!.ShowAsync(argument);
                            terminalScreen?.SetFooter(IdleFooter());
                            break;
                        case "/llama":
                            await terminalLlamaManager!.ShowAsync(argument);
                            terminalScreen?.SetFooter(IdleFooter());
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
                            await TerminalClipboardActions.CopyLastAssistantAsync(terminalClipboard, conversation, terminalScreen, Console.Out);
                            break;
                        case "/trust":
                            if (argument.Length == 0)
                            {
                                var savedTrustDecision = terminalTrustPicker is null
                                    ? null
                                    : await terminalTrustPicker.ShowAsync(trustStore, currentDirectory, trusted);
                                if (savedTrustDecision.HasValue)
                                    Console.WriteLine($" Saved trust decision: {(savedTrustDecision.Value ? "trusted" : "untrusted")}. Restart PiSharp for this to take effect.");
                                break;
                            }
                            bool? decision = argument switch
                            {
                                "yes" or "allow" => true,
                                "no" or "deny" => false,
                                "forget" => null,
                                _ => throw new ArgumentException("Use /trust yes, /trust no, or /trust forget.")
                            };
                            await trustStore.SetAsync(currentDirectory, decision);
                            trusted = decision ?? await trustStore.ResolveAsync(currentDirectory, null,
                                false, Console.In, Console.Error);
                            await ReloadResources();
                            var trustStatus = $"Project resources: {(trusted ? "trusted" : "not trusted")}";
                            if (terminalScreen is null) Console.WriteLine(trustStatus);
                            else terminalScreen.AppendThemedStatusLine(trustStatus);
                            break;
                        case "/reload":
                            {
                                const string reloadProgress = "Reloading keybindings, extensions, skills, prompts, themes, and context files...";
                                const string reloadStatus = "Reloaded keybindings, extensions, skills, prompts, themes, and context files";
                                var reloadScreen = terminalScreen is { IsActive: true } activeScreen ? activeScreen : null;
                                reloadScreen?.ShowReloadProgressPanel(reloadProgress);
                                try
                                {
                                    await ReloadResources();
                                    editor.ReloadKeybindings();
                                }
                                catch
                                {
                                    reloadScreen?.DismissReloadProgressPanel();
                                    throw;
                                }
                                if (reloadScreen is not null)
                                    reloadScreen.DismissEditorPanelAndAppendThemedStatusLine(reloadStatus);
                                else if (terminalScreen is null) Console.WriteLine(reloadStatus);
                                break;
                            }
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
                            var compatibleThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(nextSelection, thinking);
                            await ApplyModelSelectionAsync(nextSelection, compatibleThinking);
                            ShowModelChangeStatus();
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
                            SessionBranchResult branch;
                            if (command == "/clone")
                                branch = await sessionController.CloneAsync(conversation, sessionPath);
                            else
                                branch = await sessionController.NewAsync(conversation, sessionPath, sessionPath);
                            conversation = branch.Conversation;
                            sessionPath = branch.Path;
                            conversationRun = branch.Run;
                            LoadSessionTranscript();
                            Console.WriteLine($"{command[1..]}: {sessionPath ?? "(ephemeral)"}");
                            break;
                        default:
                            if (command.StartsWith("/skill:", StringComparison.Ordinal))
                                await Run(line);
                            else if (resources.Prompts.Any(item => "/" + item.Name == command))
                                await Run(line);
                            else if (extensionLease.Current.Registration.Commands.TryGetValue(command[1..], out var handler))
                            {
                                var commandArguments = argument.Split((char[]?)null,
                                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                if (command.Equals("/mcp", StringComparison.Ordinal) &&
                                    commandArguments.Length > 0 && commandArguments[0].Equals("login", StringComparison.Ordinal) &&
                                    editor is not null && terminalScreen is { IsActive: true })
                                {
                                    using var signInCancellation = new CancellationTokenSource();
                                    var signIn = handler(argument, signInCancellation.Token);
                                    await editor.MonitorMcpAuthorizationAsync(signIn, HandleEditorApplicationAction,
                                        signInCancellation.Cancel);
                                    try { Console.WriteLine(await signIn); }
                                    catch (OperationCanceledException) when (signInCancellation.IsCancellationRequested)
                                    {
                                        Console.WriteLine("MCP sign-in cancelled.");
                                    }
                                }
                                else Console.WriteLine(await handler(argument, CancellationToken.None));
                            }
                            else Console.Error.WriteLine($"Unknown command: {command}");
                            break;
                    }
                }
            }
            catch (Exception e) { Console.Error.WriteLine($"Session error: {e.Message}"); Environment.ExitCode = 1; }
            terminalScreen?.SetFooter(IdleFooter());
            continue;
        }
        await Run(line);
    }
}
