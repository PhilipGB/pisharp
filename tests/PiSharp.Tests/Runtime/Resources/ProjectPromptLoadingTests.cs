using System.Text;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ProjectPromptLoadingTests
{
    [Fact]
    public async Task CancellationDuringAReadWarningWinsOverTheLiteralFallback()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, "SYSTEM.md");
            await File.WriteAllTextAsync(path, "DENIED");
            File.SetUnixFileMode(path, UnixFileMode.None);
            using var cancellation = new CancellationTokenSource();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PromptFileInput.ResolveAsync(
                path, root, "system prompt", cancellation.Token, _ => cancellation.Cancel()));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExplicitSourcesAvoidReadingUnusedDiscoveredPrompts()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateDirectory();
        try
        {
            foreach (var name in new[] { "SYSTEM.md", "APPEND_SYSTEM.md" })
            {
                var path = Path.Combine(root, name);
                await File.WriteAllTextAsync(path, "DENIED");
                File.SetUnixFileMode(path, UnixFileMode.None);
            }
            var warnings = new List<ResourceDiagnostic>();
            var result = await ProjectPrompts.LoadWithSourcesAsync(Path.Combine(root, "project"), root, false,
                reportDiagnostic: warnings.Add, systemPromptOverride: "system literal", appendSystemPromptOverrides: ["append literal"]);
            Assert.Equal("system literal", result.System);
            Assert.Equal("append literal", result.Append);
            Assert.Null(result.SystemPath);
            Assert.Empty(result.AppendPaths);
            Assert.Empty(warnings);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExplicitEmptySourceListSuppressesDiscoveredAppendAndEmptySystemSuppressesDiscovery()
    {
        var root = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "SYSTEM.md"), "GLOBAL");
            await File.WriteAllTextAsync(Path.Combine(root, "APPEND_SYSTEM.md"), "GLOBAL");
            var result = await ProjectPrompts.LoadWithSourcesAsync(Path.Combine(root, "project"), root, false,
                systemPromptOverride: "", appendSystemPromptOverrides: []);
            Assert.Null(result.System);
            Assert.Equal("", result.Append);
            Assert.Null(result.SystemPath);
            Assert.Empty(result.AppendPaths);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrustedProjectAppendReplacesTheGlobalAppendWithoutReadingIt()
    {
        var root = CreateDirectory();
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(project, ".pi"));
        try
        {
            var globalPath = Path.Combine(agent, "APPEND_SYSTEM.md");
            var projectPath = Path.Combine(project, ".pi", "APPEND_SYSTEM.md");
            await File.WriteAllTextAsync(globalPath, "GLOBAL");
            await File.WriteAllTextAsync(projectPath, "PROJECT");
            var result = await ProjectPrompts.LoadWithSourcesAsync(project, agent, true);
            Assert.Equal("PROJECT", result.Append);
            Assert.Equal([projectPath], result.AppendPaths);
            Assert.Equal("GLOBAL", (await ProjectPrompts.LoadWithSourcesAsync(project, agent, false)).Append);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("SYSTEM.md")]
    [InlineData("APPEND_SYSTEM.md")]
    public async Task ACompleteLargePromptIsLoadedWithItsSource(string name)
    {
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, name);
            var contents = new string('x', 65 * 1024);
            await File.WriteAllTextAsync(path, contents);
            var result = await ProjectPrompts.LoadWithSourcesAsync(Path.Combine(root, "project"), root, false);
            Assert.Equal(contents, name == "SYSTEM.md" ? result.System : result.Append);
            if (name == "SYSTEM.md") Assert.Equal(path, result.SystemPath);
            else Assert.Equal([path], result.AppendPaths);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FileDecodingStripsOneBomAndUsesUtf8Replacement()
    {
        var root = CreateDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "SYSTEM.md"), Encoding.UTF8.GetBytes("\uFEFF\uFEFFretained"));
            await File.WriteAllBytesAsync(Path.Combine(root, "APPEND_SYSTEM.md"), [98, 97, 100, 32, 255]);
            var result = await ProjectPrompts.LoadWithSourcesAsync(Path.Combine(root, "project"), root, false);
            Assert.Equal("\uFEFFretained", result.System);
            Assert.Equal("bad \uFFFD", result.Append);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AnUnreadableDiscoveredPromptRemainsALiteralPathInsteadOfSelectingTheGlobalFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateDirectory();
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(project, ".pi"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "SYSTEM.md"), "GLOBAL");
            var path = Path.Combine(project, ".pi", "SYSTEM.md");
            await File.WriteAllTextAsync(path, "DENIED");
            File.SetUnixFileMode(path, UnixFileMode.None);
            var result = await ProjectPrompts.LoadWithSourcesAsync(project, agent, true);
            Assert.Equal(path, result.System);
            Assert.Equal(path, result.SystemPath);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-project-prompt-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
