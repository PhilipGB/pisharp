using System.Text;
using System.Text.Json;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BashToolUpdateCoalescingTests
{
    [Fact]
    public async Task UpdatesPublishImmediatelyThenCoalesceAtOneHundredMilliseconds()
    {
        var clock = new ManualTimeProvider();
        var observed = new List<BashToolUpdate>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var output = new ShellOutputBuffer();
        await using var publisher = new BashToolUpdates(output, update =>
        {
            observed.Add(update);
            if (observed.Count == 2) second.TrySetResult();
        }, clock);
        await publisher.AppendAsync(Encoding.UTF8.GetBytes("alpha"), default);
        Assert.Equal("alpha", Assert.Single(observed).Text);
        clock.Advance(TimeSpan.FromMilliseconds(40));
        await publisher.AppendAsync(Encoding.UTF8.GetBytes("beta"), default);
        clock.Advance(TimeSpan.FromMilliseconds(59));
        await publisher.AppendAsync(Encoding.UTF8.GetBytes("gamma"), default);
        Assert.Single(observed);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("alphabetagamma", observed[1].Text);
        Assert.Equal("betagamma", observed[1].Delta);
        await publisher.CompleteAsync();
        Assert.Equal(0, clock.ActiveTimerCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, observed.Count);
    }

    [Fact]
    public async Task CompletionFlushesPendingSnapshotAndStopsFurtherUpdates()
    {
        var clock = new ManualTimeProvider();
        var observed = new List<BashToolUpdate>();
        await using var output = new ShellOutputBuffer();
        await using var publisher = new BashToolUpdates(output, observed.Add, clock);
        await publisher.AppendAsync("one"u8.ToArray(), default);
        await publisher.AppendAsync("two"u8.ToArray(), default);
        Assert.Single(observed);
        await publisher.CompleteAsync();
        Assert.Equal(["one", "onetwo"], observed.Select(update => update.Text));
        await publisher.AppendAsync("ignored"u8.ToArray(), default);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, clock.ActiveTimerCount);
        Assert.Equal(2, observed.Count);
        Assert.Equal("onetwo", (await output.FinishWithMetadataAsync()).Output);
    }

    [Fact]
    public async Task IncompleteUtf8PublishesEmptySnapshotWithoutInventingDecoderFlushUpdate()
    {
        var observed = new List<BashToolUpdate>();
        await using var output = new ShellOutputBuffer();
        await using var publisher = new BashToolUpdates(output, observed.Add, new ManualTimeProvider());
        await publisher.AppendAsync(new byte[] { 0xf0, 0x9f }, default);
        Assert.Equal("", Assert.Single(observed).Text);
        await output.FlushDecoderAsync();
        await publisher.CompleteAsync();
        Assert.Single(observed);
        Assert.Equal("�", (await output.FinishWithMetadataAsync()).Output);
    }

    [Fact]
    public async Task LineLimitedSnapshotsReferenceOnePrivateFileContainingAllOutput()
    {
        string? fullPath = null;
        try
        {
            var observed = new List<BashToolUpdate>();
            await using var output = new ShellOutputBuffer();
            await using var publisher = new BashToolUpdates(output, observed.Add, new ManualTimeProvider());
            var first = string.Concat(Enumerable.Repeat("line\n", 2200));
            await publisher.AppendAsync(Encoding.UTF8.GetBytes(first), default);
            var details = JsonSerializer.SerializeToElement(Assert.Single(observed).Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.True(details.GetProperty("truncation").GetProperty("truncated").GetBoolean());
            fullPath = details.GetProperty("fullOutputPath").GetString()!;
            Assert.Equal(first, await File.ReadAllTextAsync(fullPath));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fullPath));
            await publisher.AppendAsync("last\n"u8.ToArray(), default);
            await publisher.CompleteAsync();
            var last = JsonSerializer.SerializeToElement(observed[^1].Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(fullPath, last.GetProperty("fullOutputPath").GetString());
            Assert.Equal(first + "last\n", await File.ReadAllTextAsync(fullPath));
            Assert.Equal(fullPath, (await output.FinishWithMetadataAsync()).FullOutputPath);
        }
        finally { if (fullPath is not null) File.Delete(fullPath); }
    }

    [Fact]
    public async Task CallbackFailureSurfacesAtCompletionAndStopsTimer()
    {
        var clock = new ManualTimeProvider();
        await using var output = new ShellOutputBuffer();
        await using var publisher = new BashToolUpdates(output, _ => throw new IOException("observer failed"), clock);
        await publisher.AppendAsync("one"u8.ToArray(), default);
        var failure = await Assert.ThrowsAsync<IOException>(publisher.CompleteAsync);
        Assert.Equal("observer failed", failure.Message);
        Assert.Equal(0, clock.ActiveTimerCount);
    }
}
