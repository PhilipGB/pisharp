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
using PiSharp.Cli.Composition;

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
var projectBinding = new ActiveProjectRuntime(projectConfiguration, projectRuntime, cli);
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
    await modelRuntime.ListModelsAsync(projectBinding.Arguments.Provider, token);
using var codemodeHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var codemodeModels = new ProviderCodemodeModels(modelRuntime, codemodeHttp);
var modelRuntimeController = new ModelRuntimeController(modelRuntime, () => projectBinding.Settings,
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
ActiveModelRuntime modelBinding;
Task<ConversationRun> OpenRunAsync(PiAgent runningAgent, ConversationSession session, string? path,
    string? runProvider = null, string? reasoningLevel = null,
    ProjectRuntimeContext? targetProject = null,
    IReadOnlyList<string>? activeToolNamesOverride = null, PreparedModelRuntime? targetRuntime = null)
{
    var runProject = targetProject ?? projectBinding.Runtime;
    var runModel = targetRuntime?.Selection ?? modelBinding.Selection;
    var runSettings = targetProject is null ? projectBinding.Settings : targetProject.Settings;
    var runCompaction = targetRuntime is not null ? targetRuntime.ContextPolicy : targetProject is null ? modelBinding.ContextPolicy : runSettings.ResolveCompactionPolicy(runModel.Model.ContextLength,
        Environment.GetEnvironmentVariable, $"{runModel.Provider.Id}/{runModel.Model.Id}");
    var runPricing = targetRuntime is not null ? targetRuntime.Pricing : targetProject is null ? modelBinding.Pricing :
        ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? runModel.Model.Pricing;
    return ConversationRun.OpenAsync(runningAgent, session, save: path is null ? null :
        token => runProject.Store.SaveAsync(session, path, token), autoCompaction: runCompaction, pricing: runPricing,
        sessionFile: path, provider: runProvider ?? runModel.Provider.Id, reasoningLevel: reasoningLevel ?? modelBinding.Thinking,
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
    modelBinding = new ActiveModelRuntime(selection, chat, agent, thinking, contextPolicy, modelPricing);
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
var conversationBinding = new ActiveConversationRuntime(conversation, conversationRun, sessionPath);
var sessionController = new InteractiveSessionController(projectBinding.Store, projectBinding.Arguments.NoSession,
    (branch, path) => OpenRunAsync(modelBinding.Agent, branch, path));
var projectSessionRuntimeFactory = new ProjectSessionRuntimeFactory(agentDirectory, projectBinding.Arguments,
    configuredSessionDirectory, trustStore, modelRuntime, codemodeModels);
var projectResourceReload = new ProjectResourceReload(agentDirectory, configuredSessionDirectory, trustStore, modelRuntime);
bool print = projectBinding.Arguments.Print || projectBinding.Arguments.Mode == "print" || Console.IsInputRedirected || Console.IsOutputRedirected;
var prompt = projectBinding.Arguments.Prompt;
// Pi combines trimmed piped input before @file content and the positional prompt.
// RPC owns stdin as a command stream and must not consume it here.
string stdinContent;
try { stdinContent = projectBinding.Arguments.Mode != "rpc" && Console.IsInputRedirected ? await CliStdin.ReadAsync(standardInput) : ""; }
catch (InvalidDataException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}
if (!modelBinding.Selection.Authenticated && projectBinding.Arguments.Mode != "rpc" &&
    (print || projectBinding.Arguments.Mode == "json" || !string.IsNullOrWhiteSpace(prompt)))
{
    Console.Error.WriteLine($"Provider '{modelBinding.Selection.Provider.Id}' is not authenticated. Use /login {modelBinding.Selection.Provider.Id} in an interactive terminal or configure {modelBinding.Selection.Provider.ApiKeyEnvironment ?? "a credential"}.");
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
        var skill = projectBinding.Resources.Skills.FirstOrDefault(item => item.Name == skillName);
        if (skill is null) return null;
        description = skill.Description;
        sourceInfo = skill.SourceInfo;
    }
    else if (command.StartsWith('/') && command.Length > 1 &&
        projectBinding.Resources.Prompts.FirstOrDefault(item => "/" + item.Name == command) is { } prompt)
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
TerminalEditor? editor = !print && projectBinding.Arguments.Mode is not ("json" or "rpc") ? new TerminalEditor(() =>
{
    var skillCommands = projectBinding.Settings.SkillCommandsEnabled
        ? projectBinding.Resources.Skills.Select(item => "/skill:" + item.Name)
        : Enumerable.Empty<string>();
    return skillCommands
        .Concat(projectBinding.Resources.Prompts.Select(item => "/" + item.Name))
        .Concat(extensionLease.Current.Registration.Commands.Keys.Select(name => "/" + name)).ToArray();
},
    agentDirectory, () => projectBinding.WorkingDirectory, initiallyExpanded: projectBinding.Arguments.Verbose,
    describeCommand: DescribeSlashCommand) : null;
string? cliThemeOverride = editor is null ? null : projectBinding.Arguments.UseTheme;
string? ActiveThemeSetting() => cliThemeOverride ?? projectBinding.Settings.Theme;
TerminalThemeCatalog CreateTerminalThemeCatalog() => new(agentDirectory, projectBinding.Trusted ? projectBinding.WorkingDirectory : null,
    trueColorOverride: projectBinding.Settings.TerminalTrueColorOverride,
    userThemePaths: projectBinding.BaseUserSettings.Themes, projectThemePaths: projectBinding.ProjectSettings?.Themes,
    discoverThemes: !projectBinding.Arguments.NoThemes, explicitThemePaths: (projectBinding.Arguments.ThemePaths ?? [])
        .Concat(projectBinding.Runtime.ExtensionResources.ThemePaths.Select(path => path.Path)).ToArray(),
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
var startupPresentation = new TerminalStartupPresentation(projectBinding.Settings.QuietStartup, projectBinding.Arguments.Verbose,
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
terminalScreen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
var hideThinkingBlock = projectBinding.Settings.HideThinkingBlock == true;
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
ConfigureMcpAuthorizationUrlPresenter(projectBinding.Runtime);
editor?.AttachScreen(terminalScreen);
editor?.CompleteInitialTerminalColorQuery();
string IdleFooter(int? providerCount = null) => TerminalModelStatus.FormatIdleFooter(modelBinding.Selection.Model, modelBinding.Thinking, conversationBinding.Conversation,
    conversationBinding.Run.ContextWindowTokens, projectBinding.Settings.AutoCompactionEnabled(Environment.GetEnvironmentVariable),
    terminalScreen?.TerminalWidth ?? 80, providerCount ?? modelRuntime.AvailableModelProviderCount,
    conversationBinding.Run.EstimatedContextTokens);
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
        workingDirectory: conversationBinding.Conversation.WorkingDirectory);
    history.LoadHistory(conversationBinding.Conversation);
}
LoadSessionTranscript();
var startupTrustNotice = !projectBinding.Trusted && ProjectTrust.HasProtectedResources(projectBinding.WorkingDirectory)
    ? "This project is not trusted. Project .pi resources and packages are ignored. Use /trust to save a trust decision, then restart PiSharp."
    : null;
if (editor is not null)
{
    IReadOnlyList<TerminalStartupDetail> CurrentStartupDetails() => TerminalStartupDetails.Build(
        projectBinding.Runtime.ContextFiles, projectBinding.Resources, extensionLease.Current, projectBinding.WorkingDirectory, terminalThemeCatalog.Diagnostics);
    terminalScreen?.SetStartupHeader((theme, columns, expanded) =>
        startupPresentation.BuildLogo(theme));
    terminalScreen?.SetStartupContent((theme, columns, expanded) =>
        startupPresentation.BuildContent(theme, columns, CurrentStartupDetails(), startupTrustNotice, expanded));
}
InteractiveTranscript CreateInteractiveTranscript(bool interactive = true) => new(Console.Out, Console.Error, interactive,
    hideThinking: hideThinkingBlock, screen: terminalScreen,
    toolRenderer: name => extensionLease.Current.Registration.GetToolRenderer(name),
    workingDirectory: projectBinding.WorkingDirectory);
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
        () => projectBinding.Resources, () => conversationBinding.Run, () => IdleFooter(), ExecuteInteractiveBashAsync, HandleEditorApplicationAction);
    terminalScreen?.Activate();
    editor?.AttachScreen(terminalScreen);
    var transcript = CreateInteractiveTranscript(interactive: !print);
    activeTranscript = transcript;
    try
    {
        await execution.ExecuteAsync(input, images, modelBinding.Selection, projectBinding.Settings, transcript, runCancel);
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Interrupted."); }
    catch (Exception ex) { Console.Error.WriteLine($"Agent error: {ex.Message}"); Environment.ExitCode = 1; }
    finally
    {
        await execution.StopMonitoringAsync();
        // Store the selected branch and any completed/aborted messages even when a turn fails.
        if (conversationBinding.Path is not null)
            try { await projectBinding.Store.SaveAsync(conversationBinding.Conversation, conversationBinding.Path); }
            catch (Exception e) { Console.Error.WriteLine($"Could not save session: {e.Message}"); Environment.ExitCode = 1; }
        activeRun = null;
        if (ReferenceEquals(activeTranscript, transcript)) activeTranscript = null;
        terminalScreen?.SetWorkingStatus(null, render: false);
        terminalScreen?.SetFooter(IdleFooter());
    }
}

async Task ExecuteInteractiveBashAsync(string command, bool excludeFromContext, CancellationToken cancellationToken)
{
    await InteractiveBashExecutor.ExecuteAsync(conversationBinding.Run, command, excludeFromContext,
        CreateInteractiveTranscript(), conversationBinding.Path is null ? null : token => projectBinding.Store.SaveAsync(conversationBinding.Conversation, conversationBinding.Path, token),
        cancellationToken);
}

async Task AdoptPreparedModelRuntime(PreparedModelRuntime prepared, bool recordModelChange)
{
    var nextAgent = projectBinding.Runtime.CreateAgent(prepared.ChatClient, prepared.Selection, prepared.Thinking, projectBinding.Arguments, projectBinding.Settings,
        prepared.VirtualModelRouter, codemodeModels);
    var nextRun = await ConversationModelReplacement.ReplaceAsync(conversationBinding.Conversation, modelBinding.Selection, modelBinding.Thinking, prepared,
        recordModelChange,
        () => OpenRunAsync(nextAgent, conversationBinding.Conversation, conversationBinding.Path, prepared.Selection.Provider.Id, prepared.Thinking,
            targetRuntime: prepared),
        conversationBinding.Path is null ? null : () => projectBinding.Store.SaveAsync(conversationBinding.Conversation, conversationBinding.Path));
    modelBinding.AdoptPrepared(prepared, nextAgent);
    conversationBinding.ReplaceRun(nextRun);
}

Task ReplaceModelRuntime(ModelSelection nextSelection, string nextThinking, bool recordModelChange,
    bool requireAuthenticated = true) => AdoptPreparedModelRuntime(
        modelRuntimeController.Prepare(nextSelection, nextThinking, requireAuthenticated), recordModelChange);

async Task ApplyModelSelectionAsync(ModelSelection nextSelection, string nextThinking)
{
    await modelRuntimeController.ApplySelectionAsync(nextSelection, nextThinking, modelBinding.Agent, conversationBinding.Run,
        prepared =>
        {
            modelBinding.AdoptPrepared(prepared);
        }, prepared => AdoptPreparedModelRuntime(prepared, recordModelChange: true));
}

async Task<IReadOnlyList<ModelDescriptor>> GetRpcModelsAsync(string? providerId, CancellationToken token) =>
    (await modelRuntime.ListModelsAsync(providerId, token, includeOutOfScope: true)).Where(model => model.Available).ToArray();

async Task<IReadOnlyList<ModelDescriptor>> GetRpcCycleModelsAsync(string? providerId, CancellationToken token) =>
    (await modelRuntime.ListModelsAsync(providerId, token)).Where(model => model.Available).ToArray();

async Task<ModelDescriptor> SelectRpcModelAsync(ModelDescriptor model, CancellationToken token)
{
    var nextSelection = await modelRuntimeController.ResolveRpcModelAsync(model, token);
    var nextThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(nextSelection, modelBinding.Thinking);
    await ApplyModelSelectionAsync(nextSelection, nextThinking);
    return modelBinding.Selection.Model;
}

async Task<string> SetRpcThinkingLevelAsync(string level, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    var normalized = ThinkingLevels.Normalize(level);
    var nextThinking = ThinkingLevels.ValidateForModel(normalized, modelBinding.Selection.Model.Reasoning,
        modelBinding.Selection.Model.ThinkingLevelMap);
    if (nextThinking.Equals(modelBinding.Thinking, StringComparison.Ordinal)) return modelBinding.Thinking;
    await ReplaceModelRuntime(modelBinding.Selection, nextThinking, recordModelChange: false);
    return modelBinding.Thinking;
}

Task<string> SetRpcThinkingLevelDuringRunAsync(string level, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    var normalized = ThinkingLevels.Normalize(level);
    var nextThinking = ThinkingLevels.ValidateForModel(normalized, modelBinding.Selection.Model.Reasoning,
        modelBinding.Selection.Model.ThinkingLevelMap);
    if (nextThinking.Equals(modelBinding.Thinking, StringComparison.Ordinal)) return Task.FromResult(modelBinding.Thinking);
    if (!conversationBinding.Run.SetThinkingLevelDuringRun(nextThinking,
        ThinkingLevels.ToOptions(nextThinking, modelBinding.Selection.Model.ThinkingLevelMap)))
        throw new InvalidOperationException("The active run is already settling.");
    modelBinding.SetThinking(nextThinking);
    return Task.FromResult(modelBinding.Thinking);
}

var rpcUserSettings = new RpcUserSettingsController(agentDirectory, Environment.GetEnvironmentVariable,
    () => projectBinding.ProjectSettings,
    (globalSettings, effectiveSettings) =>
    {
        projectBinding.SetUserSettings(globalSettings, effectiveSettings);
    },
    () => conversationBinding.Run);

var terminalModelNavigation = new TerminalModelNavigation(modelRuntime, () => Console.Out);
var terminalProviderCommands = new TerminalProviderCommands(modelRuntime, modelRuntimeController, modelBinding, () => Console.Out);
var terminalModelPicker = editor is null ? null : new TerminalModelPicker(modelRuntime, editor,
    () => projectBinding.Settings.DefaultProvider is { } provider && projectBinding.Settings.DefaultModel is { } model
        ? provider + "\0" + model : null);
var terminalSessionPicker = editor is null ? null : new TerminalSessionPicker(projectBinding.Store, editor);
var terminalForkPicker = editor is null ? null : new TerminalForkPicker(editor);
var terminalSettingsPicker = editor is null ? null : new TerminalSettingsPicker(editor, () => terminalThemeCatalog.GetAvailableNames());
var terminalTrustPicker = editor is null ? null : new TerminalTrustPicker(editor);
var terminalProjectCommands = editor is null ? null : new TerminalProjectCommands(trustStore, projectBinding,
    terminalTrustPicker, editor, terminalScreen);
var terminalSessionImport = editor is null ? null : new TerminalSessionImport(projectBinding, modelBinding, conversationBinding,
    () => editor.ReadLineAsync(HandleEditorApplicationAction, enableApplicationActions: false),
    () => projectSessionRuntimeFactory, AdoptProjectSession);
var terminalLlamaManager = editor is null ? null : new TerminalLlamaManager(modelRuntime, editor, catalogHttp,
    Environment.GetEnvironmentVariable);
var terminalProviderLogin = editor is null ? null : new TerminalProviderLogin(modelRuntime, editor, agentDirectory,
    Environment.GetEnvironmentVariable,
    () => projectBinding.WorkingDirectory, () => modelBinding.Selection.Provider.Id,
    async () => await ReplaceModelRuntime(await modelRuntime.ResolveAsync(modelBinding.Selection.Provider.Id, modelBinding.Selection.Model.Id), modelBinding.Thinking, false),
    ReloadResources, () => HandleEditorApplicationAction("app.message.copy"));
void ShowModelChangeStatus(bool persistAsDefault = false)
{
    var label = persistAsDefault
        ? $"Default model: {modelBinding.Selection.Provider.Id}/{modelBinding.Selection.Model.Id}"
        : $"Model: {modelBinding.Selection.Model.Id}";
    if (terminalScreen is { IsActive: true } screen)
        screen.ShowModelChangeStatus(IdleFooter(), label);
    else Console.WriteLine($"Model: {modelBinding.Selection.Provider.Id}/{modelBinding.Selection.Model.Id} · thinking {modelBinding.Thinking}");
}
async Task SelectModelAsync()
{
    if (terminalModelPicker is null) return;
    var picked = await terminalModelPicker.ShowAsync(modelBinding.Selection);
    if (picked is null) return;
    var nextThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(picked.Selection, modelBinding.Thinking);
    await ApplyModelSelectionAsync(picked.Selection, nextThinking);
    if (picked.PersistAsDefault)
    {
        await SaveSettingAsync(projectScope: false, "defaultProvider", modelBinding.Selection.Provider.Id);
        await SaveSettingAsync(projectScope: false, "defaultModel", modelBinding.Selection.Model.Id);
    }
    ShowModelChangeStatus(picked.PersistAsDefault);
}

async Task ResumeSessionAsync(SessionListing listing)
{
    var resumed = await sessionController.ResumeAsync(conversationBinding.Conversation, conversationBinding.Path, listing,
        modelBinding.Connection.Model, modelBinding.Connection.Endpoint?.ToString(), modelBinding.Selection.Provider.Id);
    if (resumed is null)
    {
        Console.WriteLine("Already in this session.");
        return;
    }
    conversationBinding.Adopt(resumed.Conversation, resumed.Run, resumed.Path);
    LoadSessionTranscript();
    Console.WriteLine($"Resumed {conversationBinding.Conversation.Id[..12]} · {conversationBinding.Conversation.Name ?? "(unnamed)"}");
}

async Task SelectSessionAsync()
{
    if (projectBinding.Arguments.NoSession) throw new InvalidOperationException("Cannot resume in --no-session mode.");
    if (terminalSessionPicker is null)
    {
        Console.WriteLine("Use /sessions, then /resume <ID-prefix|exact-name> in interactive mode.");
        return;
    }
    var listing = await terminalSessionPicker.ShowAsync(conversationBinding.Path);
    if (listing is not null) await ResumeSessionAsync(listing);
}

async Task ForkFromUserAsync(string id)
{
    var branch = await sessionController.ForkAtUserAsync(conversationBinding.Conversation, conversationBinding.Path, id);
    conversationBinding.Adopt(branch.Conversation, branch.Run, branch.Path);
    LoadSessionTranscript();
    if (branch.Prompt is { } draft) editor?.Prefill(draft);
    Console.WriteLine($"Forked {branch.SourceEntryId![..12]} to {branch.Path ?? "(ephemeral)"}. Edit and submit the draft prompt.");
}

async Task SelectForkAsync()
{
    var forkable = conversationBinding.Conversation.UserMessagesForForking();
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
    var savedSettings = await UserSettingsPersistence.SaveAsync(agentDirectory, projectBinding.WorkingDirectory,
        projectScope, projectBinding.Trusted, setting, value, projectBinding.BaseUserSettings, projectBinding.ProjectSettings, Environment.GetEnvironmentVariable);
    projectBinding.SetSettings(savedSettings.User, savedSettings.Project, savedSettings.Effective);
    if (setting == "hideThinkingBlock")
    {
        hideThinkingBlock = projectBinding.Settings.HideThinkingBlock == true;
        terminalScreen?.SetThinkingBlocksHidden(hideThinkingBlock);
        activeTranscript?.SetHideThinkingBlock(hideThinkingBlock);
    }
    if (setting == "theme") cliThemeOverride = null;
    projectBinding.PublishSettingsConfiguration();
    if (setting == "steeringMode")
        conversationBinding.Run.SetSteeringMode(projectBinding.Settings.SteeringMode ?? PromptDeliveryMode.OneAtATime);
    else if (setting == "followUpMode")
        conversationBinding.Run.SetFollowUpMode(projectBinding.Settings.FollowUpMode ?? PromptDeliveryMode.OneAtATime);
    if (setting is "images.blockImages" or "compaction.enabled" or "retry.provider.maxRetries" or
        "retry.provider.maxRetryDelayMs" or "retry.provider.timeoutMs" or "httpIdleTimeoutMs")
        await ReplaceModelRuntime(modelBinding.Selection, modelBinding.Thinking, recordModelChange: false);
    if (setting is "theme" or "terminal.trueColor")
    {
        RefreshTerminalTheme();
    }
    else if (setting == "markdown.codeBlockIndent")
        terminalScreen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
    if (announce)
    {
        var displayValue = setting == "httpProxy" ? value is null ? "(default)" : "(configured)" : value ?? "(default)";
        Console.WriteLine($"Saved {(projectScope ? "project" : "user")} setting {setting} = {displayValue}.");
    }
    return (projectBinding.BaseUserSettings, projectBinding.ProjectSettings);
}

async Task SelectSettingsAsync()
{
    if (terminalSettingsPicker is null) return;
    await terminalSettingsPicker.ShowAsync(projectBinding.BaseUserSettings, projectBinding.ProjectSettings, SaveSettingAsync,
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
                var nextThinking = terminalModelNavigation.NextThinkingLevel(modelBinding.Selection, modelBinding.Thinking);
                if (nextThinking is null) return;
                await ReplaceModelRuntime(modelBinding.Selection, nextThinking, recordModelChange: false);
                Console.WriteLine($"Thinking level: {modelBinding.Thinking}");
                break;
            case "app.model.select":
                await SelectModelAsync();
                break;
            case "app.settings.open":
                await SelectSettingsAsync();
                break;
            case "app.editor.external":
                var editorCommand = ExternalEditor.ResolveCommand(projectBinding.Settings.ExternalEditor, Environment.GetEnvironmentVariable);
                Console.WriteLine($"Launching external editor: {editorCommand}");
                var result = await ExternalEditor.EditAsync(editor?.Draft ?? "", editorCommand);
                if (result.Success) editor?.Prefill(result.Content);
                else Console.Error.WriteLine($"External editor exited with status {result.ExitCode}; the draft was preserved.");
                break;
            case "app.message.copy":
                if (terminalScreen?.ActiveAuthorizationUrl is { } authorizationUrl)
                    await TerminalClipboardActions.CopyAuthorizationUrlAsync(terminalClipboard, terminalScreen, authorizationUrl);
                else
                    await TerminalClipboardActions.CopyLastAssistantAsync(terminalClipboard, conversationBinding.Conversation, terminalScreen, Console.Out);
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
                var direction = action == "app.model.cycleForward" ? 1 : -1;
                var nextSelection = await terminalModelNavigation.NextModelAsync(() => modelBinding.Selection, projectBinding.Arguments.Provider, direction);
                if (nextSelection is null) return;
                var compatibleThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(nextSelection, modelBinding.Thinking);
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
    var previousContextPolicy = modelBinding.ContextPolicy;
    try
    {
        var reload = await projectResourceReload.LoadAsync(projectBinding.WorkingDirectory, () => projectBinding.Settings, () => projectBinding.Arguments, projectBinding.Trusted,
            usesDefaultToolsSetting, () => conversationBinding.Run.ActiveToolNames);
        using var nextConfiguration = reload.Configuration;
        var reloadPlan = reload.ToolPlan;
        nextProject = reload.Project;
        ConfigureMcpAuthorizationUrlPresenter(nextProject);
        modelRuntime.SetVirtualModelRegistry(nextProject.Extensions.Registration.VirtualModels);
        var nextController = new ModelRuntimeController(modelRuntime, () => nextConfiguration.Settings,
            Environment.GetEnvironmentVariable);
        var reloadAgentArguments = usesDefaultToolsSetting
            ? reloadPlan.Arguments with { Tools = null }
            : reloadPlan.Arguments;
        var nextAgent = nextProject.CreateAgent(modelBinding.Chat, modelBinding.Selection, modelBinding.Thinking, reloadAgentArguments, nextConfiguration.Settings,
            nextController.CreateVirtualModelRouter(modelBinding.Selection), codemodeModels);
        var nextContextPolicy = nextConfiguration.Settings.ResolveCompactionPolicy(modelBinding.Selection.Model.ContextLength, Environment.GetEnvironmentVariable,
            $"{modelBinding.Selection.Provider.Id}/{modelBinding.Selection.Model.Id}");
        var nextModelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? modelBinding.Selection.Model.Pricing;
        var nextRun = await OpenRunAsync(nextAgent, conversationBinding.Conversation, conversationBinding.Path, targetProject: nextProject,
            activeToolNamesOverride: reloadPlan.ActiveToolNames);
        extensionLease.Replace(nextProject.TransferExtensions());
        projectBinding.Adopt(nextConfiguration, nextProject, reloadPlan.Arguments);
        modelBinding.SetContext(nextContextPolicy, nextModelPricing);
        sessionController = new InteractiveSessionController(projectBinding.Store, projectBinding.Arguments.NoSession,
            (branch, path) => OpenRunAsync(modelBinding.Agent, branch, path));
        projectSessionRuntimeFactory = new ProjectSessionRuntimeFactory(agentDirectory, projectBinding.Arguments,
            configuredSessionDirectory, trustStore, modelRuntime, codemodeModels);
        terminalSessionPicker = editor is null ? null : new TerminalSessionPicker(projectBinding.Store, editor);
        RefreshTerminalTheme();
        terminalScreen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
        modelBinding.ReplaceAgent(nextAgent);
        conversationBinding.ReplaceRun(nextRun);
        LoadSessionTranscript();
    }
    catch
    {
        modelBinding.RestoreContextPolicy(previousContextPolicy);
        nextProject?.Dispose();
        throw;
    }
}
void AdoptProjectSession(ProjectSessionRuntime replacement)
{
    var nextProject = replacement.Project;
    ConfigureMcpAuthorizationUrlPresenter(nextProject);
    extensionLease.Replace(nextProject.TransferExtensions());
    projectBinding.Adopt(nextProject.Configuration, nextProject);
    conversationBinding.Adopt(replacement.Conversation, replacement.Run, replacement.Path);
    modelBinding.AdoptProject(replacement);
    sessionController = new InteractiveSessionController(projectBinding.Store, projectBinding.Arguments.NoSession,
        (branch, path) => OpenRunAsync(modelBinding.Agent, branch, path));
    terminalSessionPicker = editor is null ? null : new TerminalSessionPicker(projectBinding.Store, editor);
    RefreshTerminalTheme();
    terminalScreen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
    terminalScreen?.SetFooter(IdleFooter());
    LoadSessionTranscript();
}

async Task<bool> SwitchProjectSessionAsync(string sessionReference, CancellationToken cancellationToken)
{
    var targetPath = Path.GetFullPath(sessionReference, projectBinding.WorkingDirectory);
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (conversationBinding.Path is not null && Path.GetFullPath(conversationBinding.Path).Equals(targetPath, comparison)) return false;
    if (conversationBinding.Path is not null) await projectBinding.Store.SaveAsync(conversationBinding.Conversation, conversationBinding.Path, cancellationToken);
    using var replacement = await projectSessionRuntimeFactory.OpenAsync(targetPath, projectBinding.WorkingDirectory,
        modelBinding.Selection, modelBinding.Thinking, keepSourceSessionDirectory: true, cancellationToken: cancellationToken);
    AdoptProjectSession(replacement);
    return false;
}

Task ValidateRpcSessionSwitchAsync(string sessionReference, CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    projectSessionRuntimeFactory.ValidateExistingSwitchTarget(sessionReference, projectBinding.WorkingDirectory);
    return Task.CompletedTask;
}

if (projectBinding.Arguments.Mode == "rpc")
{
    void AdoptRpcSession(SessionBranchResult replacement)
    {
        conversationBinding.Adopt(replacement.Conversation, replacement.Run, replacement.Path);
    }

    async Task<bool> StartRpcSessionAsync(string? parentSession, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var created = await sessionController.NewAsync(conversationBinding.Conversation, conversationBinding.Path, parentSession);
        AdoptRpcSession(created);
        return false;
    }

    async Task<string?> ForkRpcSessionAsync(string entryId, ConversationSession branchSnapshot, string prompt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var forked = await sessionController.ForkPreparedAsync(conversationBinding.Conversation, conversationBinding.Path, entryId, branchSnapshot, prompt);
        AdoptRpcSession(forked);
        return forked.Prompt;
    }

    async Task<string?> PrepareRpcCloneAsync(ConversationSession branchSnapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await sessionController.PrepareCloneAsync(conversationBinding.Conversation, conversationBinding.Path, branchSnapshot, cancellationToken);
    }

    async Task<bool> CloneRpcSessionAsync(ConversationSession branchSnapshot, string? branchPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cloned = await sessionController.ActivatePreparedCloneAsync(branchSnapshot, branchPath);
        AdoptRpcSession(cloned);
        return false;
    }

    await new RpcMode(Console.In, Console.Out, conversationBinding.Run,
        cancellationToken => conversationBinding.Run.PersistAsync(cancellationToken),
        projectBinding.Resources, GetRpcModelsAsync,
        extensionLease.Current.Registration, () => modelBinding.Selection.Authenticated ? null :
            $"Provider '{modelBinding.Selection.Provider.Id}' is not authenticated. Use /login {modelBinding.Selection.Provider.Id} or configure {modelBinding.Selection.Provider.ApiKeyEnvironment ?? "a credential"}.",
        SelectRpcModelAsync, () => conversationBinding.Run, () => modelBinding.Thinking, () => modelRuntime.Scope.Count > 0,
        SetRpcThinkingLevelAsync, () => ThinkingLevels.AvailableForModel(modelBinding.Selection.Model.Reasoning,
            modelBinding.Selection.Model.ThinkingLevelMap),
        () => modelBinding.Selection.Model.Reasoning == true, () => ProviderChatClientFactory.ResolveProtocol(modelBinding.Selection),
        SetRpcThinkingLevelDuringRunAsync, rpcUserSettings.SetAutoRetryEnabledAsync, StartRpcSessionAsync,
        ForkRpcSessionAsync, CloneRpcSessionAsync, SwitchProjectSessionAsync,
        () => projectBinding.Resources, () => extensionLease.Current.Registration,
        rpcUserSettings.SetPromptDeliveryModeAsync,
        getModelSnapshot: () => RpcModelProjector.Project(modelBinding.Selection),
        persistAutoCompactionEnabled: rpcUserSettings.SetAutoCompactionEnabledAsync,
        projectModel: model => RpcModelProjector.Project(model,
            model.Provider is { } providerId ? modelRuntime.GetProvider(providerId) : null),
        validateSwitchSession: ValidateRpcSessionSwitchAsync,
        prepareCloneSession: PrepareRpcCloneAsync,
        discoverCycleModels: GetRpcCycleModelsAsync,
        getSystemMessage: () => RpcSystemMessageProjector.Project(modelBinding.Agent)).ServeAsync();
    return;
}
if (projectBinding.Arguments.Mode == "json")
{
    await JsonPromptMode.RunAsync(conversationBinding.Conversation, conversationBinding.Run, projectBinding.Store, conversationBinding.Path,
        projectBinding.Resources, prompt, stdinContent, projectBinding.Arguments, projectBinding.WorkingDirectory, Console.Out, Console.Error);
    return;
}
if (print)
{
    CliFileArguments.PromptFiles promptFiles;
    try { promptFiles = await CliFileArguments.ProcessFilesAsync(prompt, projectBinding.Arguments.FileArguments, projectBinding.WorkingDirectory); }
    catch (Exception e) when (e is IOException or ArgumentException) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; return; }
    promptFiles = promptFiles with { Text = stdinContent + promptFiles.Text };
    if (string.IsNullOrWhiteSpace(promptFiles.Text) && promptFiles.Images.Count == 0) { Console.Error.WriteLine("A prompt is required in print mode."); Environment.ExitCode = 2; }
    else await Run(promptFiles.Text, promptFiles.Images);
}
else
{
    if (!string.IsNullOrWhiteSpace(prompt) || (projectBinding.Arguments.FileArguments?.Count ?? 0) > 0)
    {
        CliFileArguments.PromptFiles promptFiles;
        try { promptFiles = await CliFileArguments.ProcessFilesAsync(prompt, projectBinding.Arguments.FileArguments, projectBinding.WorkingDirectory); }
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
                var sessionCommands = new InteractiveSessionCommands(projectBinding.Store, projectBinding.WorkingDirectory, projectBinding.Arguments.NoSession,
                    Console.Out, () => editor.ReadLineAsync(HandleEditorApplicationAction, enableApplicationActions: false),
                    LoadSessionTranscript);
                if (!await sessionCommands.TryExecuteAsync(command, argument, conversationBinding.Conversation, conversationBinding.Run, conversationBinding.Path))
                {
                    switch (command)
                    {
                        case "/import":
                            await terminalSessionImport!.ImportAsync(argument);
                            break;
                        case "/resume":
                            if (projectBinding.Arguments.NoSession) throw new InvalidOperationException("Cannot resume in --no-session mode.");
                            if (argument.Length == 0) await SelectSessionAsync();
                            else await ResumeSessionAsync(SessionCatalog.Resolve(await SessionCatalog.ListAsync(projectBinding.Store), argument));
                            break;
                        case "/models":
                            await terminalProviderCommands.ListModelsAsync(argument, projectBinding.Arguments.Provider);
                            break;
                        case "/scoped-models":
                            await terminalProviderCommands.SetScopeAsync(argument, projectBinding.Arguments.Provider);
                            break;
                        case "/thinking":
                            await terminalProviderCommands.SetThinkingAsync(argument, ReplaceModelRuntime);
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
                            await terminalProviderCommands.LogoutAsync(argument, ReplaceModelRuntime);
                            break;
                        case "/compact":
                            Console.WriteLine(await conversationBinding.Run.CompactAsync(argument) ? "Context compacted; full history retained." :
                                "Nothing to compact (at least two completed user turns are required).");
                            break;
                        case "/copy":
                            if (argument.Length != 0) throw new ArgumentException("/copy does not accept arguments.");
                            await TerminalClipboardActions.CopyLastAssistantAsync(terminalClipboard, conversationBinding.Conversation, terminalScreen, Console.Out);
                            break;
                        case "/trust":
                            await terminalProjectCommands!.TrustAsync(argument, ReloadResources);
                            break;
                        case "/reload":
                            await terminalProjectCommands!.ReloadAsync(ReloadResources);
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
                            await terminalProviderCommands.SelectNamedModelAsync(argument, ApplyModelSelectionAsync);
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
                                branch = await sessionController.CloneAsync(conversationBinding.Conversation, conversationBinding.Path);
                            else
                                branch = await sessionController.NewAsync(conversationBinding.Conversation, conversationBinding.Path, conversationBinding.Path);
                            conversationBinding.Adopt(branch.Conversation, branch.Run, branch.Path);
                            LoadSessionTranscript();
                            Console.WriteLine($"{command[1..]}: {conversationBinding.Path ?? "(ephemeral)"}");
                            break;
                        default:
                            await InteractiveResourceCommands.ExecuteAsync(command, argument, line, () => projectBinding.Resources,
                                () => extensionLease.Current.Registration, text => Run(text), editor, terminalScreen,
                                HandleEditorApplicationAction);
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
