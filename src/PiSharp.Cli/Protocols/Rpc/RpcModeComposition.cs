using PiSharp.Cli.Composition;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal static class RpcModeComposition
{
    public static async Task ServeAsync(string agentDirectory, ActiveProjectRuntime projectBinding, ActiveModelRuntime modelBinding,
        ActiveConversationRuntime conversationBinding, ProviderModelRuntime modelRuntime, ConversationModelTransitions modelTransitions,
        ProjectConversationTransitions projectTransitions, ExtensionLease extensionLease, TerminalPresentation presentation)
    {
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

        void AdoptProjectSession(ProjectSessionRuntime replacement) => projectTransitions.Adopt(replacement,
            presentation.ConfigureMcpAuthorizationUrlPresenter, () =>
            {
                presentation.RefreshTerminalTheme();
                presentation.Screen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
            }, () =>
            {
                presentation.Screen?.SetFooter(presentation.IdleFooter());
                presentation.LoadSessionTranscript();
            });
        Task<bool> SwitchProjectSessionAsync(string reference, CancellationToken token) =>
            projectTransitions.SwitchAsync(reference, AdoptProjectSession, token);
        Task ValidateRpcSessionSwitchAsync(string reference, CancellationToken token) =>
            projectTransitions.ValidateSwitchAsync(reference, token);

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
    }
}
