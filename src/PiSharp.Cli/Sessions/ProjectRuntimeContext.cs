using Microsoft.Extensions.AI;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Cli.Sessions;

internal sealed record ProjectRuntimeConfiguration(string WorkingDirectory, bool Trusted,
    UserSettings BaseUserSettings, UserSettings? ProjectSettings, UserSettings Settings)
{
    public static async Task<ProjectRuntimeConfiguration> LoadAsync(string workingDirectory, string agentDirectory,
        CliArguments arguments, ProjectTrust trustStore, bool interactiveTrust, TextReader input, TextWriter output,
        bool? trustedOverride = null, CancellationToken cancellationToken = default)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        var baseSettings = await UserSettings.LoadAsync(agentDirectory, Environment.GetEnvironmentVariable, cancellationToken);
        baseSettings.ApplyHttpProxyEnvironment(Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);
        var trusted = trustedOverride ?? await trustStore.ResolveAsync(cwd, arguments.ProjectTrustOverride,
            interactiveTrust, input, output, cancellationToken,
            defaultProjectTrust: baseSettings.DefaultProjectTrust ?? "ask");
        var projectSettings = trusted ? await UserSettings.LoadProjectAsync(cwd, cancellationToken) : null;
        return new(cwd, trusted, baseSettings, projectSettings,
            baseSettings.Overlay(projectSettings ?? new UserSettings()));
    }
}

internal sealed class ProjectRuntimeContext : IDisposable
{
    private bool _extensionsTransferred;

    public (string? System, string? Append) Prompts { get; }
    public string Instructions { get; }
    public ResourceCatalog Resources { get; }
    public ExtensionCatalog Extensions { get; }
    public ConversationStore Store { get; }
    public PiSessionImportService SessionImport { get; }
    public ProjectRuntimeConfiguration Configuration { get; }

    public string WorkingDirectory => Configuration.WorkingDirectory;
    public bool Trusted => Configuration.Trusted;
    public UserSettings? ProjectSettings => Configuration.ProjectSettings;
    public UserSettings Settings => Configuration.Settings;

    private ProjectRuntimeContext(ProjectRuntimeConfiguration configuration,
        (string? System, string? Append) prompts, string instructions,
        ResourceCatalog resources, ExtensionCatalog extensions, ConversationStore store,
        PiSessionImportService sessionImport)
    {
        Configuration = configuration;
        Prompts = prompts;
        Instructions = instructions;
        Resources = resources;
        Extensions = extensions;
        Store = store;
        SessionImport = sessionImport;
    }

