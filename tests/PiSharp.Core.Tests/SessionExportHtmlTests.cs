using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// HTML session export (self-contained deterministic page) and /share gh-gist plumbing.
/// </summary>
public sealed class SessionExportHtmlTests
{
    private static async Task<(SessionController Controller, SessionStore Store)> CreateFlushedControllerAsync(
        TempDirectory temp)
    {
        var (controller, _) = await SessionLifecycleTests.Harness.CreateControllerAsync(temp);
        var store = controller.Store!;
        var document = controller.Document!;
        var message = JsonSerializer.SerializeToElement(new
        {
            role = "assistant",
            provider = "p",
            model = "m",
            content = new object[] { new { type = "text", text = "answer <script>alert(1)</script>" } },
            timestamp = 1_700_000_000_000,
            usage = new
            {
                input = 10, output = 5, cacheRead = 0, cacheWrite = 0, totalTokens = 15,
                cost = new { input = 0.0, output = 0.0, cacheRead = 0.0, cacheWrite = 0.0, total = 0.001 },
            },
        });
        await store.AppendEntriesAsync(document,
        [
            new MessageEntry("u1", null, DateTimeOffset.UnixEpoch,
                JsonSerializer.SerializeToElement(new { role = "user", content = "hi <b>there</b>" })),
            new MessageEntry("a1", "u1", DateTimeOffset.UnixEpoch, message),
            new CompactionEntry("c1", "a1", DateTimeOffset.UnixEpoch, "summary <xmp>", "a1", 1,
                Usage: JsonSerializer.SerializeToElement(new
                {
                    input = 1, output = 1, cacheRead = 0, cacheWrite = 0, totalTokens = 2,
                    cost = new { input = 0.0, output = 0.0, cacheRead = 0.0, cacheWrite = 0.0, total = 0.0 },
                })),
            new SessionInfoEntry("i1", "c1", DateTimeOffset.UnixEpoch, "my <name>"),
        ], CancellationToken.None);
        return (controller, store);
    }

    [Fact]
    public async Task RenderEscapesDynamicContentAndIsDeterministic()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await CreateFlushedControllerAsync(temp);
        var document = controller.Document!;

        var first = SessionExportHtml.Render(document);
        var second = SessionExportHtml.Render(document);

        Assert.Equal(first, second);
        Assert.StartsWith("<!doctype html>", first);
        // Every dynamic string is HTML-escaped.
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", first);
        Assert.Contains("hi &lt;b&gt;there&lt;/b&gt;", first);
        Assert.Contains("summary &lt;xmp&gt;", first);
        Assert.Contains("my &lt;name&gt;", first);
        Assert.DoesNotContain("<script>", first);
        // Entry labels and the usage line render.
        Assert.Contains("message · user", first);
        Assert.Contains("message · assistant", first);
        Assert.Contains("compaction", first);
        Assert.Contains("tokens 15 (in 10, out 5)", first);
        Assert.Contains("cost $0.001", first);
    }

    [Fact]
    public async Task ExportRequiresAFlushedV3Session()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await SessionLifecycleTests.Harness.CreateControllerAsync(temp);

        // A lazy (unflushed) Pi v3 document has a planned path but no file yet (pinned
        // "existsSync(sessionFile)" guard).
        var notFlushed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionExportHtml.ExportHtmlAsync(controller.Document!, null, CancellationToken.None));
        Assert.Equal("Nothing to export yet - start a conversation first", notFlushed.Message);

        // Legacy (pre-Pi) documents cannot be rendered at all.
        var legacy = new SessionDocument(
            Path.Combine(temp.Path, "legacy.jsonl"), SessionHeader.Create(temp.Path, "model"));
        var legacyError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionExportHtml.ExportHtmlAsync(legacy, null, CancellationToken.None));
        Assert.Equal("HTML export requires a Pi v3 session.", legacyError.Message);
    }

    [Fact]
    public async Task ExportWritesTheFileAndCreatesMissingDirectories()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await CreateFlushedControllerAsync(temp);
        var target = Path.Combine(temp.Path, "nested", "out.html");

        var written = await SessionExportHtml.ExportHtmlAsync(controller.Document!, target, CancellationToken.None);

        Assert.Equal(target, written);
        var content = await File.ReadAllTextAsync(target);
        Assert.StartsWith("<!doctype html>", content);
    }

    [Fact]
    public async Task DefaultExportPathUsesThePinnedNaming()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await CreateFlushedControllerAsync(temp);

        Assert.Equal(
            Path.Combine("/cwd", $"pisharp-session-{Path.GetFileNameWithoutExtension(controller.Document!.FilePath)}.html"),
            SessionExportHtml.GetDefaultExportPath(controller.Document!.FilePath, "/cwd"));
    }

    [Fact]
    public async Task ExportFallsBackToDefaultNameInTheProcessCwd()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await CreateFlushedControllerAsync(temp);
        var expected = SessionExportHtml.GetDefaultExportPath(controller.Document!.FilePath, Environment.CurrentDirectory);
        try
        {
            var written = await SessionExportHtml.ExportHtmlAsync(controller.Document!, null, CancellationToken.None);
            Assert.Equal(expected, written);
            Assert.True(File.Exists(written));
        }
        finally
        {
            if (File.Exists(expected))
            {
                File.Delete(expected);
            }
        }
    }
}

