using PiSharp.Cli.Composition;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal sealed record OpenedConversation(ActiveModelRuntime Model, ActiveConversationRuntime Conversation);

internal static class ConversationStartup
{
    public static async Task<OpenedConversation?> OpenAsync(ActiveProjectRuntime projectBinding,
        ActiveModelRuntime initialModel, ProviderModelRuntime modelRuntime, ModelRuntimeController modelRuntimeController,
        ICodemodeModels codemodeModels, ConversationRunComposition runComposition, string currentDirectory, string? explicitThinking)
    {
        var cli = projectBinding.Arguments;
        var projectRuntime = projectBinding.Runtime;
        var userSettings = projectBinding.Settings;
        var store = projectBinding.Store;
        var selection = initialModel.Selection;
        var thinking = initialModel.Thinking;
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
            using var initialNameChange = cli.SessionName is null ? null : conversation.BeginSessionNameChange(cli.SessionName);
            if (!cli.NoSession) sessionPath ??= store.NewPath(conversation);
            var initialPath = sessionPath;
            conversationRun = await runComposition.OpenAsync(modelBinding, agent, conversation, initialPath);
            if ((cli.SessionName is not null || cli.ForkSource is not null) && sessionPath is not null)
                await store.SaveAsync(conversation, sessionPath);
            initialNameChange?.Commit();
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
