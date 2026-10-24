using System.Threading.Channels;

namespace PiSharp.Tests;

internal static class RpcTestInputOutput
{
    internal static Task WaitForAsync(LockedWriter output, string fragment) => output.WaitForLineAsync(fragment);

    internal sealed class CommandReader(ChannelReader<string> channel) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try { return await channel.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }
    }

    internal sealed class LockedWriter : StringWriter
    {
        private readonly object _gate = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteAsync(string? value)
        {
            lock (_gate)
            {
                Write(value);
                SignalChangedUnsafe();
            }
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string? value)
        {
            lock (_gate)
            {
                WriteLine(value);
                SignalChangedUnsafe();
            }
            return Task.CompletedTask;
        }

        private void SignalChangedUnsafe()
        {
            var changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }

        public async Task WaitForLineAsync(string fragment)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (ToString().Contains(fragment, StringComparison.Ordinal)) return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(timeout.Token);
            }
        }

        public async Task WaitForLineAfterAsync(string fragment, int lineOffset)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    var lines = ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Skip(lineOffset).Any(line => line.Contains(fragment, StringComparison.Ordinal))) return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(timeout.Token);
            }
        }

        public int LineCount { get { lock (_gate) return ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length; } }

        public string[] Lines() { lock (_gate) return ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries); }
    }

}
