using PiSharp.Core;
using PiSharp.Core.Settings;

namespace PiSharp.Cli;

/// <summary>
/// Line-oriented /settings command: status display and global-scope value updates,
/// the CLI equivalent of Pi's interactive settings menu.
/// </summary>
internal static class SettingsCommands
{
    private const string UnsetValue = "unset";

    /// <summary>Prints the settings file locations and the effective values for supported keys.</summary>
    public static void PrintStatus(SettingsManager settings)
    {
        // Invalid configured values surface their Pi-style error instead of crashing the listing.
        string Value(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (FormatException exception)
            {
                return $"invalid: {exception.Message}";
            }
        }

        var paths = settings.ScopePaths;
        Console.WriteLine("Settings:");
        Console.WriteLine($"  Global:  {paths[SettingsScope.Global]}");
        var projectLine = $"  Project: {paths[SettingsScope.Project]}";
        Console.WriteLine(projectLine + (settings.IsProjectTrusted ? " (trusted)" : " (untrusted - ignored)"));

        var (retryEnabled, maxRetries, baseDelay, maxDelay) = settings.GetRetrySettings();
        var lines = new (string Key, string Value)[]
        {
            ("theme", settings.GetThemeSetting() ?? "(none)"),
            ("defaultModel", settings.GetDefaultModel() ?? "(none)"),
            ("defaultProvider", settings.GetDefaultProvider() ?? "(none)"),
            ("sessionDir", settings.GetSessionDir() ?? "(default)"),
            ("steeringMode", settings.GetSteeringMode()),
            ("followUpMode", settings.GetFollowUpMode()),
            ("transport", settings.GetTransport()),
            ("defaultThinkingLevel", settings.GetDefaultThinkingLevel() ?? "(none)"),
            ("compaction.enabled", settings.GetCompactionEnabled() ? "true" : "false"),
            ("compaction.reserveTokens", Value(() => settings.GetCompactionReserveTokens().ToString())),
            ("compaction.keepRecentTokens", Value(() => settings.GetCompactionKeepRecentTokens().ToString())),
            ("retry.enabled", retryEnabled ? "true" : "false"),
            ("retry.maxRetries", maxRetries.ToString()),
            ("retry.baseDelayMs", baseDelay.ToString()),
            ("retry.maxAgentDelayMs", maxDelay.ToString()),
            ("defaultProjectTrust", settings.GetDefaultProjectTrust().ToString().ToLowerInvariant()),
            ("quietStartup", settings.GetQuietStartup() ? "true" : "false"),
            ("httpIdleTimeoutMs", Value(() =>
                settings.GetHttpIdleTimeoutMs() == 0 ? "disabled" : settings.GetHttpIdleTimeoutMs().ToString())),
        };
        foreach (var (key, value) in lines)
        {
            Console.WriteLine($"  {key,-28} {value}");
        }
        Console.WriteLine($"  {UnsetValue,-28} clears a value back to the Pi default");
    }

    /// <summary>
    /// Applies "/settings &lt;key&gt; &lt;value&gt;" to the global scope and saves.
    /// Returns the confirmation message. Throws FormatException for unknown keys or bad values.
    /// </summary>
    public static async Task<string> ApplyArgumentAsync(SettingsManager settings, string argument)
    {
        var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new FormatException("Usage: /settings <key> <value>   (or /settings for the status list)");
        }

        var (key, rawValue) = (parts[0].ToLowerInvariant(), parts[1]);
        var value = rawValue.Equals(UnsetValue, StringComparison.OrdinalIgnoreCase) ? null : rawValue;
        ApplyKey(settings, key, value);
        await settings.SaveAsync();
        return $"Set {key} {(value is null ? "to default (unset)" : $"= {value}")} in global settings.";
    }

    private static void ApplyKey(SettingsManager settings, string key, string? value)
    {
        switch (key)
        {
            case "theme":
                SetRequired(value, settings.SetTheme);
                break;
            case "defaultmodel":
                SetRequired(value, settings.SetDefaultModel);
                break;
            case "defaultprovider":
                SetRequired(value, settings.SetDefaultProvider);
                break;
            case "sessiondir":
                settings.SetSessionDir(value);
                break;
            case "steeringmode":
                settings.SetSteeringMode(value ?? "one-at-a-time");
                break;
            case "followupmode":
                settings.SetFollowUpMode(value ?? "one-at-a-time");
                break;
            case "transport":
                settings.SetTransport(value ?? "auto");
                break;
            case "defaultthinkinglevel":
                if (value is null)
                {
                    throw new FormatException("defaultThinkingLevel requires a level: " +
                        string.Join(", ", SettingsManager.ThinkingLevels));
                }
                settings.SetDefaultThinkingLevel(value);
                break;
            case "compaction.enabled":
                settings.SetCompactionEnabled(ParseBool(key, value));
                break;
            case "compaction.reservetokens":
                settings.SetCompactionReserveTokens(ParseLong(key, value));
                break;
            case "compaction.keeprecenttokens":
                settings.SetCompactionKeepRecentTokens(ParseLong(key, value));
                break;
            case "retry.enabled":
                settings.SetRetryEnabled(ParseBool(key, value));
                break;
            case "retry.maxretries":
                settings.SetRetryMaxRetries((int)ParseLong(key, value));
                break;
            case "retry.basedelayms":
                settings.SetRetryBaseDelayMs(ParseLong(key, value));
                break;
            case "defaultprojecttrust":
                settings.SetDefaultProjectTrust(ParseTrust(value ?? "ask"));
                break;
            case "quietstartup":
                settings.SetQuietStartup(ParseBool(key, value));
                break;
            default:
                throw new FormatException(
                    $"Unknown settings key '{key}'. Run /settings to list the supported keys.");
        }
    }

    private static void SetRequired(string? value, Action<string> setter)
    {
        if (value is null)
        {
            throw new FormatException("This key requires a value (no unset form).");
        }
        setter(value);
    }

    private static bool ParseBool(string key, string? value) =>
        value switch
        {
            "true" or "1" or "on" => true,
            "false" or "0" or "off" or null => false,
            _ => throw new FormatException($"Invalid {key} value '{value}'. Expected true or false."),
        };

    private static long ParseLong(string key, string? value)
    {
        if (value is null || !long.TryParse(value, out var parsed))
        {
            throw new FormatException($"Invalid {key} value '{value}'. Expected a number.");
        }
        return parsed;
    }

    private static DefaultProjectTrust ParseTrust(string value) => value.ToLowerInvariant() switch
    {
        "ask" => DefaultProjectTrust.Ask,
        "always" => DefaultProjectTrust.Always,
        "never" => DefaultProjectTrust.Never,
        _ => throw new FormatException($"Invalid defaultProjectTrust value '{value}'. Expected ask, always, or never."),
    };
}
