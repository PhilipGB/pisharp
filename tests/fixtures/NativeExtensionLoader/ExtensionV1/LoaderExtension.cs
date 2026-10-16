using PiSharp.Extensions.Abstractions;
using PiSharp.Test.NativePrivateDependency;

namespace PiSharp.Test.NativeLoaderExtension.V1;

public sealed class LoaderExtension : IPiSharpExtension
{
    public LoaderExtension()
    {
        var marker = Environment.GetEnvironmentVariable("PISHARP_NATIVE_LOADER_ACTIVATION_MARKER");
        if (!string.IsNullOrWhiteSpace(marker)) File.WriteAllText(marker, "activated");
    }

    public string PrivateDependencyVersion => DependencyIdentity.Value;

    public void Configure(IPiSharpExtensionBuilder builder)
    {
    }
}

public sealed class NonExtensionType
{
}

public sealed class ThrowingExtension : IPiSharpExtension
{
    public ThrowingExtension() => throw new InvalidOperationException("fixture activation failure");

    public void Configure(IPiSharpExtensionBuilder builder)
    {
    }
}
