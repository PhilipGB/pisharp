using PiSharp.Extensions.Abstractions;
using PiSharp.Test.NativePrivateDependency;

namespace PiSharp.Test.NativeLoaderExtension.V2;

public sealed class LoaderExtension : IPiSharpExtension
{
    public string PrivateDependencyVersion => DependencyIdentity.Value;

    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddTurnEndBoundaryHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("native-loader-v2-turn")], @continue: false)));
        builder.AddAgentBeforeSettleHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("native-loader-v2-before-settle")], @continue: false)));
        builder.AddAgentSettledHandler(async (_, context, cancellationToken) =>
        {
            using var data = System.Text.Json.JsonDocument.Parse("{\"fixture\":\"v2\"}");
            await context.Session!.AppendEntryAsync("native-loader-v2-settled", data.RootElement, cancellationToken);
        });
    }
}
