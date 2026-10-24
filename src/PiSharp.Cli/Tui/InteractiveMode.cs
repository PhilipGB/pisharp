using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Cli.Composition;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Startup;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using NativeCustomMessage = PiSharp.Extensions.Abstractions.PiSharpCustomMessage;

namespace PiSharp.Cli.Tui;

internal static class InteractiveMode
{
    /// <summary>Stops terminal rendering before extension UI components and callbacks are released.</summary>
    private sealed class TerminalShutdownLease(TerminalPresentation presentation) : IDisposable
    {
        public void Dispose() => presentation.Dispose();
    }

    public static async Task RunAsync(string agentDirectory, PreparedProjectStartup startup,
        ActiveProjectRuntime projectBinding, ActiveModelRuntime modelBinding, ActiveConversationRuntime conversationBinding,
        ProviderModelRuntime modelRuntime, ModelRuntimeController modelRuntimeController, ConversationModelTransitions modelTransitions,
        ProjectConversationTransitions projectTransitions, ExtensionLease extensionLease, HttpClient catalogHttp,
        TerminalPresentation presentation, TerminalConversationExecution execution)
    {
        var editor = presentation.Editor;
        var terminalScreen = presentation.Screen;
        using var extensionUiBinding = projectBinding.Configuration.ExtensionUi is ProjectTrustStartupUi packageUi &&
            terminalScreen is not null ? packageUi.BindInteractiveScreen(terminalScreen, editor, presentation) : null;
        using var terminalShutdown = new TerminalShutdownLease(presentation);
        if (projectBinding.Configuration.ExtensionUi is ProjectTrustStartupUi extensionUi && terminalScreen is not null)
            extensionUi.SetInteractiveNotificationHandler((message, type) => terminalScreen.AppendThemedStatusLine(
                type == "info" ? message : $"{type}: {message}"));
        var terminalClipboard = presentation.Clipboard;
        var trustStore = startup.Trust;
        var usesDefaultToolsSetting = startup.UsesDefaultToolsSetting;
        var prompt = projectBinding.Arguments.Prompt;
        var shutdownRequested = 0;
        TerminalSessionNavigation? sessionNavigationReference = null;
        var terminalThinkingSelector = editor is null ? null : new TerminalThinkingSelector(editor);
        var settingsCommands = new TerminalSettingsCommands(agentDirectory, projectBinding, modelBinding, conversationBinding,
            modelTransitions, modelRuntimeController, modelRuntime, presentation, execution);
        var terminalProviderCommands = new TerminalProviderCommands(modelRuntime, modelRuntimeController, modelBinding,
            () => Console.Out, terminalThinkingSelector, editor,
            value => settingsCommands.SaveSettingAsync(projectScope: false, "enabledModels", value));
        var sessionNavigation = new TerminalSessionNavigation(projectBinding, conversationBinding, projectTransitions,
            presentation, AdoptProjectSession);
        sessionNavigationReference = sessionNavigation;
        var modelSelection = new TerminalModelSelectionCommands(modelRuntime, modelRuntimeController, projectBinding, modelBinding,
            modelTransitions, presentation, settingsCommands);
        var inputActions = new TerminalInputActions(projectBinding, conversationBinding, presentation);
        var sessionShare = editor is null ? null : new TerminalSessionShare(modelRuntime, catalogHttp, editor,
            () => conversationBinding.Run, () => projectBinding.Settings);
        var applicationActions = new TerminalApplicationActions(settingsCommands, modelSelection, sessionNavigation, inputActions, presentation);
        Task HandleEditorApplicationAction(string action) => applicationActions.HandleAsync(action);
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
        void RefreshProjectUi()
        {
            sessionNavigationReference?.RefreshPicker();
            presentation.RefreshTerminalTheme();
            terminalScreen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
        }
        Task ReloadResources() => projectTransitions.ReloadAsync(usesDefaultToolsSetting,
            presentation.ConfigureMcpAuthorizationUrlPresenter, RefreshProjectUi, presentation.LoadSessionTranscript);
        Task AdoptProjectSession(ProjectSessionRuntime replacement) => projectTransitions.AdoptAsync(replacement,
            presentation.ConfigureMcpAuthorizationUrlPresenter, RefreshProjectUi, () =>
            {
                presentation.SetIdleFooter();
                presentation.LoadSessionTranscript();
            });
        IReadOnlyList<PiSharpExtensionCommandInfo> GetExtensionCommands()
        {
            var commands = InteractiveCommandCatalog.BuiltIns.Select(command =>
                new PiSharpExtensionCommandInfo(command.Command, command.Description, "builtin")).ToList();
            if (projectBinding.Resources is { } resources)
            {
                commands.AddRange(resources.Prompts.Select(item =>
                    new PiSharpExtensionCommandInfo(item.Name, item.Description, "prompt", item.SourceInfo.Path)));
                commands.AddRange(resources.Skills.Select(item =>
                    new PiSharpExtensionCommandInfo("skill:" + item.Name, item.Description, "skill", item.SourceInfo.Path)));
            }
            commands.AddRange(extensionLease.Current.Registration.CommandInfo.Select(pair =>
                new PiSharpExtensionCommandInfo(pair.Key, pair.Value.Description, "extension", pair.Value.SourceInfo.Path)));
            return Array.AsReadOnly(commands.GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray());
        }
        Task<bool> TryExecuteActiveExtensionCommandAsync(string text, CancellationToken cancellationToken) =>
            InteractiveResourceCommands.TryExecuteExtensionCommandAsync(text,
                () => extensionLease.Current.Registration, CreateExtensionCommandContext, cancellationToken);
        async Task QueueExtensionUserMessageAsync(ConversationRun run, string text, CancellationToken cancellationToken)
        {
            var intercepted = await run.InterceptInputAsync(text, null, PiSharpInputSource.Extension,
                PiSharpInputStreamingBehavior.Steer, cancellationToken);
            if (intercepted.Action == PiSharpInputResultAction.Handled) return;
            var prompt = intercepted.Action == PiSharpInputResultAction.Transform ? intercepted.Text! : text;
            var images = intercepted.Action == PiSharpInputResultAction.Transform ? intercepted.Images : null;
            InteractivePromptExecution.ValidateImages(images, modelBinding.Selection, projectBinding.Settings);
            var expanded = await projectBinding.Resources.ResolveInputAsync(prompt, cancellationToken,
                error => Console.Error.WriteLineAsync($"Extension error ({error.ExtensionPath}): {error.Error}"));
            var contents = new List<AIContent> { new TextContent(expanded) };
            if (images is not null) contents.AddRange(images);
            if (!run.TryQueueExtensionUserMessage(new ChatMessage(ChatRole.User, contents), expanded))
                throw new InvalidOperationException("The active conversation run settled before the message could be queued.");
        }
        Task RunAsync(string input, IReadOnlyList<DataContent>? images = null) =>
            execution.RunAsync(input, images, applicationActions.HandleAsync,
                handleExtensionShortcut: HandleExtensionShortcutAsync,
                tryExecuteExtensionCommand: TryExecuteActiveExtensionCommandAsync);

        PiSharpExtensionCommandContext CreateExtensionCommandContext(ExtensionCommandInfo commandInfo)
        {
            var run = conversationBinding.Run;
            var controls = PiSharpExtensionCommandControlFactory.Create(
                () => conversationBinding.Run,
                GetExtensionCommands,
                () => extensionLease.Current.Registration,
                modelRuntime,
                modelBinding,
                modelTransitions,
                execution.AbortCurrentRun,
                execution.CancelActiveRunAsync,
                () =>
                {
                    Interlocked.Exchange(ref shutdownRequested, 1);
                    execution.AbortCurrentRun();
                });
            async Task SendUserMessageAsync(string text, CancellationToken cancellationToken)
            {
                if (run.IsActive)
                {
                    await QueueExtensionUserMessageAsync(run, text, cancellationToken);
                    return;
                }
                await execution.RunAsync(text, handleApplicationAction: applicationActions.HandleAsync,
                    cancellationToken: cancellationToken, handleExtensionShortcut: HandleExtensionShortcutAsync,
                    source: PiSharpInputSource.Extension,
                    tryExecuteExtensionCommand: TryExecuteActiveExtensionCommandAsync);
            }

            async Task SendCustomMessageAsync(NativeCustomMessage message, bool triggerTurn,
                PiSharpMessageDeliveryMode deliveryMode, CancellationToken cancellationToken)
            {
                if (!await run.DeliverExtensionCustomMessageAsync(message,
                    commandInfo.SourceInfo.Path, triggerTurn, deliveryMode, cancellationToken)) return;
                await execution.RunCustomMessageAsync(message, commandInfo.SourceInfo.Path,
                    applicationActions.HandleAsync, cancellationToken, HandleExtensionShortcutAsync,
                    TryExecuteActiveExtensionCommandAsync);
            }

            var session = run.CreateExtensionSessionContext(commandInfo.SourceInfo.Path);
            var settingsAndFlags = PiSharpExtensionSettingsFlagContextFactory.Create(projectBinding,
                commandInfo.SourceInfo.Path);
            var capabilities = PiSharpExtensionCapabilitiesFactory.Create(projectBinding, modelBinding, run,
                commandInfo.SourceInfo.Path, session, controls, SendUserMessageAsync, SendCustomMessageAsync,
                GetExtensionCommands, settingsAndFlags);
            var callbackUi = extensionLease.Current.Registration.CreateCallbackUi(commandInfo.SourceInfo.Path,
                projectBinding.Configuration.ExtensionUi);
            return new PiSharpExtensionCommandContext(projectBinding.WorkingDirectory, !run.IsActive,
                projectBinding.Trusted, run.PendingPromptCount > 0,
                SendUserMessageAsync, SendCustomMessageAsync,
                projectBinding.Configuration.ExtensionMode, projectBinding.Configuration.HasUserInterface,
                callbackUi, controls)
            {
                Session = session,
                SettingsAndFlags = settingsAndFlags,
                Capabilities = capabilities
            };
        }

        conversationBinding.SetExtensionCapabilitiesFactory(run => (sourcePath, session) =>
        {
            var controls = PiSharpExtensionCommandControlFactory.Create(
                () => conversationBinding.Run,
                GetExtensionCommands,
                () => extensionLease.Current.Registration,
                modelRuntime,
                modelBinding,
                modelTransitions,
                execution.AbortCurrentRun,
                execution.CancelActiveRunAsync,
                () =>
                {
                    Interlocked.Exchange(ref shutdownRequested, 1);
                    execution.AbortCurrentRun();
                });

            async Task SendUserMessageAsync(string text, CancellationToken cancellationToken)
            {
                var currentRun = conversationBinding.Run;
                if (currentRun.IsActive)
                {
                    await QueueExtensionUserMessageAsync(currentRun, text, cancellationToken);
                    return;
                }
                await execution.RunAsync(text, handleApplicationAction: applicationActions.HandleAsync,
                    cancellationToken: cancellationToken, handleExtensionShortcut: HandleExtensionShortcutAsync,
                    source: PiSharpInputSource.Extension,
                    tryExecuteExtensionCommand: TryExecuteActiveExtensionCommandAsync);
            }

            async Task SendCustomMessageAsync(NativeCustomMessage message, bool triggerTurn,
                PiSharpMessageDeliveryMode deliveryMode, CancellationToken cancellationToken)
            {
                var currentRun = conversationBinding.Run;
                if (!await currentRun.DeliverExtensionCustomMessageAsync(message, sourcePath,
                    triggerTurn, deliveryMode, cancellationToken)) return;
                await execution.RunCustomMessageAsync(message, sourcePath, applicationActions.HandleAsync,
                    cancellationToken, HandleExtensionShortcutAsync, TryExecuteActiveExtensionCommandAsync);
            }

            return PiSharpExtensionCapabilitiesFactory.Create(projectBinding, modelBinding, run, sourcePath,
                session, controls, SendUserMessageAsync, SendCustomMessageAsync, GetExtensionCommands);
        });

        async Task HandleExtensionShortcutAsync(ExtensionShortcutInfo shortcut, CancellationToken cancellationToken)
        {
            try
            {
                using var context = CreateExtensionCommandContext(new(shortcut.Description, shortcut.SourceInfo));
                await shortcut.Handler(context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                if (terminalScreen is not null) terminalScreen.AppendExtensionError(shortcut.SourceInfo.Path, error.Message);
                else Console.Error.WriteLine($"Extension \"{shortcut.SourceInfo.Path}\" error: {error.Message}");
            }
        }

        editor?.SetExtensionModalActionHandlers(HandleEditorApplicationAction, HandleExtensionShortcutAsync);

        if (!string.IsNullOrWhiteSpace(prompt) || (projectBinding.Arguments.FileArguments?.Count ?? 0) > 0)
        {
            CliFileArguments.PromptFiles promptFiles;
            try { promptFiles = await CliFileArguments.ProcessFilesAsync(prompt, projectBinding.Arguments.FileArguments, projectBinding.WorkingDirectory); }
            catch (Exception e) when (e is IOException or ArgumentException) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; return; }
            if (!string.IsNullOrWhiteSpace(promptFiles.Text) || promptFiles.Images.Count > 0)
                await RunAsync(promptFiles.Text, promptFiles.Images);
        }
        while (Volatile.Read(ref shutdownRequested) == 0)
        {
            var line = await editor!.ReadLineAsync(HandleEditorApplicationAction,
                dispatchExtensionShortcut: HandleExtensionShortcutAsync);
            if (line is null || line.Trim() is "/quit" or "/exit") break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (InteractiveBashExecutor.TryParse(line, out var bashCommand, out var excludeFromContext))
            {
                await execution.RunBashAsync(bashCommand, excludeFromContext);
                continue;
            }
            if (line.StartsWith('/'))
            {
                try
                {
                    var split = line.IndexOf(' ');
                    var command = split < 0 ? line : line[..split];
                    var argument = split < 0 ? "" : line[(split + 1)..].Trim();
                    if (command == "/tree")
                    {
                        await sessionNavigation.SelectTreePointAsync();
                        continue;
                    }
                    var sessionCommands = new InteractiveSessionCommands(projectBinding.Store, projectBinding.WorkingDirectory, projectBinding.Arguments.NoSession,
                        Console.Out, () => editor.ReadLineAsync(HandleEditorApplicationAction, enableApplicationActions: false),
                        presentation.LoadSessionTranscript);
                    if (!await sessionCommands.TryExecuteAsync(command, argument, conversationBinding.Conversation, conversationBinding.Run, conversationBinding.Path))
                    {
                        switch (command)
                        {
                            case "/share":
                                await sessionShare!.ShowAsync(argument);
                                break;
                            case "/import":
                                await terminalSessionImport!.ImportAsync(argument);
                                break;
                            case "/resume":
                                await sessionNavigation.ResumeCommandAsync(argument);
                                break;
                            case "/models":
                                await terminalProviderCommands.ListModelsAsync(argument, projectBinding.Arguments.Provider);
                                break;
                            case "/scoped-models":
                                await terminalProviderCommands.SetScopeAsync(argument, projectBinding.Arguments.Provider);
                                break;
                            case "/thinking":
                                await terminalProviderCommands.SetThinkingAsync(argument,
                                    projectBinding.Settings.GetModelThinkingLevel(modelBinding.Selection) ??
                                        projectBinding.Settings.DefaultThinkingLevel ?? ThinkingLevels.Default,
                                    settingsCommands.SaveModelThinkingLevelAsync, modelTransitions.ReplaceAsync);
                                break;
                            case "/login":
                                await terminalProviderLogin!.ShowAsync(argument);
                                presentation.SetIdleFooter();
                                break;
                            case "/llama":
                                await terminalLlamaManager!.ShowAsync(argument);
                                presentation.SetIdleFooter();
                                break;
                            case "/logout":
                                await terminalProviderCommands.LogoutAsync(argument, modelTransitions.ReplaceAsync);
                                break;
                            case "/compact":
                                {
                                    terminalScreen?.ReportTerminalProgress(null);
                                    bool compacted;
                                    try
                                    {
                                        compacted = await conversationBinding.Run.CompactWithResultAsync(argument) is not null;
                                    }
                                    finally
                                    {
                                        terminalScreen?.ClearTerminalProgress();
                                    }
                                    Console.WriteLine(compacted ? "Context compacted; full history retained." :
                                        "Nothing to compact (at least two completed user turns are required).");
                                    break;
                                }
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
                                await settingsCommands.SelectSettingsAsync();
                                break;
                            case "/model":
                                if (string.IsNullOrWhiteSpace(argument))
                                {
                                    await modelSelection.SelectModelAsync();
                                    break;
                                }
                                await terminalProviderCommands.SelectNamedModelAsync(argument, modelTransitions.ApplySelectionAsync);
                                modelSelection.ShowModelChangeStatus();
                                break;
                            case "/fork":
                                if (argument.Length == 0)
                                {
                                    await sessionNavigation.SelectForkAsync();
                                    break;
                                }
                                await sessionNavigation.ForkFromUserAsync(argument);
                                break;
                            case "/new":
                            case "/clone":
                                await sessionNavigation.NewOrCloneAsync(command);
                                break;
                            default:
                                await InteractiveResourceCommands.ExecuteAsync(command, argument, line, () => projectBinding.Resources,
                                    () => extensionLease.Current.Registration, text => RunAsync(text), editor, terminalScreen,
                                    HandleEditorApplicationAction, ReloadResources,
                                    projectBinding.Runtime.GetMcpManagementSnapshot,
                                    () => projectBinding.Settings.SkillCommandsEnabled,
                                    CreateExtensionCommandContext);
                                break;
                        }
                    }
                }
                catch (Exception e) { Console.Error.WriteLine($"Session error: {e.Message}"); Environment.ExitCode = 1; }
                presentation.SetIdleFooter();
                continue;
            }
            await RunAsync(line);
        }
    }
}
