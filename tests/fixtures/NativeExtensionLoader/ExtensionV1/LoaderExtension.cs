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
        builder.AddTurnEndBoundaryHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("native-loader-v1-turn")], @continue: true)));
        builder.AddAgentBeforeSettleHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("native-loader-v1-before-settle")], @continue: true)));
        builder.AddAgentSettledHandler(async (_, context, cancellationToken) =>
        {
            using var data = System.Text.Json.JsonDocument.Parse("{\"fixture\":\"v1\"}");
            await context.Session!.AppendEntryAsync("native-loader-v1-settled", data.RootElement, cancellationToken);
        });
    }
}

public sealed class ThrowingBoundaryExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddTurnEndBoundaryHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("must-rollback-turn")])));
        builder.AddAgentBeforeSettleHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("must-rollback-before-settle")])));
        builder.AddAgentSettledHandler((_, _, _) => ValueTask.CompletedTask);
        throw new InvalidOperationException("fixture boundary configure failure");
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
