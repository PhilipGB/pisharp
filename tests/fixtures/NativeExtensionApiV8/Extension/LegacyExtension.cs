using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeApiV8;

public sealed class LegacyExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddCommand("api-v8-legacy", (arguments, _) => Task.FromResult("api-v8 command: " + arguments));
        builder.AddContextTransform((messages, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(messages);
        });
        builder.AddToolCallHook((context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(PiSharpToolCallDecision.Allow);
        });
    }
}
