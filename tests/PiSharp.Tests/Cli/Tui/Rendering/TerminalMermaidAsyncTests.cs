using System.Text.RegularExpressions;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

/// <summary>P1 responsiveness: Mermaid must not hold the screen lock while waiting for external execution.</summary>
public sealed class TerminalMermaidAsyncTests
{
    private static string StripSgr(string text) => Regex.Replace(text, "\\u001b\\[[0-9;]*m", "");

    private static TerminalMermaidArt FakeArt(string label, int width = 10) =>
        new([label], [[new TerminalMermaidSpan(label, "border")]], width, []);

    private static TerminalConversationPresentation CreatePresentation(
        object gate,
        Func<string, TerminalMermaidArt?> sync,
        Func<string, CancellationToken, Task<TerminalMermaidArt?>> asyncProvider,
        TerminalTranscriptBuffer? transcript = null)
    {
        transcript ??= new TerminalTranscriptBuffer();
        return new TerminalConversationPresentation(
            transcript,
            new TerminalImageRenderer(),
            () => 80,
            () => TerminalTheme.Default,
            mermaidSyncProvider: sync,
            mermaidAsyncProvider: asyncProvider,
            applyUnderLockAndRequest: action =>
            {
                lock (gate) { action(); }
            });
    }

    private static Func<string, TerminalMermaidArt?> EngineCacheSync() => source =>
        TerminalMermaidEngine.TryGetCached(source, out var art) ? art : null;

    [Fact]
    public async Task HundredIncrementalUpdatesStayResponsiveWhileWorkerStalls()
    {
        TerminalMermaidEngine.ClearCacheForTests();
        var gate = new object();
        var tcs = new TaskCompletionSource<TerminalMermaidArt?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int asyncCalls = 0;
        Func<string, CancellationToken, Task<TerminalMermaidArt?>> stalled = async (source, ct) =>
        {
            Interlocked.Increment(ref asyncCalls);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        };

        var presentation = CreatePresentation(gate, EngineCacheSync(), stalled);
        const string prefix = "```mermaid\nflowchart LR\n  A --> FAKE100_B";
        for (var i = 0; i < 100; i++)
            presentation.SetAssistantText($"{prefix}{i}\n```");

        var gateTask = Task.Run(() =>
        {
            lock (gate) { Volatile.Read(ref asyncCalls); }
        });
        var completed = await Task.WhenAny(gateTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(gateTask, completed);
        await gateTask;

        var finalArt = FakeArt("FINAL-99");
        tcs.TrySetResult(finalArt);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (StripSgr(presentation.LiveText).Contains("FINAL-99", StringComparison.Ordinal))
                break;
            await Task.Delay(20);
        }
        Assert.Contains("FINAL-99", StripSgr(presentation.LiveText));
        Assert.True(Volatile.Read(ref asyncCalls) <= 5,
            $"Expected coalesced worker calls, got {Volatile.Read(ref asyncCalls)} for 100 updates.");
        TerminalMermaidEngine.ClearCacheForTests();
    }

    [Fact]
    public async Task ObsoleteAsyncResultsAreDiscarded()
    {
        TerminalMermaidEngine.ClearCacheForTests();
        var gate = new object();
        var slowGate = new TaskCompletionSource<TerminalMermaidArt?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastArt = FakeArt("FAST");
        var slowArt = FakeArt("SLOW");
        Func<string, CancellationToken, Task<TerminalMermaidArt?>> provider = async (source, ct) =>
        {
            if (source.Contains("SLOW", StringComparison.Ordinal))
                return await slowGate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            await Task.Delay(10, ct);
            return fastArt;
        };

        var presentation = CreatePresentation(gate, EngineCacheSync(), provider);
        presentation.SetAssistantText("```mermaid\nflowchart LR\n  A --> SLOW\n```");
        presentation.SetAssistantText("```mermaid\nflowchart LR\n  A --> FAST\n```");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (StripSgr(presentation.LiveText).Contains("FAST", StringComparison.Ordinal))
                break;
            await Task.Delay(20);
        }
        Assert.Contains("FAST", StripSgr(presentation.LiveText));

