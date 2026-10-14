using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Composition;
using PiSharp.Cli.Protocols;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Startup;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;

using var terminalIO = new CliTerminalIO();
var standardInput = terminalIO.Input;

var agentDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("PISHARP_AGENT_DIR") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp", "agent"));

var cli = await CliStartupCommands.ParseOrRunAsync(args, agentDirectory, Console.Out, Console.Error);

if (cli is null)
{
    return;
}

var startup = await ProjectStartupPreparation.PrepareAsync(cli, agentDirectory, standardInput);

if (startup is null)
{
    return;
}

using var projectConfigurationOwner = startup.Configuration;

var projectConfiguration = startup.Configuration;
var invocationDirectory = startup.InvocationDirectory;
var configuredSessionDirectory = startup.SessionDirectory;
var trustStore = startup.Trust;
var explicitThinking = startup.ExplicitThinking;

cli = startup.Arguments;

var currentDirectory = projectConfiguration.WorkingDirectory;
var userSettings = projectConfiguration.Settings;

using var catalogHttp = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(10)
};

ProviderModelRuntime modelRuntime;

try
{
    modelRuntime = await ProviderModelRuntime.CreateAsync(
        agentDirectory, cli.Local || userSettings.DefaultProvider == "local",
        Environment.GetEnvironmentVariable, catalogHttp, cli.ApiKey,
        cli.ListModels ? null : cli.ScopedModels,
        offline: cli.Offline || Environment.GetEnvironmentVariable("PI_OFFLINE") is { } offlineFlag &&
            offlineFlag.ToLowerInvariant() is "1" or "true" or "yes");
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or
    InvalidDataException or IOException or System.Text.Json.JsonException)
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
    Console.Error.WriteLine(e is ExtensionStartupFailure ? e.Message : $"Could not load project runtime: {e.Message}");
    Environment.ExitCode = e is ExtensionStartupFailure ? 1 : 2;
    return;
}

using var projectRuntimeOwner = projectRuntime;

modelRuntime.SetVirtualModelRegistry(projectRuntime.Extensions.Registration.VirtualModels);

var projectBinding = new ActiveProjectRuntime(projectConfiguration, projectRuntime, cli);

var initialSelection = await InitialModelComposition.ResolveAsync(
    modelRuntime, cli, explicitThinking, userSettings);

if (initialSelection is null)
{
    return;
}

var selection = initialSelection.Selection;
var thinking = initialSelection.Thinking;

using var codemodeHttp = new HttpClient
{
    Timeout = Timeout.InfiniteTimeSpan
};

var codemodeModels = new ProviderCodemodeModels(modelRuntime, codemodeHttp);
var modelRuntimeController = new ModelRuntimeController(modelRuntime, () => projectBinding.Settings,
    Environment.GetEnvironmentVariable);

if (cli.ListModels)
{
    await CliStartupCommands.ListModelsAsync(modelRuntime, cli, Console.Out);
    return;
}

IChatClient chat;

try
{
    chat = ProviderChatClientFactory.Create(
        selection, userSettings.Retry?.Provider, userSettings.HttpIdleTimeoutMs);
}
catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}

using var extensionLease = new ExtensionLease(projectRuntime.TransferExtensions());

var initialModel = InitialModelComposition.Create(
    projectRuntime, chat, selection, thinking, cli, userSettings,
    modelRuntimeController, codemodeModels);

if (initialModel is null)
{
    return;
}

var runComposition = new ConversationRunComposition(projectBinding, modelRuntimeController);

var opened = await ConversationStartup.OpenAsync(
    projectBinding, initialModel, modelRuntime, modelRuntimeController,
    codemodeModels, runComposition, currentDirectory, explicitThinking);

if (opened is null)
{
    return;
}

var modelBinding = opened.Model;
var conversationBinding = opened.Conversation;

var modelTransitions = new ConversationModelTransitions(projectBinding, modelBinding, conversationBinding,
    modelRuntimeController, codemodeModels, runComposition);

var projectTransitions = new ProjectConversationTransitions(
    agentDirectory, configuredSessionDirectory, trustStore,
    projectBinding, modelBinding, conversationBinding,
    modelRuntime, codemodeModels, runComposition, extensionLease);

var modeInput = await ModeInputPreparation.PrepareAsync(
    projectBinding.Arguments, modelBinding.Selection, standardInput);

if (modeInput is null)
{
    return;
}

var print = modeInput.Print;
var stdinContent = modeInput.Stdin;
var prompt = projectBinding.Arguments.Prompt;

using var presentation = new TerminalPresentation(agentDirectory, invocationDirectory, print, terminalIO,
    projectBinding, modelBinding, conversationBinding, extensionLease, modelRuntime);

var execution = new TerminalConversationExecution(
    presentation, projectBinding, modelBinding, conversationBinding, print);

if (projectBinding.Arguments.Mode == "rpc")
{
    await RpcModeComposition.ServeAsync(agentDirectory, projectBinding, modelBinding, conversationBinding,
        modelRuntime, modelTransitions, projectTransitions, extensionLease, presentation);

    return;
}

if (projectBinding.Arguments.Mode == "json")
{
    await JsonPromptMode.RunAsync(
        conversationBinding.Conversation, conversationBinding.Run,
        projectBinding.Store, conversationBinding.Path,
        projectBinding.Resources, prompt, stdinContent, projectBinding.Arguments,
        projectBinding.WorkingDirectory, Console.Out, Console.Error);

    return;
}

if (print)
{
    await PrintPromptMode.RunAsync(
        projectBinding.Arguments, stdinContent, projectBinding.WorkingDirectory, execution);
}
else
{
    await InteractiveMode.RunAsync(
        agentDirectory, startup, projectBinding, modelBinding, conversationBinding,
        modelRuntime, modelRuntimeController, modelTransitions, projectTransitions,
        extensionLease, catalogHttp, presentation, execution);
}
