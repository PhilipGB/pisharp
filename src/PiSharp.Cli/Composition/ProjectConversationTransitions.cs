using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Composition;

internal sealed class ProjectConversationTransitions(string agentDirectory, string? configuredSessionDirectory,
    ProjectTrust trustStore, ActiveProjectRuntime projectBinding, ActiveModelRuntime modelBinding,
    ActiveConversationRuntime conversationBinding, ProviderModelRuntime modelRuntime, ICodemodeModels codemodeModels,
    ConversationRunComposition runComposition, ExtensionLease extensionLease)
{
    private readonly ProjectResourceReload _resourceReload = new(agentDirectory, configuredSessionDirectory, trustStore, modelRuntime);
    public InteractiveSessionController Sessions { get; private set; } =
        CreateSessions(projectBinding, modelBinding, runComposition, conversationBinding);
    public ProjectSessionRuntimeFactory SessionFactory { get; private set; } = new(agentDirectory, projectBinding.Arguments,
        configuredSessionDirectory, trustStore, modelRuntime, codemodeModels,
        projectBinding.Configuration.HasUserInterface, projectBinding.Configuration.ExtensionUi);

    private static InteractiveSessionController CreateSessions(ActiveProjectRuntime project, ActiveModelRuntime model,
        ConversationRunComposition runs, ActiveConversationRuntime conversation) =>
        new(project.Store, project.Arguments.NoSession, async (branch, path, reason) =>
        {
            var nextRun = await runs.OpenAsync(model, model.Agent, branch, path);
            await ExtensionSessionLifecycleHost.NotifyShutdownAsync(conversation.Run, ToShutdownReason(reason));
            await ExtensionSessionLifecycleHost.NotifyStartAsync(nextRun, reason);
            return nextRun;
        });

    private void RebindSessionControllers(bool rebuildFactory = true)
    {
        Sessions = CreateSessions(projectBinding, modelBinding, runComposition, conversationBinding);
        if (rebuildFactory)
            SessionFactory = new ProjectSessionRuntimeFactory(agentDirectory, projectBinding.Arguments,
                configuredSessionDirectory, trustStore, modelRuntime, codemodeModels,
                projectBinding.Configuration.HasUserInterface, projectBinding.Configuration.ExtensionUi);
    }

    public async Task ReloadAsync(bool usesDefaultToolsSetting, Action<ProjectRuntimeContext> configureMcp,
        Action refreshProjectUi, Action loadSessionTranscript)
    {
        ProjectRuntimeContext? nextProject = null;
        var previousContextPolicy = modelBinding.ContextPolicy;
        try
        {
            var reload = await _resourceReload.LoadAsync(projectBinding.WorkingDirectory, () => projectBinding.Settings, () => projectBinding.Arguments, projectBinding.Trusted,
                usesDefaultToolsSetting, () => conversationBinding.Run.ActiveToolNames,
                projectBinding.Configuration.HasUserInterface, projectBinding.Configuration.ExtensionUi);
            using var nextConfiguration = reload.Configuration;
            var reloadPlan = reload.ToolPlan;
            nextProject = reload.Project;
            configureMcp(nextProject);
            modelRuntime.SetVirtualModelRegistry(nextProject.Extensions.Registration.VirtualModels);
            var nextController = new ModelRuntimeController(modelRuntime, () => nextConfiguration.Settings,
                Environment.GetEnvironmentVariable);
            var reloadAgentArguments = usesDefaultToolsSetting
                ? reloadPlan.Arguments with { Tools = null }
                : reloadPlan.Arguments;
            var nextAgent = nextProject.CreateAgent(modelBinding.Chat, modelBinding.Selection, modelBinding.Thinking, reloadAgentArguments, nextConfiguration.Settings,
                nextController.CreateVirtualModelRouter(modelBinding.Selection), codemodeModels);
            var nextContextPolicy = nextConfiguration.Settings.ResolveCompactionPolicy(modelBinding.Selection.Model.ContextLength, Environment.GetEnvironmentVariable,
                $"{modelBinding.Selection.Provider.Id}/{modelBinding.Selection.Model.Id}");
            var nextModelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? modelBinding.Selection.Model.Pricing;
            var nextRun = await runComposition.OpenAsync(modelBinding, nextAgent, conversationBinding.Conversation, conversationBinding.Path, targetProject: nextProject,
                activeToolNamesOverride: reloadPlan.ActiveToolNames);
            await ExtensionSessionLifecycleHost.NotifyShutdownAsync(conversationBinding.Run,
                PiSharpExtensionSessionShutdownReason.Reload);
            await ExtensionSessionLifecycleHost.NotifyStartAsync(nextRun, PiSharpExtensionSessionStartReason.Reload);
            await nextProject.DiscoverExtensionResourcesAsync(agentDirectory, reloadPlan.Arguments,
                ExtensionResourceDiscoveryReason.Reload);
            extensionLease.Replace(nextProject.TransferExtensions());
            projectBinding.Adopt(nextConfiguration, nextProject, reloadPlan.Arguments);
            modelBinding.SetContext(nextContextPolicy, nextModelPricing);
            RebindSessionControllers();
            refreshProjectUi();
            modelBinding.ReplaceAgent(nextAgent);
            conversationBinding.ReplaceRun(nextRun);
            loadSessionTranscript();
        }
        catch
        {
            modelBinding.RestoreContextPolicy(previousContextPolicy);
            nextProject?.Dispose();
            throw;
        }
    }
    public async Task AdoptAsync(ProjectSessionRuntime replacement, Action<ProjectRuntimeContext> configureMcp,
        Action refreshProjectUi, Action finishSessionUi,
        PiSharpExtensionSessionStartReason reason = PiSharpExtensionSessionStartReason.Resume)
    {
        var nextProject = replacement.Project;
        await ExtensionSessionLifecycleHost.NotifyShutdownAsync(conversationBinding.Run, ToShutdownReason(reason));
        await ExtensionSessionLifecycleHost.NotifyStartAsync(replacement.Run, reason);
        await nextProject.DiscoverExtensionResourcesAsync(agentDirectory, projectBinding.Arguments,
            ExtensionResourceDiscoveryReason.Startup);
        configureMcp(nextProject);
        extensionLease.Replace(nextProject.TransferExtensions());
        projectBinding.Adopt(nextProject.Configuration, nextProject);
        conversationBinding.Adopt(replacement.Conversation, replacement.Run, replacement.Path);
        modelBinding.AdoptProject(replacement);
        RebindSessionControllers(rebuildFactory: false);
        refreshProjectUi();
        finishSessionUi();
    }

    public async Task<bool> SwitchAsync(string sessionReference, Func<ProjectSessionRuntime, Task> adopt,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.GetFullPath(sessionReference, projectBinding.WorkingDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (conversationBinding.Path is not null && Path.GetFullPath(conversationBinding.Path).Equals(targetPath, comparison)) return false;
        if (conversationBinding.Path is not null) await projectBinding.Store.SaveAsync(conversationBinding.Conversation, conversationBinding.Path, cancellationToken);
        using var replacement = await SessionFactory.OpenAsync(targetPath, projectBinding.WorkingDirectory,
            modelBinding.Selection, modelBinding.Thinking, keepSourceSessionDirectory: true, cancellationToken: cancellationToken);
        await adopt(replacement);
        return false;
    }

    private static PiSharpExtensionSessionShutdownReason ToShutdownReason(PiSharpExtensionSessionStartReason reason) =>
        reason switch
        {
            PiSharpExtensionSessionStartReason.New => PiSharpExtensionSessionShutdownReason.New,
            PiSharpExtensionSessionStartReason.Fork => PiSharpExtensionSessionShutdownReason.Fork,
            _ => PiSharpExtensionSessionShutdownReason.Resume
        };

    public Task ValidateSwitchAsync(string sessionReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionFactory.ValidateExistingSwitchTarget(sessionReference, projectBinding.WorkingDirectory);
        return Task.CompletedTask;
    }

}
