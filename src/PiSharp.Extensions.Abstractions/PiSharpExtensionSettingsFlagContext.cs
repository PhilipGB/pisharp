using System.Text.Json;

namespace PiSharp.Extensions.Abstractions;

/// <summary>Read-only, call-time settings and flag access supplied by the host.</summary>
public sealed class PiSharpExtensionSettingsFlagContext
{
    private readonly Func<JsonElement> _getSettings;
    private readonly Func<string, PiSharpExtensionFlagValue?> _getFlag;

    /// <summary>Creates the host bridge used by a live extension command context.</summary>
    public PiSharpExtensionSettingsFlagContext(Func<JsonElement> getSettings,
        Func<string, PiSharpExtensionFlagValue?> getFlag)
    {
        ArgumentNullException.ThrowIfNull(getSettings);
        ArgumentNullException.ThrowIfNull(getFlag);
        _getSettings = getSettings;
        _getFlag = getFlag;
    }

    /// <summary>Returns a fresh detached JSON snapshot of the host's current effective settings.</summary>
    public JsonElement GetSettings() => _getSettings().Clone();

    /// <summary>Gets this extension's registered flag value, or null when it has no value.</summary>
    public PiSharpExtensionFlagValue? GetFlag(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _getFlag(name);
    }
}

/// <summary>Provides the settings and flag methods on native extension command contexts.</summary>
public sealed partial class PiSharpExtensionCommandContext
{
    /// <summary>Call-time, read-only access to effective settings and extension-owned flag values.</summary>
    public PiSharpExtensionSettingsFlagContext? SettingsAndFlags { get; init; }

    /// <summary>Returns a fresh, read-only JSON snapshot of the current effective settings.</summary>
    public JsonElement GetSettings() => (SettingsAndFlags ?? throw new InvalidOperationException(
        "The host did not provide settings access for this callback.")).GetSettings();

    /// <summary>Returns this extension's registered flag value, or null when unset and without a default.</summary>
    public PiSharpExtensionFlagValue? GetFlag(string name) => (SettingsAndFlags ?? throw new InvalidOperationException(
        "The host did not provide flag access for this callback.")).GetFlag(name);
}
