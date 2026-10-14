using System.Text;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ContextInstructionLoadingTests
{
    [Fact]
    public async Task ASecondLeadingBomRemainsPartOfTheInstructions()
    {
        var root = CreateDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "AGENTS.md"), Encoding.UTF8.GetBytes("\uFEFF\uFEFFretained"));
            var text = await ContextInstructions.LoadAsync(root, Path.Combine(root, "agent"));
            Assert.Contains(">\n\uFEFFretained\n</project_instructions>", text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AlreadyCanceledLoadingRetainsTheCallerTokenAndDoesNotPublishFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var loaded = new List<string>();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContextInstructions.LoadAsync(
            Path.GetTempPath(), Path.GetTempPath(), cancellation.Token, loaded));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task CompleteLargeAncestorInstructionsKeepTheirExactOrderAndContents()
    {
        var root = CreateDirectory();
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "project");
        var child = Path.Combine(project, "child");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(child);
        try
        {
            var files = new[] { Path.Combine(agent, "AGENTS.md"), Path.Combine(project, "AGENTS.md"), Path.Combine(child, "AGENTS.md") };
            var contents = new[] { new string('a', 140 * 1024), new string('b', 140 * 1024), new string('c', 140 * 1024) };
            for (var index = 0; index < files.Length; index++) await File.WriteAllTextAsync(files[index], contents[index]);
            var loaded = new List<string>();
            var text = await ContextInstructions.LoadAsync(child, agent, loadedFiles: loaded);
            Assert.Equal(files, loaded);
            Assert.Equal(string.Concat(files.Zip(contents, (path, content) =>
                $"<project_instructions path=\"{path}\">\n{content}\n</project_instructions>\n\n")), text);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableInstructionsAllowAFallbackOrChildToLoad(bool ancestor)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateDirectory();
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        try
        {
            var denied = Path.Combine(root, ancestor ? "AGENTS.md" : "AGENTS.override.md");
            await File.WriteAllTextAsync(denied, "denied instructions");
            File.SetUnixFileMode(denied, UnixFileMode.None);
            var fallback = Path.Combine(ancestor ? child : root, "AGENTS.md");
            await File.WriteAllTextAsync(fallback, "retained instructions");
            var loaded = new List<string>();
            var diagnostics = new List<ResourceDiagnostic>();
            var text = await ContextInstructions.LoadAsync(child, Path.Combine(root, "agent"), loadedFiles: loaded,
                reportDiagnostic: diagnostics.Add);
            Assert.Equal([fallback], loaded);
            Assert.Contains("retained instructions", text);
            Assert.DoesNotContain("denied instructions", text);
            var warning = Assert.Single(diagnostics);
            Assert.Equal("warning", warning.Type);
            Assert.Equal(denied, warning.Path);
            Assert.StartsWith("Warning: Could not read " + denied + ": ", warning.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReadWarningsFollowGlobalThenChildToAncestorWhileContentUsesAncestorToChild()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateDirectory();
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "project");
        var child = Path.Combine(project, "child");
        try
        {
            foreach (var directory in new[] { agent, project, child })
            {
                Directory.CreateDirectory(directory);
                var denied = Path.Combine(directory, "AGENTS.override.md");
                await File.WriteAllTextAsync(denied, "DENIED");
                File.SetUnixFileMode(denied, UnixFileMode.None);
                await File.WriteAllTextAsync(Path.Combine(directory, "AGENTS.md"), "retained");
            }
            var warnings = new List<ResourceDiagnostic>();
            var loaded = new List<string>();
            await ContextInstructions.LoadAsync(child, agent, loadedFiles: loaded, reportDiagnostic: warnings.Add);
            Assert.Equal(new[] { agent, child, project }.Select(directory => Path.Combine(directory, "AGENTS.override.md")),
                warnings.Select(warning => warning.Path));
            Assert.Equal(new[] { agent, project, child }.Select(directory => Path.Combine(directory, "AGENTS.md")), loaded);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
