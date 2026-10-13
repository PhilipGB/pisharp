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
var startup = await ProjectStartupPreparation.PrepareAsync(cli, agentDirectory, standardInput);
if (startup is null) return;
using var projectConfigurationOwner = startup.Configuration;
var projectConfiguration = startup.Configuration;
var invocationDirectory = startup.InvocationDirectory;
var configuredSessionDirectory = startup.SessionDirectory;
var trustStore = startup.Trust;
var explicitThinking = startup.ExplicitThinking;
var usesDefaultToolsSetting = startup.UsesDefaultToolsSetting;
cli = startup.Arguments;
var currentDirectory = Environment.CurrentDirectory;
var userSettings = projectConfiguration.Settings;
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
var initialSelection = await InitialModelComposition.ResolveAsync(modelRuntime, cli, explicitThinking, userSettings);
if (initialSelection is null) return;
var selection = initialSelection.Selection;
var thinking = initialSelection.Thinking;
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
var initialModel = InitialModelComposition.Create(projectRuntime, chat, selection, thinking, cli, userSettings,
    modelRuntimeController, codemodeModels);
if (initialModel is null) return;
var runComposition = new ConversationRunComposition(projectBinding, modelRuntimeController);
var opened = await ConversationStartup.OpenAsync(projectBinding, initialModel, modelRuntime, modelRuntimeController,
    codemodeModels, runComposition, currentDirectory, explicitThinking);
if (opened is null) return;
var modelBinding = opened.Model;
var conversationBinding = opened.Conversation;
var modelTransitions = new ConversationModelTransitions(projectBinding, modelBinding, conversationBinding,
    modelRuntimeController, codemodeModels, runComposition);
var projectTransitions = new ProjectConversationTransitions(agentDirectory, configuredSessionDirectory, trustStore,
    projectBinding, modelBinding, conversationBinding, modelRuntime, codemodeModels, runComposition, extensionLease);
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

async Task<IReadOnlyList<ModelDescriptor>> GetRpcModelsAsync(string? providerId, CancellationToken token) =>
    (await modelRuntime.ListModelsAsync(providerId, token, includeOutOfScope: true)).Where(model => model.Available).ToArray();

async Task<IReadOnlyList<ModelDescriptor>> GetRpcCycleModelsAsync(string? providerId, CancellationToken token) =>
    (await modelRuntime.ListModelsAsync(providerId, token)).Where(model => model.Available).ToArray();

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
    () => projectTransitions.SessionFactory, AdoptProjectSession);
var terminalLlamaManager = editor is null ? null : new TerminalLlamaManager(modelRuntime, editor, catalogHttp,
    Environment.GetEnvironmentVariable);
var terminalProviderLogin = editor is null ? null : new TerminalProviderLogin(modelRuntime, editor, agentDirectory,
    Environment.GetEnvironmentVariable,
    () => projectBinding.WorkingDirectory, () => modelBinding.Selection.Provider.Id,
    async () => await modelTransitions.ReplaceAsync(await modelRuntime.ResolveAsync(modelBinding.Selection.Provider.Id, modelBinding.Selection.Model.Id), modelBinding.Thinking, false),
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
    await modelTransitions.ApplySelectionAsync(picked.Selection, nextThinking);
    if (picked.PersistAsDefault)
    {
        await SaveSettingAsync(projectScope: false, "defaultProvider", modelBinding.Selection.Provider.Id);
        await SaveSettingAsync(projectScope: false, "defaultModel", modelBinding.Selection.Model.Id);
    }
    ShowModelChangeStatus(picked.PersistAsDefault);
}

