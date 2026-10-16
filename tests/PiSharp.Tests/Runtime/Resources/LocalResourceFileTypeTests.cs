using System.Diagnostics;
using System.Net.Sockets;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class LocalResourceFileTypeTests
{
    [Theory]
    [InlineData("fifo")]
    [InlineData("socket")]
    [InlineData("directory")]
    [InlineData("linked-fifo")]
    public async Task NonRegularPriorityCandidatesAreSkippedWithoutOpeningThem(string type)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-filetype-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            var path = Path.Combine(root, "AGENTS.override.md");
            if (type is "fifo" or "linked-fifo")
            {
                var target = type == "fifo" ? path : Path.Combine(root, "pipe");
                using var process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { target } })!;
                await process.WaitForExitAsync();
                Assert.Equal(0, process.ExitCode);
                if (type == "linked-fifo") File.CreateSymbolicLink(path, target);
            }
            else if (type == "socket") socket.Bind(new UnixDomainSocketEndPoint(path));
            else Directory.CreateDirectory(path);

            Assert.False(LocalResourceFileType.IsRegularFile(path));
            var fallback = Path.Combine(root, "AGENTS.md");
            await File.WriteAllTextAsync(fallback, "retained fallback");
            var loaded = new List<string>();
            var warnings = new List<ResourceDiagnostic>();
            var text = await ContextInstructions.LoadAsync(root, Path.Combine(root, "agent"),
                loadedFiles: loaded, reportDiagnostic: warnings.Add);
            Assert.Equal([fallback], loaded);
            Assert.Contains("retained fallback", text);
            Assert.Empty(warnings);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SymlinksToRegularInstructionFilesStillLoadTheirContents()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-filetype-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "target.md");
            var path = Path.Combine(root, "AGENTS.md");
            await File.WriteAllTextAsync(target, "linked instructions");
            File.CreateSymbolicLink(path, target);
            Assert.True(LocalResourceFileType.IsRegularFile(path));
            var loaded = new List<string>();
            Assert.Contains("linked instructions", await ContextInstructions.LoadAsync(root, Path.Combine(root, "agent"), loadedFiles: loaded));
            Assert.Equal([path], loaded);
        }
        finally { Directory.Delete(root, true); }
    }
}
