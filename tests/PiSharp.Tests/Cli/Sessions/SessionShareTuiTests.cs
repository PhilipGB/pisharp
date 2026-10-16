using System.Diagnostics;
using System.Text;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class SessionShareTuiTests
{
    [Fact]
    public async Task SelectorPersistsForcedRadiusAndShareReportsMissingAuthentication()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new SessionShareFixture();
        var agent = Path.Combine(fixture.Root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
            "{\"quietStartup\":true,\"sessionShareDestination\":\"auto\"}");
        using var process = Start(fixture, agent);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new TerminalOutputCapture(process, text =>
        {
            if (text.Contains("Saved user setting sessionShareDestination = radius.", StringComparison.Ordinal)) saved.TrySetResult();
            if (text.Contains("Settings closed.", StringComparison.Ordinal)) closed.TrySetResult();
            if (text.Contains("Radius authentication is not configured.", StringComparison.Ordinal)) shared.TrySetResult();
        });
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await output.WaitForRawInputReadyAsync(TimeSpan.FromSeconds(10));
            await SendAsync(process, "/settings\nSession share destination\n\u001b[B\u001b[B\n");
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal("radius", (await UserSettings.LoadAsync(agent, _ => null)).ShareDestination);
            await SendAsync(process, "\u001b");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await SendAsync(process, "/share\n");
            await shared.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Empty(fixture.Commands);
            await SendAsync(process, "/quit\n");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error);
            Assert.Contains("Run '/login radius' first.", await output.Completion);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task EscapeCancelsAGistWithoutAConfirmationAndRestoresTheEditor()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new SessionShareFixture();
        fixture.SetGitHubScenario(new { wait = true });
        var agent = Path.Combine(fixture.Root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
            "{\"quietStartup\":true,\"sessionShareDestination\":\"gist\"}");
        var sessionPath = Path.Combine(fixture.Root, "session.json");
        await File.WriteAllTextAsync(sessionPath, new ConversationSession(fixture.Root, ConnectionSettings.LocalModel,
            ConnectionSettings.LocalEndpoint, "local").ToJson());
        using var process = Start(fixture, agent, sessionPath);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new TerminalOutputCapture(process, text =>
        {
            if (text.Contains("Share cancelled", StringComparison.Ordinal)) cancelled.TrySetResult();
        });
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await output.WaitForRawInputReadyAsync(TimeSpan.FromSeconds(10));
            await SendAsync(process, "/share\n");
            await ProcessTestHelpers.WaitForFileAsync(Path.Combine(fixture.Root, "gist-pid"));
            var pid = ProcessTestHelpers.ReadLinuxProcessId(Path.Combine(fixture.Root, "gist-pid"));
            await SendAsync(process, "\u001b");
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ProcessTestHelpers.WaitForLinuxProcessExit(pid);
            var uploadPath = fixture.Commands[1][^1];
            Assert.False(Directory.Exists(Path.GetDirectoryName(uploadPath)));
            await SendAsync(process, "/quit\n");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error);
            var captured = await output.Completion;
            Assert.Contains("Creating gist...", captured);
            Assert.DoesNotContain("Stop sharing?", captured);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            Assert.Fail(output.Output + "\n" + await error);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private static Process Start(SessionShareFixture fixture, string agent, string? sessionPath = null)
    {
        var command = "stty rows 40 cols 120; dotnet " + ProcessTestHelpers.ShellQuote(typeof(CliArguments).Assembly.Location) +
            " --local --no-tools --no-approve --offline" + (sessionPath is null ? " --no-session" : " --session " + ProcessTestHelpers.ShellQuote(sessionPath));
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            WorkingDirectory = fixture.Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-q", "-e", "-c", command, "/dev/null" }
        };
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(fixture.Root, "sessions");
        start.Environment["PATH"] = fixture.Root + Path.PathSeparator + start.Environment["PATH"];
        foreach (var name in new[] { "RADIUS_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_AUTH_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_MODELS_PATH" })
            start.Environment.Remove(name);
        return Process.Start(start)!;
    }

    private static async Task SendAsync(Process process, string input)
    {
        await process.StandardInput.WriteAsync(input.Replace('\n', '\r'));
        await process.StandardInput.FlushAsync();
    }

}
