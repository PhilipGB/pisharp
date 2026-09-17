namespace PiSharp.Core.Settings;

/// <summary>
/// Resolves the Pi settings locations: the global agent directory (PI_CODING_AGENT_DIR or
/// ~/.pi/agent, matching Pi's ENV_AGENT_DIR) and the project settings path.
/// </summary>
public static class SettingsPaths
{
    /// <summary>Environment variable that overrides the global agent directory (Pi: PI_CODING_AGENT_DIR).</summary>
    public const string AgentDirEnvironmentVariable = "PI_CODING_AGENT_DIR";

    /// <summary>Environment variable that overrides the session storage directory (Pi: PI_CODING_AGENT_SESSION_DIR).</summary>
    public const string SessionDirEnvironmentVariable = "PI_CODING_AGENT_SESSION_DIR";

    /// <summary>Gets the global agent directory (PI_CODING_AGENT_DIR or ~/.pi/agent).</summary>
    public static string GetAgentDir(string? homeDirectory = null)
    {
        var environmentDir = Environment.GetEnvironmentVariable(AgentDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentDir))
        {
            return Path.GetFullPath(ExpandTilde(environmentDir));
        }

        return Path.Combine(ProjectTrustPath.GetHomeDirectory(homeDirectory), ".pi", "agent");
    }

    /// <summary>Gets the global settings file path.</summary>
    public static string GetGlobalSettingsPath(string? homeDirectory = null) =>
        Path.Combine(GetAgentDir(homeDirectory), "settings.json");

    /// <summary>Gets the project settings path for a working directory.</summary>
    public static string GetProjectSettingsPath(string cwd) =>
        Path.Combine(Path.GetFullPath(cwd), ".pi", "settings.json");

    /// <summary>Gets the keybindings file path under the agent directory.</summary>
    public static string GetKeybindingsPath(string? homeDirectory = null) =>
        Path.Combine(GetAgentDir(homeDirectory), "keybindings.json");

    /// <summary>Resolves a configured session directory override (CLI, then env, then settings).</summary>
    public static string? ResolveSessionDir(string? cliOverride, string? settingsValue, string? homeDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride))
        {
            return Path.GetFullPath(ExpandTilde(cliOverride));
        }

        var environmentValue = Environment.GetEnvironmentVariable(SessionDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return Path.GetFullPath(ExpandTilde(environmentValue));
        }

        if (!string.IsNullOrWhiteSpace(settingsValue))
        {
            return Path.GetFullPath(ExpandTilde(settingsValue));
        }

        return null;
    }

    /// <summary>Expands a leading ~ to the home directory (Pi's expandTildePath behaviour).</summary>
    public static string ExpandTilde(string path)
    {
        if (path is "~" or "~/")
        {
            return ProjectTrustPath.GetHomeDirectory();
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(ProjectTrustPath.GetHomeDirectory(), path[2..]);
        }

        return path;
    }
}
