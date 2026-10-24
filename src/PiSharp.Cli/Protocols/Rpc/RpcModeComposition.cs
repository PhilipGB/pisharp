using PiSharp.Cli.Composition;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Tui;
using PiSharp.Extensions.Abstractions;
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

        IReadOnlyList<PiSharpExtensionCommandInfo> GetExtensionCommands()
        {
            var commands = InteractiveCommandCatalog.BuiltIns.Select(command =>
                new PiSharpExtensionCommandInfo(command.Command, command.Description, "builtin")).ToList();
            commands.AddRange(extensionLease.Current.Registration.CommandInfo.Select(pair =>
                new PiSharpExtensionCommandInfo(pair.Key, pair.Value.Description, "extension", pair.Value.SourceInfo.Path)));
            if (projectBinding.Resources is { } resources)
            {
                commands.AddRange(resources.Prompts.Select(item =>
                    new PiSharpExtensionCommandInfo(item.Name, item.Description, "prompt", item.SourceInfo.Path)));
                commands.AddRange(resources.Skills.Select(item =>
                    new PiSharpExtensionCommandInfo("skill:" + item.Name, item.Description, "skill", item.SourceInfo.Path)));
            }
            return Array.AsReadOnly(commands.GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray());
        }

        var rpcUserSettings = new RpcUserSettingsController(agentDirectory, Environment.GetEnvironmentVariable,
            () => projectBinding.ProjectSettings,
            (globalSettings, effectiveSettings) =>
            {
                projectBinding.SetUserSettings(globalSettings, effectiveSettings);
            },
            () => conversationBinding.Run);

        Task AdoptProjectSession(ProjectSessionRuntime replacement) => projectTransitions.AdoptAsync(replacement,
            presentation.ConfigureMcpAuthorizationUrlPresenter, () =>
            {
                presentation.RefreshTerminalTheme();
                presentation.Screen?.SetMarkdownCodeBlockIndent(projectBinding.Settings.MarkdownCodeBlockIndent ?? UserSettings.DefaultMarkdownCodeBlockIndent);
            }, () =>
            {
                presentation.SetIdleFooter();
                presentation.LoadSessionTranscript();
            });
        Task<bool> SwitchProjectSessionAsync(string reference, CancellationToken token) =>
            projectTransitions.SwitchAsync(reference, AdoptProjectSession, token);
        Task ValidateRpcSessionSwitchAsync(string reference, CancellationToken token) =>
            projectTransitions.ValidateSwitchAsync(reference, token);

        async Task<bool> StartRpcSessionAsync(string? parentSession, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var created = await projectTransitions.Sessions.NewAsync(conversationBinding.Conversation, conversationBinding.Path, parentSession);
            await projectTransitions.AdoptSessionBranchAsync(created, PiSharpExtensionSessionStartReason.New);
            return false;
        }

        async Task<string?> ForkRpcSessionAsync(string entryId, ConversationSession branchSnapshot, string prompt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var forked = await projectTransitions.Sessions.ForkPreparedAsync(conversationBinding.Conversation, conversationBinding.Path, entryId, branchSnapshot, prompt);
            await projectTransitions.AdoptSessionBranchAsync(forked, PiSharpExtensionSessionStartReason.Fork);
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
            await projectTransitions.AdoptSessionBranchAsync(cloned, PiSharpExtensionSessionStartReason.Fork);
            return false;
        }

        var rpcMode = new RpcMode(Console.In, Console.Out, conversationBinding.Run,
            cancellationToken => conversationBinding.Run.PersistAsync(cancellationToken),
            projectBinding.Resources, GetRpcModelsAsync,
            extensionLease.Current.Registration, () => modelBinding.Selection.Authenticated ? null :
                $"Provider '{modelBinding.Selection.Provider.Id}' is not authenticated. Use /login {modelBinding.Selection.Provider.Id} or configure {modelBinding.Selection.Provider.ApiKeyEnvironment ?? "a credential"}.",
            modelTransitions.SelectRpcModelAsync, () => conversationBinding.Run, () => modelBinding.Thinking, () => modelRuntime.HasScope,
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
            getSystemMessage: () => RpcSystemMessageProjector.Project(modelBinding.Agent),
            getProjectTrusted: () => projectBinding.Trusted,
            extensionMode: projectBinding.Configuration.ExtensionMode,
            extensionHasUserInterface: projectBinding.Configuration.HasUserInterface,
            extensionUi: projectBinding.Configuration.ExtensionUi,
            createCommandControlOperations: (abort, cancel, shutdown) => PiSharpExtensionCommandControlFactory.Create(
                () => conversationBinding.Run,
                GetExtensionCommands,
                () => extensionLease.Current.Registration,
                modelRuntime,
                modelBinding,
                modelTransitions,
                abort,
                cancel,
                shutdown),
            createSettingsFlagContext: sourcePath =>
                PiSharpExtensionSettingsFlagContextFactory.Create(projectBinding, sourcePath),
            cycleModel: (model, token) => modelTransitions.SelectRpcModelAsync(model,
                PiSharpExtensionModelSelectionSource.Cycle, token),
            installExtensionCapabilitiesFactory: conversationBinding.SetExtensionCapabilitiesFactory,
            getSystemPrompt: () => modelBinding.Agent.SystemInstructions);
        rpcMode.AfterServeBeforeUiShutdown = token => ExtensionSessionLifecycleHost.NotifyShutdownAsync(
            conversationBinding.Run, PiSharpExtensionSessionShutdownReason.Quit, token);
        await rpcMode.ServeAsync();
    }
}
