using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcExtensionUiTransport(JsonLineWriter writer) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _eventWrites = new();
    private readonly object _dialogGate = new();
    private readonly object _eventGate = new();
    private long _nextWrite;
    private Exception? _eventWriteFailure;
    private int _disposed;
    private Task? _disposeTask;
    private bool _dialogsStopped;

    internal async Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
        PiSharpExtensionUiDialogOptions? dialogOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Any(option => option is null)) throw new ArgumentException("Selection options cannot contain null.", nameof(options));
        var response = await RequestAsync(id => CreateDialogRequest(id, "select", dialogOptions?.Timeout,
            ("title", title), ("options", options)), dialogOptions?.Timeout, cancellationToken).ConfigureAwait(false);
        return response is { ValueKind: JsonValueKind.Object } && !IsCancelled(response.Value) &&
            response.Value.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    internal async Task<bool> ConfirmAsync(string title, string message,
        PiSharpExtensionUiDialogOptions? dialogOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        var response = await RequestAsync(id => CreateDialogRequest(id, "confirm", dialogOptions?.Timeout,
            ("title", title), ("message", message)), dialogOptions?.Timeout, cancellationToken).ConfigureAwait(false);
        return response is { ValueKind: JsonValueKind.Object } && !IsCancelled(response.Value) &&
            response.Value.TryGetProperty("confirmed", out var confirmed) &&
            confirmed.ValueKind == JsonValueKind.True;
    }

    internal async Task<string?> InputAsync(string title, string? placeholder = null,
        PiSharpExtensionUiDialogOptions? dialogOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        var response = await RequestAsync(id => CreateDialogRequest(id, "input", dialogOptions?.Timeout,
            ("title", title), ("placeholder", placeholder)), dialogOptions?.Timeout, cancellationToken).ConfigureAwait(false);
        return response is { ValueKind: JsonValueKind.Object } && !IsCancelled(response.Value) &&
            response.Value.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    internal async Task<string?> EditAsync(string title, string? prefill = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        var response = await RequestAsync(id => CreateDialogRequest(id, "editor", null,
            ("title", title), ("prefill", prefill)), null, cancellationToken).ConfigureAwait(false);
        return response is { ValueKind: JsonValueKind.Object } && !IsCancelled(response.Value) &&
            response.Value.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    internal void Notify(string message, string type)
    {
        ArgumentNullException.ThrowIfNull(message);
        var notifyType = type is "warning" or "error" ? type : "info";
        EmitEvent(new
        {
            type = "extension_ui_request",
            id = NewId(),
            method = "notify",
            message,
            notifyType
        });
    }

    internal void SetStatus(string key, string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var request = CreateEvent("setStatus");
        request["statusKey"] = key;
        if (text is not null) request["statusText"] = text;
        EmitEvent(request);
    }

    internal void SetWidget(string key, IReadOnlyList<string>? lines,
        PiSharpExtensionWidgetPlacement placement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var widgetPlacement = placement switch
        {
            PiSharpExtensionWidgetPlacement.AboveEditor => "aboveEditor",
            PiSharpExtensionWidgetPlacement.BelowEditor => "belowEditor",
            _ => throw new ArgumentOutOfRangeException(nameof(placement))
        };
        var request = CreateEvent("setWidget");
        request["widgetKey"] = key;
        if (lines is not null)
        {
            request["widgetLines"] = lines;
            request["widgetPlacement"] = widgetPlacement;
        }
        EmitEvent(request);
    }

    internal void SetTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        EmitEvent(new { type = "extension_ui_request", id = NewId(), method = "setTitle", title });
    }

    internal void SetEditorText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EmitEvent(new { type = "extension_ui_request", id = NewId(), method = "set_editor_text", text });
    }

    internal void ReceiveResponse(JsonElement response)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !response.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String ||
            idElement.GetString() is not { Length: > 0 } id || !_pending.TryRemove(id, out var pending))
            return;
        pending.TrySetResult(response.Clone());
    }

    internal void StopDialogs()
    {
        lock (_dialogGate)
        {
            _dialogsStopped = true;
            foreach (var pair in _pending.ToArray())
            {
                if (_pending.TryRemove(pair.Key, out var pending))
                    pending.TrySetCanceled();
            }
        }
    }

    private async Task<JsonElement?> RequestAsync(Func<string, object> createRequest, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTimeout(timeout);
        if (timeout == TimeSpan.Zero) return null;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var id = NewId();
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_dialogGate)
        {
            if (_dialogsStopped) return null;
            if (!_pending.TryAdd(id, pending)) throw new InvalidOperationException("Could not register RPC UI request.");
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            _pending.TryRemove(id, out _);
            pending.TrySetCanceled();
            throw new ObjectDisposedException(nameof(RpcExtensionUiTransport));
        }
        try
        {
            Task? write = null;
            lock (_dialogGate)
            {
                if (_dialogsStopped)
                {
                    _pending.TryRemove(id, out _);
                    pending.TrySetResult(CreateCancelledResponse(id));
                }
                else write = writer.EmitAsync(createRequest(id), cancellationToken);
            }
            if (write is null) return await pending.Task.ConfigureAwait(false);
            await write.ConfigureAwait(false);
            if (timeout is null || timeout == Timeout.InfiniteTimeSpan)
                return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var boundedTimeout = BoundTimeout(timeout!.Value);
            return await pending.Task.WaitAsync(boundedTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
            pending.TrySetCanceled();
        }
    }

    private void EmitEvent(object request)
    {
        long key;
        TaskCompletionSource registeredWrite;
        lock (_eventGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            key = Interlocked.Increment(ref _nextWrite);
            registeredWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _eventWrites[key] = registeredWrite.Task;
        }

        _ = TrackEventWriteAsync(key, request, registeredWrite);
    }

    private async Task EmitEventAsync(object request)
    {
        try { await writer.EmitAsync(request).ConfigureAwait(false); }
        catch (Exception) when (Volatile.Read(ref _disposed) != 0) { }
    }

    private async Task TrackEventWriteAsync(long key, object request, TaskCompletionSource registeredWrite)
    {
        try
        {
            await EmitEventAsync(request).ConfigureAwait(false);
            registeredWrite.TrySetResult();
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref _eventWriteFailure, error.GetBaseException(), null);
            registeredWrite.TrySetException(error);
        }
        finally
        {
            _eventWrites.TryRemove(key, out _);
        }
    }

    internal async Task FlushAsync()
    {
        while (!_eventWrites.IsEmpty)
        {
            var writes = _eventWrites.Values.ToArray();
            if (writes.Length == 0) continue;
            await Task.WhenAll(writes).ConfigureAwait(false);
        }
        if (Volatile.Read(ref _eventWriteFailure) is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public ValueTask DisposeAsync()
    {
        lock (_eventGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            Volatile.Write(ref _disposed, 1);
            _disposeTask = DisposeCoreAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var pending)) pending.TrySetCanceled();
        }
        await FlushAsync().ConfigureAwait(false);
    }

    private static string NewId() => Guid.NewGuid().ToString("D");

    private static JsonElement CreateCancelledResponse(string id) =>
        JsonSerializer.SerializeToElement(new { id, cancelled = true });

    private static bool IsCancelled(JsonElement response) =>
        response.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True;

    private static Dictionary<string, object?> CreateDialogRequest(string id, string method, TimeSpan? timeout,
        params (string Key, object? Value)[] fields)
    {
        var request = CreateEvent(method, id);
        foreach (var (key, value) in fields)
        {
            if (value is not null) request[key] = value;
        }
        if (GetTimeoutMilliseconds(timeout) is { } timeoutMilliseconds)
            request["timeout"] = timeoutMilliseconds;
        return request;
    }

    private static Dictionary<string, object?> CreateEvent(string method, string? id = null) => new()
    {
        ["type"] = "extension_ui_request",
        ["id"] = id ?? NewId(),
        ["method"] = method
    };

    private static long? GetTimeoutMilliseconds(TimeSpan? timeout)
    {
        ValidateTimeout(timeout);
        return timeout is null || timeout == Timeout.InfiniteTimeSpan
            ? null
            : Math.Min((long)Math.Ceiling(timeout!.Value.TotalMilliseconds), uint.MaxValue - 1L);
    }

    private static void ValidateTimeout(TimeSpan? timeout)
    {
        if (timeout is { } value && value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Dialog timeout cannot be negative.");
    }

    private static TimeSpan BoundTimeout(TimeSpan timeout) => timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1)
        ? TimeSpan.FromMilliseconds(uint.MaxValue - 1)
        : timeout;
}
