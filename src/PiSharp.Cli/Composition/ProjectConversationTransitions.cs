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
        new(project.Store, project.Arguments.NoSession,
            (branch, path, _) => runs.OpenAsync(model, model.Agent, branch, path));

    public async Task AdoptSessionBranchAsync(SessionBranchResult branch,
        PiSharpExtensionSessionStartReason reason)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var outgoingRun = conversationBinding.Run;
        var shutdownError = await NotifyShutdownForCommitAsync(outgoingRun, ToShutdownReason(reason));
        conversationBinding.Adopt(branch.Conversation, branch.Run, branch.Path);
        await ExtensionSessionLifecycleHost.NotifyStartAsync(branch.Run, reason);
        ThrowTransitionErrors(shutdownError, null);
    }

    private void RebindSessionControllers(bool rebuildFactory = true)
    {
        Sessions = CreateSessions(projectBinding, modelBinding, runComposition, conversationBinding);
        if (rebuildFactory)
            SessionFactory = new ProjectSessionRuntimeFactory(agentDirectory, projectBinding.Arguments,
                configuredSessionDirectory, trustStore, modelRuntime, codemodeModels,
                projectBinding.Configuration.HasUserInterface, projectBinding.Configuration.ExtensionUi);
    }

    public async Task ReloadAsync(bool usesDefaultToolsSetting, Action<ProjectRuntimeContext> configureMcp,
        Action refreshProjectUi, Action loadSessionTranscript, Action? afterReplacementPrepared = null)
    {
        ProjectRuntimeContext? nextProject = null;
        var committed = false;
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
            modelRuntime.ValidateExtensionModelRegistrations(
                nextProject.Extensions.Registration.ExtensionProviders,
                nextProject.Extensions.Registration.VirtualModels);
            var nextController = new ModelRuntimeController(modelRuntime, () => nextConfiguration.Settings,
                Environment.GetEnvironmentVariable);
            var reloadAgentArguments = usesDefaultToolsSetting
                ? reloadPlan.Arguments with { Tools = null }
                : reloadPlan.Arguments;
            var nextAgent = nextProject.CreateAgent(modelBinding.Chat, modelBinding.Selection, modelBinding.Thinking, reloadAgentArguments, nextConfiguration.Settings,
                nextController.CreateVirtualModelRouter(modelBinding.Selection,
                    nextProject.Extensions.Registration.VirtualModels), codemodeModels);
            var nextContextPolicy = nextConfiguration.Settings.ResolveCompactionPolicy(modelBinding.Selection.Model.ContextLength, Environment.GetEnvironmentVariable,
                $"{modelBinding.Selection.Provider.Id}/{modelBinding.Selection.Model.Id}");
            var nextModelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? modelBinding.Selection.Model.Pricing;
            var nextRun = await runComposition.OpenAsync(modelBinding, nextAgent, conversationBinding.Conversation, conversationBinding.Path, targetProject: nextProject,
                activeToolNamesOverride: reloadPlan.ActiveToolNames);
            await nextProject.DiscoverExtensionResourcesAsync(agentDirectory, reloadPlan.Arguments,
                ExtensionResourceDiscoveryReason.Reload);
            afterReplacementPrepared?.Invoke();

            var shutdownError = await NotifyShutdownForCommitAsync(conversationBinding.Run,
                PiSharpExtensionSessionShutdownReason.Reload);
            committed = true;
            modelRuntime.SetExtensionProviders(nextProject.Extensions.Registration.ExtensionProviders);
            modelRuntime.SetVirtualModelRegistry(nextProject.Extensions.Registration.VirtualModels);
            modelRuntime.SetScope(reloadPlan.Arguments.ScopedModels);
            var nextExtensions = nextProject.TransferExtensions();
            projectBinding.Adopt(nextConfiguration, nextProject, reloadPlan.Arguments);
            modelBinding.SetContext(nextContextPolicy, nextModelPricing);
            RebindSessionControllers();
            modelBinding.ReplaceAgent(nextAgent);
            conversationBinding.ReplaceRun(nextRun);

            Exception? leaseReplacementError = null;
            try { extensionLease.Replace(nextExtensions); }
            catch (Exception error) { leaseReplacementError = error; }
            await ExtensionSessionLifecycleHost.NotifyStartAsync(nextRun, PiSharpExtensionSessionStartReason.Reload);
            refreshProjectUi();
            loadSessionTranscript();
            ThrowTransitionErrors(shutdownError, leaseReplacementError);
        }
        catch
        {
            if (!committed)
            {
                modelBinding.RestoreContextPolicy(previousContextPolicy);
                nextProject?.Dispose();
            }
            throw;
        }
    }
    public async Task AdoptAsync(ProjectSessionRuntime replacement, Action<ProjectRuntimeContext> configureMcp,
        Action refreshProjectUi, Action finishSessionUi,
        PiSharpExtensionSessionStartReason reason = PiSharpExtensionSessionStartReason.Resume,
        Action? afterReplacementPrepared = null)
    {
        var nextProject = replacement.Project;
        await nextProject.DiscoverExtensionResourcesAsync(agentDirectory, projectBinding.Arguments,
            ExtensionResourceDiscoveryReason.Startup);
        configureMcp(nextProject);
        modelRuntime.ValidateExtensionModelRegistrations(
            nextProject.Extensions.Registration.ExtensionProviders,
            nextProject.Extensions.Registration.VirtualModels);
        afterReplacementPrepared?.Invoke();

        var shutdownError = await NotifyShutdownForCommitAsync(conversationBinding.Run, ToShutdownReason(reason));
        modelRuntime.SetExtensionProviders(nextProject.Extensions.Registration.ExtensionProviders);
        modelRuntime.SetVirtualModelRegistry(nextProject.Extensions.Registration.VirtualModels);
        if (!projectBinding.Arguments.HasExplicitModelScope)
            modelRuntime.SetScope(nextProject.Configuration.Settings.EnabledModels);
        var nextExtensions = nextProject.TransferExtensions();
        replacement.CommitAdoption();
        projectBinding.Adopt(nextProject.Configuration, nextProject);
        conversationBinding.Adopt(replacement.Conversation, replacement.Run, replacement.Path);
        modelBinding.AdoptProject(replacement);
        RebindSessionControllers(rebuildFactory: false);
        Exception? leaseReplacementError = null;
        try { extensionLease.Replace(nextExtensions); }
        catch (Exception error) { leaseReplacementError = error; }
        await ExtensionSessionLifecycleHost.NotifyStartAsync(replacement.Run, reason);
        refreshProjectUi();
        finishSessionUi();
        ThrowTransitionErrors(shutdownError, leaseReplacementError);
    }

    public async Task<bool> SwitchAsync(string sessionReference, Func<ProjectSessionRuntime, Task> adopt,
        CancellationToken cancellationToken, SessionListing? sessionListing = null)
    {
        var targetPath = Path.GetFullPath(sessionReference, projectBinding.WorkingDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (conversationBinding.Path is not null && Path.GetFullPath(conversationBinding.Path).Equals(targetPath, comparison)) return false;
        if (conversationBinding.Path is not null) await projectBinding.Store.SaveAsync(conversationBinding.Conversation, conversationBinding.Path, cancellationToken);
        using var replacement = await SessionFactory.OpenAsync(targetPath, projectBinding.WorkingDirectory,
            modelBinding.Selection, modelBinding.Thinking, keepSourceSessionDirectory: true, cancellationToken: cancellationToken,
            sessionListing: sessionListing);
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

    private static async Task<Exception?> NotifyShutdownForCommitAsync(ConversationRun run,
        PiSharpExtensionSessionShutdownReason reason)
    {
        try
        {
            await ExtensionSessionLifecycleHost.NotifyShutdownAsync(run, reason);
            return null;
        }
        catch (Exception error)
        {
            // The run records the notification before dispatch, so even a host-side dispatch failure
            // must not leave it active after the replacement transition.
            return error;
        }
    }

    private static void ThrowTransitionErrors(Exception? shutdownError, Exception? leaseReplacementError)
    {
        if (shutdownError is null && leaseReplacementError is null) return;
        if (shutdownError is not null && leaseReplacementError is not null)
            throw new AggregateException("Session replacement committed with lifecycle errors.",
                shutdownError, leaseReplacementError);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(shutdownError ?? leaseReplacementError!)
            .Throw();
    }

    public Task ValidateSwitchAsync(string sessionReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionFactory.ValidateExistingSwitchTarget(sessionReference, projectBinding.WorkingDirectory);
        return Task.CompletedTask;
    }

    public async Task BeforeSessionSwitchAsync(PiSharpExtensionSessionSwitchReason reason,
        string? targetSessionFile, CancellationToken cancellationToken)
    {
        string? normalizedTarget = null;
        if (reason == PiSharpExtensionSessionSwitchReason.Resume)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetSessionFile);
            normalizedTarget = Path.GetFullPath(targetSessionFile, projectBinding.WorkingDirectory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (conversationBinding.Path is not null && Path.GetFullPath(conversationBinding.Path).Equals(normalizedTarget, comparison))
                return;

            await ValidateSwitchAsync(normalizedTarget, cancellationToken);
        }

        var result = await conversationBinding.Run.DispatchSessionBeforeSwitchAsync(reason, normalizedTarget,
            conversationBinding.Path, cancellationToken);
        if (result?.Cancel == true)
            throw new InvalidOperationException("Session transition was cancelled by an extension.");
    }

    public async Task BeforeSessionForkAsync(string entryId, PiSharpExtensionForkPosition position,
        CancellationToken cancellationToken)
    {
        var result = await conversationBinding.Run.DispatchSessionBeforeForkAsync(entryId, position,
            conversationBinding.Path, cancellationToken);
        if (result?.Cancel == true)
            throw new InvalidOperationException("Session fork was cancelled by an extension.");
    }

}
