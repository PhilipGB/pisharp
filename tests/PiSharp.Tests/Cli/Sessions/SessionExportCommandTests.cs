using System.Diagnostics;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class SessionExportCommandTests
{
    [Fact]
    public async Task StandaloneExportEscapesSessionAndDoesNotInitializeAProviderOrOverwrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-export-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ConversationStore(root, root);
            var session = new ConversationSession(root, "fixture-model", "https://offline.example/v1", "fixture");
            session.Append(new ChatMessage(ChatRole.User, "<script>secret prompt</script>"));
            var input = store.NewPath(session);
            await store.SaveAsync(session, input);
            async Task<(int Code, string Output, string Error)> Run(params string[] args)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
                start.ArgumentList.Add("--export");
                foreach (var arg in args) start.ArgumentList.Add(arg);
                start.Environment["PISHARP_AGENT_DIR"] = Path.Combine(root, "nonexistent-agent");
                start.Environment.Remove("PISHARP_BASE_URL");
                start.Environment.Remove("OPENAI_API_KEY");
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                return (process.ExitCode, await stdout, await stderr);
            }
            var first = await Run(input);
            var destination = input[..^".session.json".Length] + ".html";
            Assert.Equal(0, first.Code);
            Assert.Contains(destination, first.Output);
            Assert.Equal("", first.Error);
            var html = await File.ReadAllTextAsync(destination);
            Assert.DoesNotContain("<script>secret prompt</script>", html);
            Assert.Contains("secret prompt", html);
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destination));
            Assert.Equal(2, (await Run(input)).Code);
            Assert.Equal(html, await File.ReadAllTextAsync(destination));
            var explicitPath = Path.Combine(root, "explicit.html");
            Assert.Equal(0, (await Run(input, explicitPath)).Code);
            Assert.Equal(html, await File.ReadAllTextAsync(explicitPath));
            Assert.Equal(2, (await Run(input, explicitPath, "unexpected")).Code);
            var corrupt = Path.Combine(root, "corrupt.session.json");
            await File.WriteAllTextAsync(corrupt, "not-a-session");
            var rejected = await Run(corrupt);
            Assert.Equal(2, rejected.Code);
            Assert.DoesNotContain("not-a-session", rejected.Error);
            if (OperatingSystem.IsLinux())
            {
                var link = Path.Combine(root, "link.session.json");
                File.CreateSymbolicLink(link, input);
                Assert.Equal(2, (await Run(link)).Code);
                Assert.False(File.Exists(Path.Combine(root, "link.html")));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
