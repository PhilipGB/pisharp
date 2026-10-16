using PiSharp.Extensions.Abstractions;
using PiSharp.Test.NativePrivateDependency;

namespace PiSharp.Test.NativeLoaderExtension.V2;

public sealed class LoaderExtension : IPiSharpExtension
{
    public string PrivateDependencyVersion => DependencyIdentity.Value;

    public void Configure(IPiSharpExtensionBuilder builder)
    {
    }
}
