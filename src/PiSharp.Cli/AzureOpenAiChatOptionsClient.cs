using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace PiSharp.Cli;

internal sealed record AzureOpenAiRequestMetadata(string? PromptCacheKey);

internal sealed class AzureOpenAiRequestContext
{
    private sealed record SessionPromptCacheKey(string Value);

    private static readonly ConditionalWeakTable<AgentSession, SessionPromptCacheKey> SessionPromptCacheKeys = new();
    private readonly AsyncLocal<AzureOpenAiRequestMetadata?> _current = new();

    public AzureOpenAiRequestMetadata? Current => _current.Value;

    public static string? GetCurrentSessionPromptCacheKey()
    {
        AgentRunContext? runContext;
        try { runContext = AIAgent.CurrentRunContext; }
        catch (InvalidOperationException) { return null; }
        return runContext?.Session is { } session
            ? SessionPromptCacheKeys.GetValue(session, static _ => new(Guid.NewGuid().ToString("N"))).Value
            : null;
    }

    public IDisposable Push(AzureOpenAiRequestMetadata metadata)
    {
        var previous = _current.Value;
        _current.Value = metadata;
        return new Scope(this, previous);
    }

    private sealed class Scope(AzureOpenAiRequestContext owner, AzureOpenAiRequestMetadata? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner._current.Value = previous;
        }
    }
}

/// <summary>Passes the session cache key to the Azure-specific HTTP adapter without changing canonical history.</summary>
internal sealed class AzureOpenAiChatOptionsClient(IChatClient inner, AzureOpenAiRequestContext requestContext)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var scope = requestContext.Push(new(options?.ConversationId ?? AzureOpenAiRequestContext.GetCurrentSessionPromptCacheKey()));
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var scope = requestContext.Push(new(options?.ConversationId ?? AzureOpenAiRequestContext.GetCurrentSessionPromptCacheKey()));
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return update;
    }
}
