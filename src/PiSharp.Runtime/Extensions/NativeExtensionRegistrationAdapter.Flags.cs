using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void RegisterFlag(string name, NativeExtensions.PiSharpExtensionFlagType type,
        string? description = null, NativeExtensions.PiSharpExtensionFlagValue? defaultValue = null)
    {
        EnsureConfiguring();
        registration.AddFlag(name, type, description, defaultValue);
    }
}
