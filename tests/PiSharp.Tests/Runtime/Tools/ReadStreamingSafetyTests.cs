using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class ReadStreamingSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationSettlesDuringCountingAndSelectedContentReads(bool duringSelection)
    {
        var path = Path.Combine(Path.GetTempPath(), "pisharp-read-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = File.Create(path)) file.SetLength(4 * 1024 * 1024);
            using var cancellation = new CancellationTokenSource();
            await using var stream = new ObservedFileStream(path, blockAtRewind: duringSelection ? 2 : 1);
            var read = StreamingTextReader.SelectDetailedAsync(stream, "fixture.txt", 1, null, cancellation.Token);
            await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(read.IsCanceled);
            Assert.Equal(4 * 1024 * 1024, new FileInfo(path).Length);
            Assert.Equal(duringSelection ? 4 * 1024 * 1024 : 0, stream.BytesRead);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task HugeSingleLineUsesBoundedReadBuffersAndRetainsCompleteCounts()
    {
        var path = Path.Combine(Path.GetTempPath(), "pisharp-read-bound-" + Guid.NewGuid().ToString("N"));
        const long size = 64 * 1024 * 1024;
        try
        {
            await using (var file = File.Create(path)) file.SetLength(size);
            await using var stream = new ObservedFileStream(path);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var result = await StreamingTextReader.SelectDetailedAsync(stream, "fixture.txt", 1, null, deadline.Token);

            Assert.Equal("[Line 1 is 64.0MB, exceeds 50.0KB limit. Use bash: sed -n '1p' fixture.txt | head -c 51200]", result.Text);
            Assert.NotNull(result.Truncation);
            Assert.Equal(size, result.Truncation.TotalBytes);
            Assert.Equal(1, result.Truncation.TotalLines);
            Assert.Equal("", result.Truncation.Content);
            Assert.True(result.Truncation.FirstLineExceedsLimit);
            Assert.InRange(stream.MaximumRequestedBytes, 1, 64 * 1024);
            Assert.Equal(2 * size, stream.BytesRead);
            Assert.Equal(size, stream.Length);
        }
        finally { File.Delete(path); }
    }

    private sealed class ObservedFileStream(string path, int? blockAtRewind = null)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous)
    {
        private int _rewinds;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long BytesRead { get; private set; }
        public int MaximumRequestedBytes { get; private set; }

        public override long Position
        {
            get => base.Position;
            set
            {
                base.Position = value;
                if (value == 0) _rewinds++;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRequestedBytes = Math.Max(MaximumRequestedBytes, buffer.Length);
            if (blockAtRewind == _rewinds)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }
}
