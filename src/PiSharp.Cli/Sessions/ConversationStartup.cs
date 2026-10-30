using PiSharp.Cli.Composition;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Sessions;

internal sealed record OpenedConversation(ActiveModelRuntime Model, ActiveConversationRuntime Conversation);

internal static class ConversationStartup
{
    public static async Task<OpenedConversation?> OpenAsync(ActiveProjectRuntime projectBinding,
        ActiveModelRuntime initialModel, ProviderModelRuntime modelRuntime, ModelRuntimeController modelRuntimeController,
        ICodemodeModels codemodeModels, ConversationRunComposition runComposition, string agentDirectory,
        string currentDirectory, string? explicitThinking)
    {
        var cli = projectBinding.Arguments;
        var projectRuntime = projectBinding.Runtime;
        var userSettings = projectBinding.Settings;
        var store = projectBinding.Store;
        var selection = initialModel.Selection;
        var initialSelection = selection;
        var thinking = initialModel.Thinking;
        var initialThinking = thinking;
        var chat = initialModel.Chat;
        var agent = initialModel.Agent;
        var contextPolicy = initialModel.ContextPolicy;
        var modelPricing = initialModel.Pricing;
        ActiveModelRuntime modelBinding;
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
                _ = selection.Connection;
                thinking = ThinkingLevels.ValidateForModel(thinking, selection.Model.Reasoning, selection.Model.ThinkingLevelMap);
                chat = ProviderChatClientFactory.Create(selection, userSettings.Retry?.Provider, userSettings.HttpIdleTimeoutMs);
                contextPolicy = userSettings.ResolveCompactionPolicy(selection.Model.ContextLength, Environment.GetEnvironmentVariable, $"{selection.Provider.Id}/{selection.Model.Id}");
                modelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? selection.Model.Pricing;
                agent = projectRuntime.CreateAgent(chat, selection, thinking, cli, userSettings,
                    modelRuntimeController.CreateVirtualModelRouter(selection), codemodeModels);
            }
            modelBinding = new ActiveModelRuntime(selection, chat, agent, thinking, contextPolicy, modelPricing);
            var previousSessionName = conversation.Name;
            using var initialNameChange = cli.SessionName is null ? null : conversation.BeginSessionNameChange(cli.SessionName);
            if (!cli.NoSession) sessionPath ??= store.NewPath(conversation);
            var initialPath = sessionPath;
            if (string.Equals(cli.Mode, "rpc", StringComparison.Ordinal) &&
                startupConversation.StartReason == PiSharpExtensionSessionStartReason.Startup &&
                conversation.Tree.Entries.Count == 0)
            {
                conversation.SelectModel(selection.Connection.Model, selection.Connection.Endpoint?.ToString(),
                    selection.Provider.Id);
                conversation.AppendThinkingLevelChange(thinking);
            }
            conversationRun = await runComposition.OpenAsync(modelBinding, agent, conversation, initialPath);
            if (startupModel.RebuildRuntime)
                await PiSharpExtensionSelectionEvents.PublishRestoreAsync(agent, initialSelection, initialThinking,
                    selection, thinking, CancellationToken.None).ConfigureAwait(false);
            await ExtensionSessionLifecycleHost.NotifyStartAsync(conversationRun, startupConversation.StartReason);
            await projectRuntime.DiscoverExtensionResourcesAsync(agentDirectory, cli,
                ExtensionResourceDiscoveryReason.Startup);
            if ((cli.SessionName is not null || cli.ForkSource is not null) && sessionPath is not null)
                await store.SaveAsync(conversation, sessionPath);
            initialNameChange?.Commit();
            if (initialNameChange is not null)
                await conversationRun.NotifySessionInfoChangedIfChangedAsync(previousSessionName,
                    CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not open session: {e.Message}");
            Environment.ExitCode = 2;
            return null;
        }
        return new(modelBinding, new ActiveConversationRuntime(conversation, conversationRun, sessionPath));
    }
}