        slowGate.TrySetResult(slowArt);
        await Task.Delay(200);
        Assert.Contains("FAST", StripSgr(presentation.LiveText));
        Assert.DoesNotContain("SLOW", StripSgr(presentation.LiveText));
        TerminalMermaidEngine.ClearCacheForTests();
    }

    [Fact]
    public void NoNodeFallbackRendersCodeBlock()
    {
        TerminalMermaidEngine.ClearCacheForTests();
        try
        {
            var gate = new object();
            var presentation = CreatePresentation(gate, _ => null, (_, _) => Task.FromResult<TerminalMermaidArt?>(null));
            presentation.SetAssistantText("```mermaid\nflowchart LR\n  A --> NONODE_FALLBACK\n```");
            var live = StripSgr(presentation.LiveText);
            Assert.Contains("```mermaid", live);
            Assert.DoesNotContain("┌───┐", live);

            presentation.CommitAssistantText("```mermaid\npie\n  title x\n```");
            Assert.True(true);
        }
        finally
        {
            TerminalMermaidEngine.ClearCacheForTests();
        }
    }

    [Fact]
    public async Task ScreenGateStaysResponsiveWhileMermaidWorkerStalls()
    {
        TerminalMermaidEngine.ClearCacheForTests();
        try
        {
            var stalled = new TaskCompletionSource<TerminalMermaidArt?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<string, CancellationToken, Task<TerminalMermaidArt?>> provider = async (source, ct) =>
                await stalled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            using var output = new StringWriter();
            using var error = new StringWriter();
            using var screen = new TerminalScreen(output, error, () => 80, () => 24,
                new TerminalImageRenderer(), TerminalTheme.Default,
                queryTerminalColors: false,
                mermaidSyncProvider: _ => null,
                mermaidAsyncProvider: provider);

            screen.SetAssistantText("```mermaid\nflowchart LR\n  A --> GATETEST_STALL\n```");
            var gateOp = Task.Run(() => screen.SetShowHardwareCursor(true));
            var winner = await Task.WhenAny(gateOp, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(gateOp, winner);
            await gateOp;
            stalled.TrySetResult(null);
        }
        finally
        {
            TerminalMermaidEngine.ClearCacheForTests();
        }
    }

    [Fact]
    public async Task PersistentWorkerBoundsSubprocessCreation()
    {
        TerminalMermaidWorker.ResetForTests();
        TerminalMermaidEngine.ClearCacheForTests();
        try
        {
            var gate = new object();
            var presentation = new TerminalConversationPresentation(
                new TerminalTranscriptBuffer(),
                new TerminalImageRenderer(),
                () => 80,
                () => TerminalTheme.Default,
                mermaidSyncProvider: source =>
                    TerminalMermaidEngine.TryGetCached(source, out var art) ? art : null,
                mermaidAsyncProvider: (source, ct) => TerminalMermaidWorker.RenderAsync(source, ct),
                applyUnderLockAndRequest: action => { lock (gate) { action(); } });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
                presentation.SetAssistantText($"```mermaid\nflowchart LR\n  A --> B{i}\n```");
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            var finalLabel = "B19";
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (StripSgr(presentation.LiveText).Contains(finalLabel, StringComparison.Ordinal))
                    break;
                await Task.Delay(20);
            }
            sw.Stop();
            Assert.Contains(finalLabel, StripSgr(presentation.LiveText));
            Assert.True(TerminalMermaidWorker.ProcessCreationCount <= 2,
                $"Expected bounded worker processes, got {TerminalMermaidWorker.ProcessCreationCount}.");
        }
        finally
        {
            TerminalMermaidWorker.ResetForTests();
            TerminalMermaidEngine.ClearCacheForTests();
        }
    }
}
