using PiSharp.Extensions.Abstractions;

namespace PiSharp.Test.NativeIncompatibleContract;

public sealed class IncompatibleExtension : IPiSharpExtension
{
    public IncompatibleExtension()
    {
        var marker = Environment.GetEnvironmentVariable("PISHARP_NATIVE_LOADER_ACTIVATION_MARKER");
        if (!string.IsNullOrWhiteSpace(marker)) File.WriteAllText(marker, "activated");
    }

    public void Configure(object builder)
    {
    }
}
