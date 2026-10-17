using PiSharp.Cli.Composition;
using PiSharp.Cli.Configuration;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Sessions;

internal static class PiSharpExtensionSettingsFlagContextFactory
{
    public static PiSharpExtensionSettingsFlagContext Create(ActiveProjectRuntime projectBinding,
        string ownerSourcePath)
    {
        ArgumentNullException.ThrowIfNull(projectBinding);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSourcePath);

        return new(
            () => NativeExtensionSettingsSnapshot.Create(projectBinding.Settings),
            name =>
            {
                var registration = projectBinding.Runtime.Extensions.Registration;
                var flag = registration.ExtensionFlags.FirstOrDefault(candidate => candidate.Name == name &&
                    string.Equals(candidate.SourceInfo.Path, ownerSourcePath, StringComparison.Ordinal));
                if (flag is null) return null;
                return projectBinding.Arguments.ExtensionFlagValues.TryGetValue(name, out var value)
                    ? value
                    : flag.DefaultValue;
            });
    }
}
