namespace PiSharp.Core;

using PiSharp.Core.Settings;

/// <summary>
/// Resolves Pi's canonical session storage layout: the agent directory
/// (<c>PI_CODING_AGENT_DIR</c> or <c>~/.pi/agent</c>) plus
/// <c>sessions/&lt;encoded-cwd&gt;/</c>, where the encoded cwd follows pinned
/// session-manager.ts <c>getDefaultSessionDirPath</c> exactly: one leading path
/// separator is stripped and every <c>/</c>, <c>\</c>, and <c>:</c> becomes <c>-</c>,
/// wrapped in <c>--…--</c>.
/// </summary>
public static class SessionDirectory
{
    /// <summary>
    /// Encodes a resolved working directory into Pi's session directory name. Pure string
    /// manipulation (no path API) so Windows-style paths are testable on every platform:
    /// <c>/home/user/proj</c> → <c>--home-user-proj--</c>, <c>C:\Users\me</c> →
    /// <c>--C--Users-me--</c>, <c>/</c> → <c>----</c>.
    /// </summary>
    public static string EncodeCwd(string resolvedCwd)
    {
        ArgumentNullException.ThrowIfNull(resolvedCwd);
        var normalized = NormalizeTrailingSeparators(resolvedCwd);
        var trimmed = normalized.Length > 0 && (normalized[0] == '/' || normalized[0] == '\\')
            ? normalized[1..]
            : normalized;
        var safe = new string(trimmed.Select(ch => ch is '/' or '\\' or ':' ? '-' : ch).ToArray());
        return $"--{safe}--";
    }

    /// <summary>
    /// Trims trailing path separators the way Node's <c>path.resolve</c> normalizes them,
    /// while preserving root paths (<c>/</c> and drive roots like <c>C:\</c>).
    /// </summary>
    private static string NormalizeTrailingSeparators(string path)
    {
        while (path.Length > 1 && (path[^1] == '/' || path[^1] == '\\'))
        {
            var trimmed = path[..^1];
            // Keep drive roots ("C:\" → "C:") and the filesystem root intact.
            if (trimmed.Length == 0 || (trimmed.Length >= 2 && trimmed[1] == ':'))
            {
                break;
            }
            path = trimmed;
        }
        return path;
    }

    /// <summary>
    /// Gets the canonical session directory for a working directory under the given agent
    /// directory (pinned <c>getDefaultSessionDirPath</c>): <c>{agentDir}/sessions/{encoded}</c>.
    /// The caller creates the directory when it first needs to write.
    /// </summary>
    public static string GetDefaultSessionDirPath(string cwd, string? agentDirectory = null, string? homeDirectory = null)
    {
        var resolvedCwd = Path.GetFullPath(cwd);
        var agentDir = Path.GetFullPath(agentDirectory ?? SettingsPaths.GetAgentDir(homeDirectory));
        return Path.Combine(agentDir, "sessions", EncodeCwd(resolvedCwd));
    }
}