/// <summary>/share: gh gist creation through the injectable launcher seam.</summary>
public sealed class SessionShareTests
{
    [Fact]
    public async Task ShareReturnsTheGistUrlFromGhOutput()
    {
        using var temp = TempDirectory.Create();
        var file = Path.Combine(temp.Path, "session.html");
        await File.WriteAllTextAsync(file, "<!doctype html>");
        // Real gh prints the gist url on its own final line.
        SessionShare.Launcher = async (path, _, _) =>
        {
            Assert.Equal(file, path);
            await Task.CompletedTask;
            return (0, "https://gist.github.com/u/abc123\n", null);
        };

        try
        {
            var url = await SessionShare.ShareFileAsync(file, CancellationToken.None);
            Assert.Equal("https://gist.github.com/u/abc123", url);
        }
        finally
        {
            SessionShare.Launcher = null;
        }
    }

    [Fact]
    public async Task ShareSurfacesTheGhErrorOnNonZeroExit()
    {
        using var temp = TempDirectory.Create();
        var file = Path.Combine(temp.Path, "session.html");
        await File.WriteAllTextAsync(file, "<!doctype html>");
        SessionShare.Launcher = async (_, _, _) =>
        {
            await Task.CompletedTask;
            return (1, string.Empty, "authentication required");
        };

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => SessionShare.ShareFileAsync(file, CancellationToken.None));
            Assert.Contains("authentication required", error.Message);
        }
        finally
        {
            SessionShare.Launcher = null;
        }
    }

    [Fact]
    public async Task ShareRejectsAMissingFile()
    {
        using var temp = TempDirectory.Create();
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => SessionShare.ShareFileAsync(Path.Combine(temp.Path, "missing.html"), CancellationToken.None));
    }

    [Fact]
    public async Task ShareCreatesANonPublicGist()
    {
        using var temp = TempDirectory.Create();
        var file = Path.Combine(temp.Path, "session.html");
        await File.WriteAllTextAsync(file, "<!doctype html>");
        IReadOnlyList<string>? captured = null;
        SessionShare.Launcher = async (path, args, _) =>
        {
            Assert.Equal(file, path);
            captured = args;
            await Task.CompletedTask;
            return (0, "https://gist.github.com/u/abc123\n", null);
        };

        try
        {
            await SessionShare.ShareFileAsync(file, CancellationToken.None);

            // Pinned parity: the gist is private (--public=false), never --public.
            Assert.NotNull(captured);
            Assert.Contains("--public=false", captured!);
            Assert.DoesNotContain("--public", captured!);
        }
        finally
        {
            SessionShare.Launcher = null;
        }
    }

    [Fact]
    public async Task TheRealProcessLauncherStartsAndCapturesTheOutput()
    {
        // The test host runs on the .NET muxer; launching it is a deterministic, offline
        // way to exercise the real launcher without requiring gh or network access.
        var host = Environment.ProcessPath
            ?? throw new InvalidOperationException("test host process path is unavailable");

        var (exitCode, stdOut, _) = await SessionShare.RunProcessAsync(host, ["--version"], CancellationToken.None);
        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdOut));

        // A failing invocation still yields a captured stderr and its exit code.
        var (failedExit, _, stdErr) =
            await SessionShare.RunProcessAsync(host, ["not-a-real-pisharp-command"], CancellationToken.None);
        Assert.NotEqual(0, failedExit);
        Assert.False(string.IsNullOrWhiteSpace(stdErr));
    }
}
