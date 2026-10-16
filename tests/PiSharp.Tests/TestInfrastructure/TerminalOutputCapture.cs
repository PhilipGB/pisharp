using System.Diagnostics;
using System.Text;

namespace PiSharp.Tests;

internal sealed class TerminalOutputCapture
{
    private const string RawInputReadyMarker = "\u001b[?2004h";
    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private readonly TaskCompletionSource _rawInputReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<string> _completion;

    public TerminalOutputCapture(Process process, Action<string>? observe = null)
    {
        _completion = ReadAsync(process, observe);
    }

    public string Output
    {
        get { lock (_gate) return _output.ToString(); }
    }

    public Task<string> Completion => _completion;

    public Task WaitForRawInputReadyAsync(TimeSpan timeout) => _rawInputReady.Task.WaitAsync(timeout);

    private async Task<string> ReadAsync(Process process, Action<string>? observe)
    {
        var buffer = new char[4096];
        while (await process.StandardOutput.ReadAsync(buffer) is var count && count > 0)
        {
            string current;
            lock (_gate)
            {
                _output.Append(buffer, 0, count);
                current = _output.ToString();
            }

            if (current.Contains(RawInputReadyMarker, StringComparison.Ordinal))
                _rawInputReady.TrySetResult();
            observe?.Invoke(current);
        }

        if (!_rawInputReady.Task.IsCompleted)
            _rawInputReady.TrySetException(new InvalidOperationException("The terminal exited before raw input became ready."));
        return Output;
    }
}
