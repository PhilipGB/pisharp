using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Sessions;

internal sealed record ReloadedProjectResources(ProjectRuntimeConfiguration Configuration,
    ProjectRuntimeContext Project, DefaultToolReloadPlan ToolPlan);

internal sealed class ProjectResourceReload(string agentDirectory, string? configuredSessionDirectory,
    ProjectTrust trustStore, ProviderModelRuntime models)
{
    public async Task<ReloadedProjectResources> LoadAsync(string workingDirectory, Func<UserSettings> getPreviousSettings,
        Func<CliArguments> getArguments, bool trusted, bool usesDefaultToolsSetting, Func<IEnumerable<string>> getActiveToolNames,
        bool hasUserInterface = false, IProjectTrustExtensionUi? extensionUi = null)
    {
        var nextConfiguration = await ProjectRuntimeConfiguration.LoadAsync(workingDirectory,
            agentDirectory, getArguments(), trustStore, interactiveTrust: false, Console.In, Console.Error,
            trustedOverride: trusted, extensionUi: extensionUi, hasUserInterface: hasUserInterface);
        try
        {
            var plan = DefaultToolReloadPolicy.Resolve(getArguments(), usesDefaultToolsSetting, getPreviousSettings(),
                nextConfiguration.Settings, getActiveToolNames(), Environment.GetEnvironmentVariable);
            var nextProject = await ProjectRuntimeContext.LoadAsync(nextConfiguration, agentDirectory, plan.Arguments,
                configuredSessionDirectory, providerTokenResolver: models.GetApiKeyForProviderAsync,
                resourceDiscoveryReason: ExtensionResourceDiscoveryReason.Reload, activeToolNames: plan.ActiveToolNames,
                deferExtensionResourceDiscovery: true);
            return new(nextConfiguration, nextProject, plan);
        }
        catch
        {
            nextConfiguration.Dispose();
            throw;
        }
    }
}