    public static async Task<ProjectRuntimeContext> LoadAsync(ProjectRuntimeConfiguration configuration,
        string agentDirectory, CliArguments arguments, string? configuredSessionDirectory,
        string? sessionDirectoryOverride = null,
        CancellationToken cancellationToken = default)
    {
        var cwd = configuration.WorkingDirectory;
        var prompts = await CliPromptOverrides.ResolveAsync(arguments,
            await ProjectPrompts.LoadAsync(cwd, agentDirectory, configuration.Trusted, cancellationToken), cwd);
        var instructions = arguments.NoContextFiles ? "" : await ContextInstructions.LoadAsync(cwd, agentDirectory, cancellationToken);
        var resources = await ResourceCatalog.LoadAsync(cwd, agentDirectory, configuration.Trusted, cancellationToken,
            discoverSkills: !arguments.NoSkills, discoverPrompts: !arguments.NoPromptTemplates,
            additionalSkills: arguments.SkillPaths, additionalPrompts: arguments.PromptTemplatePaths,
            userSkills: configuration.BaseUserSettings.Skills, projectSkills: configuration.ProjectSettings?.Skills,
            userPrompts: configuration.BaseUserSettings.Prompts, projectPrompts: configuration.ProjectSettings?.Prompts);
        instructions += "\n" + resources.SystemInstructions();

        var sessionDirectory = sessionDirectoryOverride ?? arguments.SessionDirectory ?? configuredSessionDirectory ??
            configuration.Settings.SessionDirectory;
        if (sessionDirectory is not null) sessionDirectory = Path.GetFullPath(sessionDirectory, cwd);
        var store = new ConversationStore(cwd, sessionDirectory);
        var sessionImport = new PiSessionImportService(store, cwd, arguments.NoSession);
        ExtensionCatalog? extensions = null;
        try
        {
            var mcpManager = new McpRuntimeManager();
            var userExtensionPaths = arguments.NoExtensions ? null : configuration.BaseUserSettings.Extensions;
            var projectExtensionPaths = arguments.NoExtensions ? null : configuration.ProjectSettings?.Extensions;
            extensions = ExtensionCatalog.Load(agentDirectory, cwd, configuration.Trusted, discover: !arguments.NoExtensions,
                additionalPaths: arguments.ExtensionPaths, userPaths: userExtensionPaths,
                projectPaths: projectExtensionPaths,
                builtins: [ToolSearchBuiltin.Definition, CodemodeBuiltin.Definition,
                    McpBuiltin.CreateDefinition(mcpManager)]);
            var mcp = await McpConfiguration.LoadAsync(agentDirectory, cwd, configuration.Trusted, cancellationToken);
            var mcpErrors = extensions.LoadedBuiltins.Contains("mcp")
                ? await McpRuntime.RegisterAsync(mcp, extensions, cwd, cancellationToken, mcpManager)
                : Array.Empty<string>();
            foreach (var error in mcpErrors) Console.Error.WriteLine(error);
            var connectedMcp = extensions.Registration.ToolDefinitions.Where(tool =>
                tool.Function.Name.StartsWith("mcp__", StringComparison.Ordinal)).ToArray();
            var mcpRegistrations = connectedMcp.Concat(extensions.Registration.ToolDefinitions.Where(tool =>
                tool.Function.Name is "list_mcp_resources" or "list_mcp_resource_templates" or "read_mcp_resource"))
                .ToArray();
            var projectPaths = configuration.Trusted ? projectExtensionPaths : null;
            var explicitBuiltins = (arguments.ExtensionPaths ?? []).ToHashSet(StringComparer.Ordinal);
            if (mcpRegistrations.Any(tool => tool.Exposure == ToolExposure.Deferred) &&
                (!arguments.NoExtensions || explicitBuiltins.Contains("builtin:tool-search")))
                extensions.EnableBuiltinIfAllowed(ToolSearchBuiltin.Definition,
                    userExtensionPaths, projectPaths);
            var codemodeDeferred = mcp.Servers.Any(server =>
                connectedMcp.Any(tool => tool.Function.Name.StartsWith("mcp__" + server.Name + "__", StringComparison.Ordinal)) &&
                (server.Exposure == McpToolExposure.CodemodeDeferred ||
                    server.ToolExposure.Values.Contains(McpToolExposure.CodemodeDeferred)));
            if (mcp.AutoEnableCodemode && (!arguments.NoExtensions || explicitBuiltins.Contains("builtin:codemode")) &&
                (mcpRegistrations.Any(tool => tool.Exposure == ToolExposure.CodeMode) || codemodeDeferred))
            {
                extensions.EnableBuiltinIfAllowed(CodemodeBuiltin.Definition,
                    userExtensionPaths, projectPaths);
                if (extensions.Registration.ToolSourceInfo.TryGetValue("codemode", out var codemodeSource) &&
                    codemodeSource.Path == "builtin:codemode")
                    extensions.Registration.SetToolDefaultActive("codemode", true);
            }
            return new ProjectRuntimeContext(configuration, prompts, instructions,
                resources, extensions, store, sessionImport);
        }
        catch
        {
            extensions?.Dispose();
            throw;
        }
    }

    public PiAgent CreateAgent(IChatClient chat, ModelSelection selection, string thinking,
        CliArguments arguments, UserSettings? settings = null)
    {
        var effectiveSettings = settings ?? Settings;
        return new PiAgent(chat, new CodingTools(WorkingDirectory, effectiveSettings.ShellPath,
                selection.Model.InputLimits?.Images?.Resize, effectiveSettings.ShellCommandPrefix), arguments.Tools, arguments.ExcludeTools,
            arguments.NoTools, Instructions, Prompts.System, Prompts.Append,
            reasoning: ThinkingLevels.ToOptions(thinking, selection.Model.ThinkingLevelMap), blockImages: effectiveSettings.BlockImages == true,
            extensionToolRegistrations: Extensions.Registration.ToolDefinitions,
            noBuiltinTools: arguments.NoBuiltinTools,
            supportsImages: selection.Model.Input?.Contains("image", StringComparer.Ordinal) != false,
            liveExtensionRegistration: Extensions.Registration,
            extensionToolCallHooks: Extensions.Registration.ToolCallHooks,
            extensionToolResultHooks: Extensions.Registration.ToolResultHooks,
            // ProviderChatClientFactory applies retry.provider.maxRetries inside the SDK adapter.
            // Avoid adding PiAgent's independent fallback retry loop on top of that configured count.
            retryPolicy: ProviderRetryPolicy.None);
    }

    public ExtensionCatalog TransferExtensions()
    {
        _extensionsTransferred = true;
        return Extensions;
    }

    public void Dispose()
    {
        if (!_extensionsTransferred) Extensions.Dispose();
    }
}
