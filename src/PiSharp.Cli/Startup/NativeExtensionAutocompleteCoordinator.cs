using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

/// <summary>Owns package-scoped autocomplete providers and isolates cancellation from provider cooperation.</summary>
internal sealed class NativeExtensionAutocompleteCoordinator
{
    private sealed class Entry(Guid ownerId, long order, long registrationOrder,
        NativeExtensions.PiSharpExtensionAutocompleteProvider provider) : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private int _active = 1;

        public Guid OwnerId { get; } = ownerId;
        public long Order { get; } = order;
        public long RegistrationOrder { get; } = registrationOrder;
        public NativeExtensions.PiSharpExtensionAutocompleteProvider Provider { get; } = provider;
        public CancellationToken LifetimeToken => _lifetime.Token;
        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            _lifetime.Cancel();
        }
    }

    private sealed class Registration(NativeExtensionAutocompleteCoordinator owner, Entry entry,
        Action<Exception> reportCancellationFailure) : IDisposable
    {
        private int _active = 1;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            owner.Remove(entry, reportCancellationFailure);
        }
    }

    private readonly object _gate = new();
    private readonly List<Entry> _providers = [];
    private long _nextRegistrationOrder;

    public IDisposable Add(Guid ownerId, long order,
        NativeExtensions.PiSharpExtensionAutocompleteProvider provider,
        Action<Exception> reportCancellationFailure)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(reportCancellationFailure);
        var entry = new Entry(ownerId, order, Interlocked.Increment(ref _nextRegistrationOrder), provider);
        lock (_gate) _providers.Add(entry);
        return new Registration(this, entry, reportCancellationFailure);
    }

    public async Task<IReadOnlyList<NativeExtensions.PiSharpExtensionAutocompleteSuggestion>> GetSuggestionsAsync(
        string text, int cursor, CancellationToken cancellationToken,
        Action<Guid, Exception> reportFailure)
    {
        Entry[] providers;
        lock (_gate) providers = _providers.OrderBy(provider => provider.Order)
            .ThenBy(provider => provider.RegistrationOrder).ToArray();

        var start = cursor;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var query = text[start..cursor];
        var results = new List<NativeExtensions.PiSharpExtensionAutocompleteSuggestion>();
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provider.IsActive) continue;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                provider.LifetimeToken);
            if (linked.IsCancellationRequested) continue;
            try
            {
                var pending = provider.Provider(new(text, cursor, query, linked.Token), linked.Token).AsTask();
                ObserveFault(pending);
                var supplied = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                foreach (var suggestion in (supplied ?? []).Take(100))
                {
                    if (suggestion is null) continue;
                    var replacement = TerminalSafeText.Normalize(suggestion.ReplacementText ?? string.Empty);
                    var display = TerminalSafeText.Normalize(suggestion.DisplayText ?? string.Empty);
                    if (replacement.Length == 0 || display.Length == 0) continue;
                    var description = suggestion.Description is null ? null : TerminalSafeText.Normalize(
                        suggestion.Description[..Math.Min(suggestion.Description.Length, 1024)]);
                    results.Add(new(display[..Math.Min(display.Length, 512)],
                        replacement[..Math.Min(replacement.Length, 4096)], description));
                    if (results.Count == 100) return results;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!provider.IsActive) { }
            catch (Exception exception)
            {
                reportFailure(provider.OwnerId, exception);
            }
        }
        return results;
    }

    public void RemoveOwner(Guid ownerId, Action<Exception> reportCancellationFailure)
    {
        Entry[] removed;
        lock (_gate)
        {
            removed = _providers.Where(provider => provider.OwnerId == ownerId).ToArray();
            foreach (var provider in removed) _providers.Remove(provider);
        }
        foreach (var provider in removed) Dispose(provider, reportCancellationFailure);
    }

    private void Remove(Entry entry, Action<Exception> reportCancellationFailure)
    {
        lock (_gate) _providers.Remove(entry);
        Dispose(entry, reportCancellationFailure);
    }

    private static void Dispose(Entry entry, Action<Exception> reportCancellationFailure)
    {
        try { entry.Dispose(); }
        catch (Exception exception) { reportCancellationFailure(exception); }
    }

    private static void ObserveFault(Task task) => _ = task.ContinueWith(static completed =>
        _ = completed.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}
