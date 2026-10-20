using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeApiV10;

public sealed class ContextMutationExtension : IPiSharpExtension
{
#if CONTEXT_MUTATION_V2
    private const string Revision = "v2";
#else
    private const string Revision = "v1";
#endif

    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddContextHandler((eventData, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var user = eventData.Messages.LastOrDefault(message => message.Role == ChatRole.User);
            if (user?.Contents.OfType<TextContent>().LastOrDefault() is { } text)
                text.Text += $" [compiled context handler {Revision}]";
            return ValueTask.CompletedTask;
        });
        builder.AddContextWithSystemHandler((eventData, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            eventData.SystemInstructions = $"compiled context_with_system handler {Revision}";
            return ValueTask.CompletedTask;
        });
    }
}
