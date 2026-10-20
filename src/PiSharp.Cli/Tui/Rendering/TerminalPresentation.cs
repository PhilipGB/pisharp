using PiSharp.Cli.Composition;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Startup;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalPresentation : IDisposable
{
    private readonly string _agentDirectory;
    private readonly string _invocationDirectory;
    private readonly ActiveProjectRuntime _projectBinding;
    private readonly ActiveModelRuntime _modelBinding;
    private readonly ActiveConversationRuntime _conversationBinding;
    private readonly ExtensionLease _extensionLease;
    private readonly ProviderModelRuntime _modelRuntime;
    private readonly TerminalEditor? _editor;
    private readonly TerminalScreen? _terminalScreen;
    private readonly TerminalMode? _terminalMode;
    private TerminalThemeCatalog _terminalThemeCatalog = null!;
    private string? _cliThemeOverride;
    private string? _extensionThemeOverride;
    private bool _hideThinkingBlock;
    public TerminalEditor? Editor => _editor;
    public TerminalScreen? Screen => _terminalScreen;
    public TerminalClipboard Clipboard { get; }
    public TerminalThemeCatalog Themes => _terminalThemeCatalog;
    public bool HideThinking => _hideThinkingBlock;

    public TerminalPresentation(string agentDirectory, string invocationDirectory, bool print, CliTerminalIO terminalIO,
        ActiveProjectRuntime projectBinding, ActiveModelRuntime modelBinding, ActiveConversationRuntime conversationBinding,
        ExtensionLease extensionLease, ProviderModelRuntime modelRuntime)
    {
        _agentDirectory = agentDirectory;
        _invocationDirectory = invocationDirectory;
        _projectBinding = projectBinding;
        _modelBinding = modelBinding;
        _conversationBinding = conversationBinding;
        _extensionLease = extensionLease;
        _modelRuntime = modelRuntime;
        var completion = new TerminalResourceCompletion(_projectBinding, _extensionLease);
        _editor = !print && _projectBinding.Arguments.Mode is not ("json" or "rpc")
            ? new TerminalEditor(completion.Commands, _agentDirectory, () => _projectBinding.WorkingDirectory,
                initiallyExpanded: _projectBinding.Arguments.Verbose, describeCommand: completion.Describe,
                getExtensionShortcuts: () => _extensionLease.Current.Registration.ExtensionShortcuts) : null;
        _cliThemeOverride = _editor is null ? null : _projectBinding.Arguments.UseTheme;
        _terminalThemeCatalog = CreateTerminalThemeCatalog();
        var initialTerminalTheme = ResolveConfiguredTheme(ActiveThemeSetting());
        var startupPresentation = new TerminalStartupPresentation(_projectBinding.Settings.QuietStartup, _projectBinding.Arguments.Verbose,
            action => _editor?.GetDisplayKeys(action));
        if (_editor is not null && startupPresentation.ShowDetails && _modelRuntime.Scope.Count > 0)
        {
            var cycleKey = _editor.GetDisplayKeys("app.model.cycleForward");
            var cycleHint = string.IsNullOrWhiteSpace(cycleKey) ? "" : $" ({cycleKey} to cycle)";
            Console.WriteLine(initialTerminalTheme.Style("dim",
                $"Model scope: {string.Join(", ", _modelRuntime.Scope)}{cycleHint}"));
        }
        _terminalScreen = _editor is null ? null : new TerminalScreen(terminalIO.Output,
            terminalIO.Error,
            getColumns: null, getRows: null, imageRenderer: new TerminalImageRenderer(), theme: initialTerminalTheme,
            queryTerminalColors: !Console.IsInputRedirected && !Console.IsOutputRedirected,
            followTerminalAppearance: ThemeFollowsTerminalAppearance(ActiveThemeSetting()), deferInitialRender: true,
            minimumRenderInterval: TimeSpan.FromMilliseconds(16));
        try
        {
            _terminalScreen?.SetThemeResolver(colors => ResolveConfiguredTheme(ActiveThemeSetting(), colors));
            _terminalScreen?.SetMarkdownCodeBlockIndent(_projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
            ApplyTuiSettings(_projectBinding.Settings);
            _hideThinkingBlock = _projectBinding.Settings.HideThinkingBlock == true;
            _terminalScreen?.SetThinkingBlocksHidden(_hideThinkingBlock);
            _terminalScreen?.Activate();
            _terminalMode = _editor is null ? null : TerminalMode.Enter(_terminalScreen);
            ConfigureMcpAuthorizationUrlPresenter(_projectBinding.Runtime);
            _editor?.AttachScreen(_terminalScreen);
            _terminalScreen?.SetFooter(IdleFooter(providerCount: 1), render: false);
            _terminalScreen?.RenderInitial();
            _editor?.CompleteInitialTerminalColorQuery();
            LoadSessionTranscript();
            var startupTrustNotice = !_projectBinding.Trusted && ProjectTrust.HasProtectedResources(_projectBinding.WorkingDirectory)
                ? "This project is not trusted. Project .pi resources and packages are ignored. Use /trust to save a trust decision, then restart PiSharp."
                : null;
            if (_editor is not null)
            {
                IReadOnlyList<TerminalStartupDetail> CurrentStartupDetails() => TerminalStartupDetails.Build(
                    _projectBinding.Runtime.ContextFiles, _projectBinding.Resources, _extensionLease.Current, _projectBinding.WorkingDirectory, _terminalThemeCatalog.Diagnostics);
                _terminalScreen?.SetStartupHeader((theme, columns, expanded) =>
                    startupPresentation.BuildLogo(theme));
                _terminalScreen?.SetStartupContent((theme, columns, expanded) =>
                    startupPresentation.BuildContent(theme, columns, CurrentStartupDetails(), startupTrustNotice, expanded));
            }
            Clipboard = new TerminalClipboard(writeTerminalControl: value =>
            {
                if (_terminalScreen is { IsActive: true }) _terminalScreen.WriteControl(value);
                else Console.Write(value);
            });
            _terminalScreen?.SetFooter(IdleFooter(providerCount: 1));
            _terminalScreen?.SetFooter(IdleFooter());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void ClearThemeOverride()
    {
        _cliThemeOverride = null;
        _extensionThemeOverride = null;
    }

    internal PiSharpExtensionUiThemeResult SetExtensionTheme(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(false, "Theme name cannot be empty.");
        if (_terminalScreen is null) return new(false, "Interactive theme selection is unavailable.");
        try
        {
            _ = _terminalThemeCatalog.Load(name);
            var saved = UserSettingsPersistence.SaveAsync(_agentDirectory, _projectBinding.WorkingDirectory,
                projectScope: false, trusted: _projectBinding.Trusted, setting: "theme", value: name,
                user: _projectBinding.BaseUserSettings, project: _projectBinding.ProjectSettings,
                environment: Environment.GetEnvironmentVariable).GetAwaiter().GetResult();
            _projectBinding.SetSettings(saved.User, saved.Project, saved.Effective);
            _projectBinding.PublishSettingsConfiguration();
            _cliThemeOverride = null;
            _extensionThemeOverride = name;
            RefreshTerminalTheme();
            return new(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException or ArgumentException or FormatException or
            NotSupportedException)
        {
            return new(false, exception.Message);
        }
    }

    public void SetHideThinking(bool hide)
    {
        _hideThinkingBlock = hide;
        _terminalScreen?.SetThinkingBlocksHidden(hide);
    }

    public void ApplyTuiSettings(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _terminalScreen?.SetShowHardwareCursor(settings.ShowHardwareCursor == true);
        _terminalScreen?.SetEditorPaddingX(settings.EditorPaddingX ?? 0);
        _terminalScreen?.SetOutputPad(settings.OutputPad ?? 1);
        _terminalScreen?.SetAutocompleteMaxVisible(settings.AutocompleteMaxVisible ?? 5);
        _terminalScreen?.SetClearOnShrink(settings.ClearOnShrink == true);
        _terminalScreen?.SetShowTerminalProgress(settings.ShowTerminalProgress == true);
    }

    public void Dispose()
    {
        try { _terminalMode?.Dispose(); }
        finally { _terminalScreen?.Dispose(); }
    }

    public string? ActiveThemeSetting() => _extensionThemeOverride ?? _cliThemeOverride ?? _projectBinding.Settings.Theme;
    TerminalThemeCatalog CreateTerminalThemeCatalog() => new(_agentDirectory, _projectBinding.Trusted ? _projectBinding.WorkingDirectory : null,
        trueColorOverride: _projectBinding.Settings.TerminalTrueColorOverride,
        userThemePaths: _projectBinding.BaseUserSettings.Themes, projectThemePaths: _projectBinding.ProjectSettings?.Themes,
        discoverThemes: !_projectBinding.Arguments.NoThemes, explicitThemePaths: (_projectBinding.Arguments.ThemePaths ?? [])
            .Concat(_projectBinding.Runtime.ExtensionResources.ThemePaths.Select(path => path.Path)).ToArray(),
        explicitThemeBaseDirectory: _invocationDirectory,
        userPackages: _projectBinding.BaseUserSettings.Packages,
        projectPackages: _projectBinding.ProjectSettings?.Packages,
        managedPackageRoots: _projectBinding.Configuration.ManagedPackageRoots);
    TerminalTheme ResolveConfiguredTheme(string? themeSetting, TerminalColorState? terminalColors = null)
    {
        var colors = terminalColors ?? new TerminalColorState();
        try { return _terminalThemeCatalog.Resolve(themeSetting, colors); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"Could not load theme '{themeSetting}': {error.Message}. Using terminal appearance defaults.");
            return _terminalThemeCatalog.Resolve(null, colors);
        }
    }
    bool ThemeFollowsTerminalAppearance(string? themeSetting) => themeSetting is null or "" or "system" ||
        themeSetting.Count(character => character == '/') == 1;
    public void ConfigureMcpAuthorizationUrlPresenter(ProjectRuntimeContext context)
    {
        Action<string, Uri>? presenter = _terminalScreen is null ? null : (serverName, url) =>
            _terminalScreen.AppendMcpAuthorizationNotification(serverName, url,
                _editor?.GetDisplayKeys("app.message.copy"), _editor?.GetDisplayKeys("app.interrupt"));
        Action<string>? completed = _terminalScreen is null ? null : _terminalScreen.ClearMcpAuthorizationNotification;
        context.SetMcpAuthorizationUrlPresenter(presenter, completed, _editor?.McpAuthorizationCallbackInput);
    }
    public string IdleFooter(int? providerCount = null) => TerminalModelStatus.FormatIdleFooter(_modelBinding.Selection.Model, _modelBinding.Thinking, _conversationBinding.Conversation,
        _conversationBinding.Run.ContextWindowTokens, _projectBinding.Settings.AutoCompactionEnabled(Environment.GetEnvironmentVariable),
        _terminalScreen?.TerminalWidth ?? 80, providerCount ?? _modelRuntime.AvailableModelProviderCount,
        _conversationBinding.Run.EstimatedContextTokens);
    public void RefreshTerminalTheme()
    {
        if (_terminalScreen is null) return;
        _terminalThemeCatalog = CreateTerminalThemeCatalog();
        _terminalScreen.SetThemeResolver(colors => ResolveConfiguredTheme(ActiveThemeSetting(), colors));
        _terminalScreen.SetTheme(ResolveConfiguredTheme(ActiveThemeSetting(), _terminalScreen.CurrentTerminalColors));
        _terminalScreen.SetTerminalAppearanceFollowing(ThemeFollowsTerminalAppearance(ActiveThemeSetting()));
    }
    public void LoadSessionTranscript()
    {
        if (_terminalScreen is null) return;
        var history = new InteractiveTranscript(Console.Out, Console.Error, interactive: true,
            hideThinking: _hideThinkingBlock, screen: _terminalScreen,
            toolRenderer: name => _extensionLease.Current.Registration.GetToolRenderer(name),
            workingDirectory: _conversationBinding.Conversation.WorkingDirectory);
        history.LoadHistory(_conversationBinding.Conversation);
    }
    public InteractiveTranscript CreateInteractiveTranscript(bool interactive = true) => new(Console.Out, Console.Error, interactive,
        hideThinking: _hideThinkingBlock, screen: _terminalScreen,
        toolRenderer: name => _extensionLease.Current.Registration.GetToolRenderer(name),
        workingDirectory: _projectBinding.WorkingDirectory);
}
