using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeApiV9;

public sealed class LegacyExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddCommand("api-v9-legacy", (arguments, _) => Task.FromResult("api-v9 command: " + arguments));
        builder.AddShortcut(PiSharpShortcutKey.Parse("ctrl+alt+9"), (_, _) => Task.CompletedTask);
        builder.AddSessionStartHandler((eventData, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = context.Session?.SessionId;
            return ValueTask.CompletedTask;
        });
    }
}
