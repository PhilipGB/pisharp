using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Tests.TestInfrastructure;

internal static class CompactionStreamingTestAdapter
{
    public static bool IsSummaryRequest(IEnumerable<ChatMessage> messages)
    {
        var text = string.Join('\n', messages.Select(message => message.Text));
        return text.Contains("structured context checkpoint summary", StringComparison.Ordinal) ||
            text.Contains("Update the existing structured summary", StringComparison.Ordinal) ||
            text.Contains("Create a concise checkpoint", StringComparison.Ordinal) ||
            text.Contains("Create a structured summary of this conversation branch", StringComparison.Ordinal);
    }

    public static async IAsyncEnumerable<ChatResponseUpdate> StreamResponseAsync(
        Func<CancellationToken, Task<ChatResponse>> getResponse,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await getResponse(cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }
}
