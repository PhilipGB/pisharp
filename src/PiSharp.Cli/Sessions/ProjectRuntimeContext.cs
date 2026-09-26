using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
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
            additionalSkills: arguments.SkillPaths, additionalPrompts: arguments.PromptTemplatePaths);
        instructions += "\n" + resources.SystemInstructions();

        var sessionDirectory = sessionDirectoryOverride ?? arguments.SessionDirectory ?? configuredSessionDirectory ??
            configuration.Settings.SessionDirectory;
        if (sessionDirectory is not null) sessionDirectory = Path.GetFullPath(sessionDirectory, cwd);
        var store = new ConversationStore(cwd, sessionDirectory);
        var sessionImport = new PiSessionImportService(store, cwd, arguments.NoSession);
        ExtensionCatalog? extensions = null;
        try
        {
            extensions = ExtensionCatalog.Load(agentDirectory, cwd, configuration.Trusted, discover: !arguments.NoExtensions,
                additionalPaths: arguments.ExtensionPaths);
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
                selection.Model.InputLimits?.Images?.Resize), arguments.Tools, arguments.ExcludeTools,
            arguments.NoTools, Instructions, Prompts.System, Prompts.Append, Extensions.Registration.Tools,
            reasoning: ThinkingLevels.ToOptions(thinking), blockImages: effectiveSettings.BlockImages == true,
            noBuiltinTools: arguments.NoBuiltinTools,
            supportsImages: selection.Model.Input?.Contains("image", StringComparer.Ordinal) != false);
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
