using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Runtime.Tools;
using Xunit.Abstractions;

namespace PiSharp.Tests;

public sealed class GrepContextReaderTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        using var fixture = SearchProcessProtocolTests.SourceFixture("grep", "context");
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [row.GetProperty("caseId").GetString()!, row.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RepeatedContextMatchesPinnedOutputWithBoundedScanning(string caseId, string fixture)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var document = JsonDocument.Parse(fixture);
        var row = document.RootElement;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-grep-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var streams = new List<ObservedFileStream>();
        try
        {
            var path = Path.Combine(root, "file.txt");
            var contents = row.GetProperty("setup").GetProperty("files").GetProperty("file.txt");
            if (contents.ValueKind == JsonValueKind.String) await File.WriteAllTextAsync(path, contents.GetString());
            else
            {
                var recipe = contents.GetProperty("concat");
                var prefix = recipe[0];
                var chunk = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(prefix.GetProperty("repeat").GetString(), 64)));
                await using var file = File.Create(path);
                for (var index = 0; index < prefix.GetProperty("count").GetInt32() / 64; index++) await file.WriteAsync(chunk);
                await file.WriteAsync(Encoding.UTF8.GetBytes(recipe[1].GetString()!));
            }
            var length = new FileInfo(path).Length;
            byte[] before;
            await using (var file = File.OpenRead(path)) before = await SHA256.HashDataAsync(file);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));

            var context = row.GetProperty("setup").GetProperty("arguments").GetProperty("context").GetDouble();
            var result = await RipgrepSearch.ExecuteAsync(root, "needle", "file.txt", null, false, false, context, 100,
                deadline.Token, openContextFile: value =>
                {
                    var stream = new ObservedFileStream(value);
                    streams.Add(stream);
                    return stream;
                });

            var expected = row.GetProperty("expectedResult");
            Assert.Equal(expected.GetProperty("content")[0].GetProperty("text").GetString(), result.Text);
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("details"),
                JsonSerializer.SerializeToElement(result.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            output.WriteLine(JsonSerializer.Serialize(new
            {
                caseId,
                inputBytes = length,
                contextBytesRead = streams.Sum(stream => stream.BytesRead),
                opens = streams.Count,
                rewinds = streams.Sum(stream => stream.Rewinds)
            }));
            Assert.Single(streams);
            var overflow = caseId == "grep-context-cache-overflow";
            Assert.InRange(streams[0].BytesRead, length - 8192, overflow ? length + 100 * (2 * context + 1) * 1024 + 8192 : length);
            Assert.InRange(streams[0].MaximumRequestedBytes, 1, 8192);
            if (overflow) Assert.True(streams[0].Rewinds > 0);
            else Assert.Equal(0, streams[0].Rewinds);
            Assert.All(streams, stream => Assert.True(stream.IsDisposed));
            await using var after = File.OpenRead(path);
            Assert.Equal(before, await SHA256.HashDataAsync(after));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancellationDuringContextReadDisposesTheStreamAndAllowsRecovery()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ObservedFileStream? stream = null;
        Task<SearchToolOutput>? pending = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "needle\n");
            var opened = new TaskCompletionSource<ObservedFileStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = RipgrepSearch.ExecuteAsync(root, "needle", "file.txt", null, false, false, 1, 100, cancellation.Token,
                openContextFile: path =>
                {
                    stream = new(path) { BlockReads = true };
                    opened.SetResult(stream);
                    return stream;
                });
            stream = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(stream.IsDisposed);
            Assert.Empty(ProcessTestHelpers.FindChildrenWithArgument(Path.Combine(root, "file.txt")));
            var recovered = await RipgrepSearch.ExecuteAsync(root, "needle", "file.txt", null, false, false, 1, 100, CancellationToken.None);
            Assert.Equal("file.txt:1: needle\nfile.txt-2- ", recovered.Text);
        }
        finally
        {
            cancellation.Cancel();
            if (pending is not null) try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedContextReadRollsBackItsPartialBlockAndCachesTheFailure()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var streams = new List<ObservedFileStream>();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "needle\nneedle\n" + new string('x', 20000));
            var result = await RipgrepSearch.ExecuteAsync(root, "needle", "file.txt", null, false, false, 5, 100, CancellationToken.None,
                openContextFile: path =>
                {
                    var stream = new ObservedFileStream(path) { FailAfterFirstRead = true };
                    streams.Add(stream);
                    return stream;
                });
            Assert.Equal("file.txt:1: (unable to read file)\nfile.txt:2: (unable to read file)", result.Text);
            Assert.Null(result.Details);
            Assert.Single(streams);
            Assert.True(streams[0].IsDisposed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InterleavedAndBackwardWindowsKeepFileHandlesAndCachedTextBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var streams = new List<ObservedFileStream>();
        var maximumOpen = 0;
        try
        {
            await using (var reader = new GrepContextReader(path =>
            {
                var stream = new ObservedFileStream(path);
                streams.Add(stream);
                maximumOpen = Math.Max(maximumOpen, streams.Count(value => !value.IsDisposed));
                return stream;
            }))
            {
                var files = Enumerable.Range(0, 10).Select(index => Path.Combine(root, index + ".txt")).ToArray();
                foreach (var path in files) await File.WriteAllTextAsync(path, "one\nneedle\nthree\n");
                foreach (var path in files)
                {
                    var block = new List<(double, string, bool)>();
                    Assert.True(await reader.AppendAsync(path, 2, 1, (number, text, truncated) => block.Add((number, text, truncated)), CancellationToken.None));
                    Assert.Equal([(1d, "one", false), (2d, "needle", false), (3d, "three", false)], block);
                }
                Assert.True(streams[0].IsDisposed);
                var forward = new List<(double, string, bool)>();
                await reader.AppendAsync(files[9], 3, 1, (number, text, truncated) => forward.Add((number, text, truncated)), CancellationToken.None);
                Assert.Equal([(2d, "needle", false), (3d, "three", false), (4d, "", false)], forward);
                var backward = new List<(double, string, bool)>();
                await reader.AppendAsync(files[9], 1, 1, (number, text, truncated) => backward.Add((number, text, truncated)), CancellationToken.None);
                Assert.Equal([(1d, "one", false), (2d, "needle", false)], backward);
                Assert.Equal(1, streams[^1].Rewinds);
                await reader.AppendAsync(files[0], 2, 1, (_, _, _) => { }, CancellationToken.None);
                Assert.Equal(11, streams.Count);
                var wide = Path.Combine(root, "wide.txt");
                await File.WriteAllTextAsync(wide, string.Concat(Enumerable.Repeat(new string('x', 600) + "\n", 5000)));
                await reader.AppendAsync(wide, 1, 3000, (_, _, _) => { }, CancellationToken.None);
                Assert.InRange(maximumOpen, 1, GrepContextReader.MaximumFiles);
                Assert.InRange(reader.RetainedCharacters, 1, GrepContextReader.MaximumFiles * GrepContextReader.MaximumCharactersPerFile);
                Assert.InRange(reader.RetainedLines, 1, GrepContextReader.MaximumFiles * GrepContextReader.MaximumLinesPerFile);
                await reader.AppendAsync(wide, 4000, 1, (_, _, _) => { }, CancellationToken.None);
                Assert.True(streams[^1].BytesRead <= new FileInfo(wide).Length);
                Assert.Equal(0, streams[^1].Rewinds);
            }
            Assert.All(streams, stream => Assert.True(stream.IsDisposed));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ObservedFileStream(string path)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        public long BytesRead { get; private set; }
        public int MaximumRequestedBytes { get; private set; }
        public int Rewinds { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool BlockReads { get; init; }
        public bool FailAfterFirstRead { get; init; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRequestedBytes = Math.Max(MaximumRequestedBytes, buffer.Length);
            if (BlockReads)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (FailAfterFirstRead && BytesRead > 0) throw new IOException("Fixture context read failure");
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin == SeekOrigin.Begin && offset < Position) Rewinds++;
            return base.Seek(offset, origin);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
