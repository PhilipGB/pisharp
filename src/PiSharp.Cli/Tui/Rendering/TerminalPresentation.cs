using System.Text.Json;
using Microsoft.Extensions.AI;
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

using PiSharp.Runtime.Codemode;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalPresentation : IDisposable
{
    private readonly string agentDirectory;
    private readonly string invocationDirectory;
    private readonly ActiveProjectRuntime projectBinding;
    private readonly ActiveModelRuntime modelBinding;
    private readonly ActiveConversationRuntime conversationBinding;
    private readonly ExtensionLease extensionLease;
    private readonly ProviderModelRuntime modelRuntime;
    private readonly TerminalEditor? editor;
    private readonly TerminalScreen? terminalScreen;
    private readonly TerminalMode? terminalMode;
    private TerminalThemeCatalog terminalThemeCatalog = null!;
    private string? cliThemeOverride;
    private bool hideThinkingBlock;
    public TerminalEditor? Editor => editor;
    public TerminalScreen? Screen => terminalScreen;
    public TerminalClipboard Clipboard { get; }
    public TerminalThemeCatalog Themes => terminalThemeCatalog;
    public bool HideThinking => hideThinkingBlock;

    public TerminalPresentation(string agentDirectory, string invocationDirectory, bool print, CliTerminalIO terminalIO,
        ActiveProjectRuntime projectBinding, ActiveModelRuntime modelBinding, ActiveConversationRuntime conversationBinding,
        ExtensionLease extensionLease, ProviderModelRuntime modelRuntime)
    {
        this.agentDirectory = agentDirectory;
        this.invocationDirectory = invocationDirectory;
        this.projectBinding = projectBinding;
        this.modelBinding = modelBinding;
        this.conversationBinding = conversationBinding;
        this.extensionLease = extensionLease;
        this.modelRuntime = modelRuntime;
        var completion = new TerminalResourceCompletion(projectBinding, extensionLease);
        editor = !print && projectBinding.Arguments.Mode is not ("json" or "rpc")
            ? new TerminalEditor(completion.Commands, agentDirectory, () => projectBinding.WorkingDirectory,
                initiallyExpanded: projectBinding.Arguments.Verbose, describeCommand: completion.Describe) : null;
        cliThemeOverride = editor is null ? null : projectBinding.Arguments.UseTheme;
        terminalThemeCatalog = CreateTerminalThemeCatalog();
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
        terminalScreen = editor is null ? null : new TerminalScreen(terminalIO.Output,
            terminalIO.Error,
            getColumns: null, getRows: null, imageRenderer: new TerminalImageRenderer(), theme: initialTerminalTheme,
            queryTerminalColors: !Console.IsInputRedirected && !Console.IsOutputRedirected,
            followTerminalAppearance: ThemeFollowsTerminalAppearance(ActiveThemeSetting()), deferInitialRender: true,
            minimumRenderInterval: TimeSpan.FromMilliseconds(16));
        try
        {
            terminalScreen?.SetThemeResolver(colors => ResolveConfiguredTheme(ActiveThemeSetting(), colors));
            terminalScreen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
            hideThinkingBlock = projectBinding.Settings.HideThinkingBlock == true;
            terminalScreen?.SetThinkingBlocksHidden(hideThinkingBlock);
            terminalScreen?.Activate();
            terminalMode = editor is null ? null : TerminalMode.Enter(terminalScreen);
            ConfigureMcpAuthorizationUrlPresenter(projectBinding.Runtime);
            editor?.AttachScreen(terminalScreen);
            editor?.CompleteInitialTerminalColorQuery();
            terminalScreen?.SetFooter(IdleFooter(providerCount: 1), render: false);
            terminalScreen?.RenderInitial();
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
            Clipboard = new TerminalClipboard(writeTerminalControl: value =>
            {
                if (terminalScreen is { IsActive: true }) terminalScreen.WriteControl(value);
                else Console.Write(value);
            });
            terminalScreen?.SetFooter(IdleFooter(providerCount: 1));
            terminalScreen?.SetFooter(IdleFooter());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void ClearThemeOverride() => cliThemeOverride = null;
    public void SetHideThinking(bool hide)
    {
        hideThinkingBlock = hide;
        terminalScreen?.SetThinkingBlocksHidden(hide);
    }

    public void Dispose()
    {
        try { terminalMode?.Dispose(); }
        finally { terminalScreen?.Dispose(); }
    }

    public string? ActiveThemeSetting() => cliThemeOverride ?? projectBinding.Settings.Theme;
    TerminalThemeCatalog CreateTerminalThemeCatalog() => new(agentDirectory, projectBinding.Trusted ? projectBinding.WorkingDirectory : null,
        trueColorOverride: projectBinding.Settings.TerminalTrueColorOverride,
        userThemePaths: projectBinding.BaseUserSettings.Themes, projectThemePaths: projectBinding.ProjectSettings?.Themes,
        discoverThemes: !projectBinding.Arguments.NoThemes, explicitThemePaths: (projectBinding.Arguments.ThemePaths ?? [])
            .Concat(projectBinding.Runtime.ExtensionResources.ThemePaths.Select(path => path.Path)).ToArray(),
        explicitThemeBaseDirectory: invocationDirectory);
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
    public void ConfigureMcpAuthorizationUrlPresenter(ProjectRuntimeContext context)
    {
        Action<string, Uri>? presenter = terminalScreen is null ? null : (serverName, url) =>
            terminalScreen.AppendMcpAuthorizationNotification(serverName, url,
                editor?.GetDisplayKeys("app.message.copy"), editor?.GetDisplayKeys("app.interrupt"));
        Action<string>? completed = terminalScreen is null ? null : terminalScreen.ClearMcpAuthorizationNotification;
        context.SetMcpAuthorizationUrlPresenter(presenter, completed);
    }
    public string IdleFooter(int? providerCount = null) => TerminalModelStatus.FormatIdleFooter(modelBinding.Selection.Model, modelBinding.Thinking, conversationBinding.Conversation,
        conversationBinding.Run.ContextWindowTokens, projectBinding.Settings.AutoCompactionEnabled(Environment.GetEnvironmentVariable),
        terminalScreen?.TerminalWidth ?? 80, providerCount ?? modelRuntime.AvailableModelProviderCount,
        conversationBinding.Run.EstimatedContextTokens);
    public void RefreshTerminalTheme()
    {
        if (terminalScreen is null) return;
        terminalThemeCatalog = CreateTerminalThemeCatalog();
        terminalScreen.SetThemeResolver(colors => ResolveConfiguredTheme(ActiveThemeSetting(), colors));
        terminalScreen.SetTheme(ResolveConfiguredTheme(ActiveThemeSetting(), terminalScreen.CurrentTerminalColors));
        terminalScreen.SetTerminalAppearanceFollowing(ThemeFollowsTerminalAppearance(ActiveThemeSetting()));
    }
    public void LoadSessionTranscript()
    {
        if (terminalScreen is null) return;
        var history = new InteractiveTranscript(Console.Out, Console.Error, interactive: true,
            hideThinking: hideThinkingBlock, screen: terminalScreen,
            toolRenderer: name => extensionLease.Current.Registration.GetToolRenderer(name),
            workingDirectory: conversationBinding.Conversation.WorkingDirectory);
        history.LoadHistory(conversationBinding.Conversation);
    }
    public InteractiveTranscript CreateInteractiveTranscript(bool interactive = true) => new(Console.Out, Console.Error, interactive,
        hideThinking: hideThinkingBlock, screen: terminalScreen,
        toolRenderer: name => extensionLease.Current.Registration.GetToolRenderer(name),
        workingDirectory: projectBinding.WorkingDirectory);
}
