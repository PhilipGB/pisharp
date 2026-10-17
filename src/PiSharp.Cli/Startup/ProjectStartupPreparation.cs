using PiSharp.Cli.Sessions;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Startup;

internal sealed record PreparedProjectStartup(string InvocationDirectory, string? SessionDirectory,
    ProjectTrust Trust, ProjectRuntimeConfiguration Configuration, CliArguments Arguments,
    string? ExplicitThinking, bool UsesDefaultToolsSetting);

internal static class ProjectStartupPreparation
{
    public static async Task<PreparedProjectStartup?> PrepareAsync(CliArguments cli, string agentDirectory, TextReader standardInput)
    {
        var invocationDirectory = Environment.CurrentDirectory;
        string? configuredSessionDirectory;
        try
        {
            configuredSessionDirectory = Environment.GetEnvironmentVariable("PISHARP_SESSION_DIR");
            if (configuredSessionDirectory is not null)
                configuredSessionDirectory = Path.GetFullPath(configuredSessionDirectory, invocationDirectory);
            cli = ExtensionCliPaths.Resolve(cli, invocationDirectory);
            var startupTarget = PiSessionStartupTarget.Resolve(invocationDirectory, cli);
            cli = startupTarget.Arguments;
            Environment.CurrentDirectory = startupTarget.WorkingDirectory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine(cli.PendingUnknownOption ?? $"Could not inspect session startup target: {error.Message}");
            Environment.ExitCode = 2;
            return null;
        }
        var currentDirectory = Environment.CurrentDirectory;
        var trustStore = new ProjectTrust(agentDirectory);
        ProjectRuntimeConfiguration projectConfiguration;
        var hasProjectTrustUi = cli.Mode == "interactive" && !cli.Print &&
            !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var projectTrustMode = cli.Print ? "print" : cli.Mode;
        var projectTrustUi = new ProjectTrustStartupUi(hasProjectTrustUi, projectTrustMode, Console.Out, Console.Error);
        try
        {
            projectConfiguration = await ProjectRuntimeConfiguration.LoadAsync(currentDirectory, agentDirectory, cli,
                trustStore, hasProjectTrustUi, standardInput, Console.Error, extensionUi: projectTrustUi);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            Console.Error.WriteLine(cli.PendingUnknownOption ?? error.Message);
            Environment.ExitCode = 2;
            return null;
        }
        try
        {
            var userSettings = projectConfiguration.Settings;
            var usesDefaultToolsSetting = DefaultToolReloadPolicy.UsesSettingsDefaults(cli);
            var explicitThinking = cli.Thinking;
            cli = DefaultToolReloadPolicy.ApplyStartupDefaults(cli, userSettings, Environment.GetEnvironmentVariable,
                preserveSessionModel: cli.Continue || cli.SessionPath is not null || cli.ForkSource is not null || cli.ListModels);
            return new(invocationDirectory, configuredSessionDirectory, trustStore, projectConfiguration, cli, explicitThinking, usesDefaultToolsSetting);
        }
        catch
        {
            projectConfiguration.Dispose();
            throw;
        }
    }
}
