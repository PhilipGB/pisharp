using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli;

internal sealed class ExtensionProviderHandle(
    NativeExtensions.PiSharpExtensionProviderDefinition definition,
    string extensionPath)
{
    private readonly object _gate = new();
    private readonly List<WeakReference<ExtensionProviderChatClient>> _clients = [];
    private NativeExtensions.PiSharpExtensionProviderDefinition? _definition = definition;
    private bool _deactivated;

    internal string Id { get; } = definition.Id;
    internal string ExtensionPath { get; } = extensionPath;
    internal NativeExtensions.PiSharpExtensionProviderDefinition? Definition => Volatile.Read(ref _definition);

    internal IChatClient TrackClient(IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var tracked = new ExtensionProviderChatClient(client, Id, ExtensionPath);
        bool deactivate;
        lock (_gate)
        {
            deactivate = _deactivated;
            if (!deactivate) _clients.Add(new(tracked));
        }
        if (deactivate) tracked.Deactivate();
        return tracked;
    }

    internal void Deactivate()
    {
        WeakReference<ExtensionProviderChatClient>[] clients;
        lock (_gate)
        {
            if (_deactivated) return;
            _deactivated = true;
            Interlocked.Exchange(ref _definition, null);
            clients = _clients.ToArray();
            _clients.Clear();
        }
        foreach (var client in clients)
            if (client.TryGetTarget(out var target)) target.Deactivate();
    }
}

internal sealed class ExtensionProviderChatClient(IChatClient inner, string providerId, string extensionPath) : IChatClient
{
    private readonly object _gate = new();
    private IChatClient? _inner = inner;
    private int _activeRequests;
    private bool _deactivated;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var client = Acquire();
        try { return await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false); }
        finally { Release(); }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = Acquire();
        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return update;
        }
        finally { Release(); }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        var client = Acquire();
        try { return client.GetService(serviceType, serviceKey); }
        finally { Release(); }
    }

    public void Dispose() => Deactivate();

    internal void Deactivate()
    {
        IChatClient? dispose = null;
        lock (_gate)
        {
            if (_deactivated) return;
            _deactivated = true;
            if (_activeRequests == 0)
            {
                dispose = _inner;
                _inner = null;
            }
        }
        dispose?.Dispose();
    }

    private IChatClient Acquire()
    {
        lock (_gate)
        {
            if (_deactivated || _inner is null)
                throw new InvalidOperationException($"Extension provider '{providerId}' from '{extensionPath}' was removed or replaced. Select an available model before the next request.");
            _activeRequests++;
            return _inner;
        }
    }

    private void Release()
    {
        IChatClient? dispose = null;
        lock (_gate)
        {
            _activeRequests--;
            if (_deactivated && _activeRequests == 0)
            {
                dispose = _inner;
                _inner = null;
            }
        }
        dispose?.Dispose();
    }
}
