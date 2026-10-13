using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Composition;

internal sealed class ActiveProjectRuntime
{
    public ProjectRuntimeContext Runtime { get; private set; }
    public ProjectRuntimeConfiguration Configuration { get; private set; }
    public CliArguments Arguments { get; private set; }
    public string WorkingDirectory => Runtime.WorkingDirectory;
    public bool Trusted { get; private set; }
    public UserSettings BaseUserSettings { get; private set; }
    public UserSettings? ProjectSettings { get; private set; }
    public UserSettings Settings { get; private set; }
    public ResourceCatalog Resources => Runtime.Resources;
    public ConversationStore Store => Runtime.Store;

    public ActiveProjectRuntime(ProjectRuntimeConfiguration configuration, ProjectRuntimeContext runtime,
        CliArguments arguments)
    {
        Configuration = configuration;
        Runtime = runtime;
        Arguments = arguments;
        Trusted = configuration.Trusted;
        BaseUserSettings = configuration.BaseUserSettings;
        ProjectSettings = configuration.ProjectSettings;
        Settings = configuration.Settings;
    }

    public void Adopt(ProjectRuntimeConfiguration configuration, ProjectRuntimeContext runtime,
        CliArguments? arguments = null)
    {
        Runtime = runtime;
        Configuration = configuration;
        Trusted = configuration.Trusted;
        SetSettings(configuration.BaseUserSettings, configuration.ProjectSettings, configuration.Settings);
        if (arguments is not null) Arguments = arguments;
    }

    public void SetSettings(UserSettings user, UserSettings? project, UserSettings effective)
    {
        BaseUserSettings = user;
        ProjectSettings = project;
        Settings = effective;
    }

    public void SetUserSettings(UserSettings user, UserSettings effective)
    {
        BaseUserSettings = user;
        Settings = effective;
    }

    public void PublishSettingsConfiguration() => Configuration = Configuration.WithSettings(BaseUserSettings, ProjectSettings, Settings);
    public void SetTrust(bool trusted) => Trusted = trusted;
}
