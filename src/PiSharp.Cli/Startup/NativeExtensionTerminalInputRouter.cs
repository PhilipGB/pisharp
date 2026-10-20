using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

/// <summary>Applies package input listeners to decoded terminal events in registration order.</summary>
internal sealed class NativeExtensionTerminalInputRouter(TextWriter error)
{
    private readonly object _gate = new();
    private readonly List<InputListener> _listeners = [];
    private long _registrationOrder;

    internal IDisposable Add(Guid owner, long order, NativeExtensions.PiSharpExtensionTerminalInputHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var listener = new InputListener(owner, order, Interlocked.Increment(ref _registrationOrder), handler);
        lock (_gate) _listeners.Add(listener);
        return new Registration(this, listener);
    }

    internal TerminalInputInterception Intercept(TerminalInputEvent input, Func<TerminalInputEvent, bool> isReserved,
        out bool failed)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(isReserved);
        failed = false;
        if (input.IsControl || input.IsEndOfStream) return new(false, input);

        InputListener[] listeners;
        lock (_gate)
            listeners = _listeners.OrderBy(listener => listener.Order)
                .ThenBy(listener => listener.RegistrationOrder).ToArray();

        IReadOnlyList<TerminalInputEvent> current = [input];
        var replacementEventCount = 0;
        foreach (var listener in listeners)
        {
            var following = new List<TerminalInputEvent>(current.Count);
            foreach (var item in current)
            {
                if (isReserved(item) || item.IsControl || item.IsEndOfStream)
                {
                    following.Add(item);
                    continue;
                }

                try
                {
                    if (!listener.TryInvoke(NativeExtensionInteractiveUiHost.ToExtensionInput(item), out var result) ||
                        result is null)
                    {
                        following.Add(item);
                        continue;
                    }
                    if (result.Consumed) continue;
                    if (result.ReplacementText is { } replacement)
                    {
                        var parsed = TerminalInput.ParseExtensionReplacement(replacement);
                        if (replacementEventCount + parsed.Count > 4096)
                        {
                            following.Add(item);
                            continue;
                        }
                        following.AddRange(parsed);
                        replacementEventCount += parsed.Count;
                        continue;
                    }
                    following.Add(item);
                }
                catch (Exception exception)
                {
                    failed = true;
                    following.Add(item);
                    try
                    {
                        error.WriteLine($"Extension terminal input handler failed for {listener.OwnerId:N}: " +
                            TerminalSafeText.Normalize(exception.Message));
                    }
                    catch (Exception) { }
                }
            }
            current = following;
            if (current.Count == 0) return new(true, input);
        }
        return new(false, current[0], current.Skip(1).ToArray());
    }

    internal void RemoveOwner(Guid owner)
    {
        InputListener[] removed;
        lock (_gate)
        {
            removed = _listeners.Where(listener => listener.OwnerId == owner).ToArray();
            foreach (var listener in removed) listener.Deactivate();
            foreach (var listener in removed) _listeners.Remove(listener);
        }
        foreach (var listener in removed) listener.WaitForCallback();
    }

    private void Remove(InputListener listener)
    {
        listener.Deactivate();
        lock (_gate) _listeners.Remove(listener);
        listener.WaitForCallback();
    }

    private sealed class InputListener(Guid ownerId, long order, long registrationOrder,
        NativeExtensions.PiSharpExtensionTerminalInputHandler handler)
    {
        private readonly object _gate = new();
        private int _active = 1;

        internal Guid OwnerId { get; } = ownerId;
        internal long Order { get; } = order;
        internal long RegistrationOrder { get; } = registrationOrder;

        internal bool TryInvoke(NativeExtensions.PiSharpExtensionUiInputEvent input,
            out NativeExtensions.PiSharpExtensionTerminalInputResult? result)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _active) == 0)
                {
                    result = null;
                    return false;
                }
                result = handler(input);
                return true;
            }
        }

        internal void Deactivate() => Interlocked.Exchange(ref _active, 0);

        internal void WaitForCallback()
        {
            lock (_gate) { }
        }
    }

    private sealed class Registration(NativeExtensionTerminalInputRouter owner, InputListener listener) : IDisposable
    {
        private int _active = 1;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) != 0) owner.Remove(listener);
        }
    }
}