async Task ResumeSessionAsync(SessionListing listing)
{
    var resumed = await projectTransitions.Sessions.ResumeAsync(conversationBinding.Conversation, conversationBinding.Path, listing,
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
    var branch = await projectTransitions.Sessions.ForkAtUserAsync(conversationBinding.Conversation, conversationBinding.Path, id);
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
        await modelTransitions.ReplaceAsync(modelBinding.Selection, modelBinding.Thinking, recordModelChange: false);
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
                await modelTransitions.ReplaceAsync(modelBinding.Selection, nextThinking, recordModelChange: false);
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
                await modelTransitions.ApplySelectionAsync(nextSelection, compatibleThinking);
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

void RefreshProjectUi()
{
    terminalSessionPicker = editor is null ? null : new TerminalSessionPicker(projectBinding.Store, editor);
    RefreshTerminalTheme();
    terminalScreen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
}
Task ReloadResources() => projectTransitions.ReloadAsync(usesDefaultToolsSetting,
    ConfigureMcpAuthorizationUrlPresenter, RefreshProjectUi, LoadSessionTranscript);
void AdoptProjectSession(ProjectSessionRuntime replacement) => projectTransitions.Adopt(replacement,
    ConfigureMcpAuthorizationUrlPresenter, RefreshProjectUi, () =>
    {
        terminalScreen?.SetFooter(IdleFooter());
        LoadSessionTranscript();
    });
Task<bool> SwitchProjectSessionAsync(string reference, CancellationToken token) =>
    projectTransitions.SwitchAsync(reference, AdoptProjectSession, token);
Task ValidateRpcSessionSwitchAsync(string reference, CancellationToken token) =>
    projectTransitions.ValidateSwitchAsync(reference, token);

if (projectBinding.Arguments.Mode == "rpc")
{
    void AdoptRpcSession(SessionBranchResult replacement)
    {
        conversationBinding.Adopt(replacement.Conversation, replacement.Run, replacement.Path);
    }

    async Task<bool> StartRpcSessionAsync(string? parentSession, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var created = await projectTransitions.Sessions.NewAsync(conversationBinding.Conversation, conversationBinding.Path, parentSession);
        AdoptRpcSession(created);
        return false;
    }

    async Task<string?> ForkRpcSessionAsync(string entryId, ConversationSession branchSnapshot, string prompt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var forked = await projectTransitions.Sessions.ForkPreparedAsync(conversationBinding.Conversation, conversationBinding.Path, entryId, branchSnapshot, prompt);
        AdoptRpcSession(forked);
        return forked.Prompt;
    }

    async Task<string?> PrepareRpcCloneAsync(ConversationSession branchSnapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await projectTransitions.Sessions.PrepareCloneAsync(conversationBinding.Conversation, conversationBinding.Path, branchSnapshot, cancellationToken);
    }

    async Task<bool> CloneRpcSessionAsync(ConversationSession branchSnapshot, string? branchPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cloned = await projectTransitions.Sessions.ActivatePreparedCloneAsync(branchSnapshot, branchPath);
        AdoptRpcSession(cloned);
        return false;
    }

    await new RpcMode(Console.In, Console.Out, conversationBinding.Run,
        cancellationToken => conversationBinding.Run.PersistAsync(cancellationToken),
        projectBinding.Resources, GetRpcModelsAsync,
        extensionLease.Current.Registration, () => modelBinding.Selection.Authenticated ? null :
            $"Provider '{modelBinding.Selection.Provider.Id}' is not authenticated. Use /login {modelBinding.Selection.Provider.Id} or configure {modelBinding.Selection.Provider.ApiKeyEnvironment ?? "a credential"}.",
        modelTransitions.SelectRpcModelAsync, () => conversationBinding.Run, () => modelBinding.Thinking, () => modelRuntime.Scope.Count > 0,
        modelTransitions.SetRpcThinkingLevelAsync, () => ThinkingLevels.AvailableForModel(modelBinding.Selection.Model.Reasoning,
            modelBinding.Selection.Model.ThinkingLevelMap),
        () => modelBinding.Selection.Model.Reasoning == true, () => ProviderChatClientFactory.ResolveProtocol(modelBinding.Selection),
        modelTransitions.SetRpcThinkingLevelDuringRunAsync, rpcUserSettings.SetAutoRetryEnabledAsync, StartRpcSessionAsync,
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
                            await terminalProviderCommands.SetThinkingAsync(argument, modelTransitions.ReplaceAsync);
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
                            await terminalProviderCommands.LogoutAsync(argument, modelTransitions.ReplaceAsync);
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
                            await terminalProviderCommands.SelectNamedModelAsync(argument, modelTransitions.ApplySelectionAsync);
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
                                branch = await projectTransitions.Sessions.CloneAsync(conversationBinding.Conversation, conversationBinding.Path);
                            else
                                branch = await projectTransitions.Sessions.NewAsync(conversationBinding.Conversation, conversationBinding.Path, conversationBinding.Path);
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
