using System.Diagnostics;
using System.Text;

namespace PiSharp.Tests;

internal sealed class PtyScreenCapture(Process process, int rows = 24, int columns = 100)
{
    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private readonly List<(Func<string, bool> Predicate, TaskCompletionSource<string> Completion)> _waiters = [];
    private readonly List<(Func<int, int, bool> Predicate, TaskCompletionSource Completion)> _cursorWaiters = [];
    private string _lastFrame = "";
    private int _lastCursorRow;
    private int _lastCursorColumn;
    private readonly TaskCompletionSource _inputReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task InputReady => _inputReady.Task;
    public Task<string> ReadCompletion => ReadAsync(process);
    public string LastFrame { get { lock (_gate) return _lastFrame; } }
    public int LastCursorRow { get { lock (_gate) return _lastCursorRow; } }
    public int LastCursorColumn { get { lock (_gate) return _lastCursorColumn; } }
    public string CapturedOutput { get { lock (_gate) return _output.ToString(); } }

    public Task<string> WaitForFrameAsync(Func<string, bool> predicate)
    {
        lock (_gate)
        {
            if (_lastFrame.Length > 0 && predicate(_lastFrame)) return Task.FromResult(_lastFrame);
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((predicate, completion));
            return completion.Task;
        }
    }

    public Task WaitForCursorAsync(Func<int, int, bool> predicate)
    {
        lock (_gate)
        {
            if (predicate(_lastCursorRow, _lastCursorColumn)) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cursorWaiters.Add((predicate, completion));
            return completion.Task;
        }
    }

    private async Task<string> ReadAsync(Process process)
    {
        var buffer = new char[2048];
        int count;
        while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
        {
            lock (_gate)
            {
                _output.Append(buffer, 0, count);
                var captured = _output.ToString();
                if (captured.Contains("\u001b[?2004h", StringComparison.Ordinal)) _inputReady.TrySetResult();
                var frames = TerminalOutputFrameReader.Read(captured, rows, columns);
                if (frames.Count == 0) continue;
                _lastFrame = frames[^1].Screen;
                _lastCursorRow = frames[^1].CursorRow;
                _lastCursorColumn = frames[^1].CursorColumn;
                for (var index = _cursorWaiters.Count - 1; index >= 0; index--)
                {
                    var waiter = _cursorWaiters[index];
                    if (!waiter.Predicate(_lastCursorRow, _lastCursorColumn)) continue;
                    _cursorWaiters.RemoveAt(index);
                    waiter.Completion.TrySetResult();
                }
                for (var index = _waiters.Count - 1; index >= 0; index--)
                {
                    var waiter = _waiters[index];
                    if (!waiter.Predicate(_lastFrame)) continue;
                    _waiters.RemoveAt(index);
                    waiter.Completion.TrySetResult(_lastFrame);
                }
            }
        }
        lock (_gate) return _output.ToString();
    }
}
