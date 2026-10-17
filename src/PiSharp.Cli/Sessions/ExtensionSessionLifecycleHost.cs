using System.Text.Json;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal static class ExtensionSessionLifecycleHost
{
    public static async Task NotifyStartAsync(ConversationRun run, PiSharpExtensionSessionStartReason reason,
        CancellationToken cancellationToken = default)
    {
        Report(await run.NotifySessionStartAsync(reason, cancellationToken));
    }

    public static async Task NotifyShutdownAsync(ConversationRun run,
        PiSharpExtensionSessionShutdownReason reason, CancellationToken cancellationToken = default)
    {
        Report(await run.NotifySessionShutdownAsync(reason, cancellationToken));
    }

    private static void Report(IReadOnlyList<AgentLifecycleEvent> events)
    {
        foreach (var item in events)
        {
            var details = item.Details is null ? null : JsonSerializer.Serialize(item.Details);
            Console.Error.WriteLine(details is null
                ? $"Extension error ({item.Type}): {item.Error}"
                : $"Extension error ({item.Type}; {details}): {item.Error}");
        }
    }
}

internal sealed class ActiveExtensionSessionLifetime(Func<ConversationRun> getRun) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => new(
        ExtensionSessionLifecycleHost.NotifyShutdownAsync(getRun(), PiSharpExtensionSessionShutdownReason.Quit));
}
