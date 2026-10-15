using Microsoft.Extensions.AI;
using PiSharp.Cli.Composition;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Startup;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Tui;

internal static class InteractiveMode
{
    public static async Task RunAsync(string agentDirectory, PreparedProjectStartup startup,
        ActiveProjectRuntime projectBinding, ActiveModelRuntime modelBinding, ActiveConversationRuntime conversationBinding,
        ProviderModelRuntime modelRuntime, ModelRuntimeController modelRuntimeController, ConversationModelTransitions modelTransitions,
        ProjectConversationTransitions projectTransitions, ExtensionLease extensionLease, HttpClient catalogHttp,
        TerminalPresentation presentation, TerminalConversationExecution execution)
    {
        var editor = presentation.Editor;
        var terminalScreen = presentation.Screen;
        if (projectBinding.Configuration.ExtensionUi is ProjectTrustStartupUi extensionUi && terminalScreen is not null)
            extensionUi.SetInteractiveNotificationHandler((message, type) => terminalScreen.AppendThemedStatusLine(
                type == "info" ? message : $"{type}: {message}"));
        var terminalClipboard = presentation.Clipboard;
        var trustStore = startup.Trust;
        var usesDefaultToolsSetting = startup.UsesDefaultToolsSetting;
        var prompt = projectBinding.Arguments.Prompt;
        var terminalProviderCommands = new TerminalProviderCommands(modelRuntime, modelRuntimeController, modelBinding, () => Console.Out);
        var sessionNavigation = new TerminalSessionNavigation(projectBinding, modelBinding, conversationBinding, projectTransitions, presentation);
        var settingsCommands = new TerminalSettingsCommands(agentDirectory, projectBinding, modelBinding, conversationBinding,
            modelTransitions, presentation, execution);
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
            sessionNavigation.RefreshPicker();
            presentation.RefreshTerminalTheme();
            terminalScreen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
        }
        Task ReloadResources() => projectTransitions.ReloadAsync(usesDefaultToolsSetting,
            presentation.ConfigureMcpAuthorizationUrlPresenter, RefreshProjectUi, presentation.LoadSessionTranscript);
        void AdoptProjectSession(ProjectSessionRuntime replacement) => projectTransitions.Adopt(replacement,
            presentation.ConfigureMcpAuthorizationUrlPresenter, RefreshProjectUi, () =>
            {
                terminalScreen?.SetFooter(presentation.IdleFooter());
                presentation.LoadSessionTranscript();
            });
        Task RunAsync(string input, IReadOnlyList<DataContent>? images = null) =>
            execution.RunAsync(input, images, applicationActions.HandleAsync);

        if (!string.IsNullOrWhiteSpace(prompt) || (projectBinding.Arguments.FileArguments?.Count ?? 0) > 0)
        {
            CliFileArguments.PromptFiles promptFiles;
            try { promptFiles = await CliFileArguments.ProcessFilesAsync(prompt, projectBinding.Arguments.FileArguments, projectBinding.WorkingDirectory); }
            catch (Exception e) when (e is IOException or ArgumentException) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; return; }
            if (!string.IsNullOrWhiteSpace(promptFiles.Text) || promptFiles.Images.Count > 0)
                await RunAsync(promptFiles.Text, promptFiles.Images);
        }
        while (true)
        {
            var line = await editor!.ReadLineAsync(HandleEditorApplicationAction);
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
                                await terminalProviderCommands.SetThinkingAsync(argument, modelTransitions.ReplaceAsync);
                                break;
                            case "/login":
                                await terminalProviderLogin!.ShowAsync(argument);
                                terminalScreen?.SetFooter(presentation.IdleFooter());
                                break;
                            case "/llama":
                                await terminalLlamaManager!.ShowAsync(argument);
                                terminalScreen?.SetFooter(presentation.IdleFooter());
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
                                    HandleEditorApplicationAction);
                                break;
                        }
                    }
                }
                catch (Exception e) { Console.Error.WriteLine($"Session error: {e.Message}"); Environment.ExitCode = 1; }
                terminalScreen?.SetFooter(presentation.IdleFooter());
                continue;
            }
            await RunAsync(line);
        }
    }
}
