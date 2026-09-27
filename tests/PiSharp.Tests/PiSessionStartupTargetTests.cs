using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class PiSessionStartupTargetTests
{
    [Fact]
    public void ResolvesProjectCwdFromExplicitSessionAndKeepsRelativeArgumentsAtInvocationDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-startup-target-" + Guid.NewGuid().ToString("N"));
        var invocation = Path.Combine(root, "invocation");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(invocation);
        Directory.CreateDirectory(project);
        var sourcePath = Path.Combine(invocation, "source.jsonl");
        File.WriteAllText(sourcePath, SessionJsonl(project));
        File.WriteAllText(Path.Combine(invocation, "system.md"), "system");
        try
        {
            var cli = CliArguments.Parse(["--session", "source.jsonl", "--session-dir", "sessions", "--system-prompt",
                "system.md", "@prompt.txt"]);

            var target = PiSessionStartupTarget.Resolve(invocation, cli);

            Assert.Equal(project, target.WorkingDirectory);
            Assert.Equal(sourcePath, target.Arguments.SessionPath);
            Assert.Equal(Path.Combine(invocation, "sessions"), target.Arguments.SessionDirectory);
            Assert.Equal(Path.Combine(invocation, "system.md"), target.Arguments.SystemPrompt);
            Assert.Equal(Path.Combine(invocation, "prompt.txt"), Assert.Single(target.Arguments.FileArguments!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ForkSourcePathKeepsTheInvocationProjectAsTheTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-session-fork-target-" + Guid.NewGuid().ToString("N"));
        var invocation = Path.Combine(root, "invocation");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(invocation);
        Directory.CreateDirectory(project);
        var sourcePath = Path.Combine(invocation, "source.session.json");
        File.WriteAllText(sourcePath, new ConversationSession(project, "model", null).ToJson());
        try
        {
            var cli = CliArguments.Parse(["--fork", "source.session.json"]);

            var target = PiSessionStartupTarget.Resolve(invocation, cli);

            Assert.Equal(invocation, target.WorkingDirectory);
            Assert.Equal(sourcePath, target.Arguments.ForkSource);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RpcStartupForksForeignProjectSessionIntoTheInvocationProject(bool piJsonlSource)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-cross-project-fork-" + Guid.NewGuid().ToString("N"));
        var sourceCwd = Path.Combine(root, "source");
        var invocationCwd = Path.Combine(root, "target");
        var agentDirectory = Path.Combine(root, "agent");
        var targetSessionDirectory = Path.Combine(invocationCwd, "forked-sessions");
        Directory.CreateDirectory(sourceCwd);
        Directory.CreateDirectory(invocationCwd);
        Directory.CreateDirectory(agentDirectory);
        var sourceStore = new ConversationStore(sourceCwd, Path.Combine(root, "source-sessions"));
        var source = new ConversationSession(sourceCwd, "gpt-4o", null, "openai");
        source.Append(new ChatMessage(ChatRole.User, "root prompt"));
        var branchPoint = source.Tree.HeadId;
        source.Append(new ChatMessage(ChatRole.User, "inactive branch"));
        var inactiveBranchId = source.Tree.HeadId;
        source.Tree.Select(branchPoint);
        source.Append(new ChatMessage(ChatRole.User, "copied from another project"));
        source.Append(new ChatMessage(ChatRole.Assistant, "source answer"));
        var nativePath = sourceStore.NewPath(source);
        await sourceStore.SaveAsync(source, nativePath);
        var sourcePath = nativePath;
        if (piJsonlSource)
        {
            sourcePath = Path.Combine(root, "source.jsonl");
            await PiJsonlSessionInterchange.ExportToFileAsync(source, sourcePath);
        }
        var originalSource = await File.ReadAllBytesAsync(sourcePath);

        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = invocationCwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--offline", "--fork", sourcePath,
                "--session-dir", targetSessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"state\",\"type\":\"get_state\"}");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var errorText = await error.WaitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"Exit code: {process.ExitCode}\n{errorText}");
            Assert.Equal("", errorText);
            var outputText = await output.WaitAsync(timeout.Token);
            using var state = JsonDocument.Parse(Assert.Single(outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
            Assert.True(state.RootElement.GetProperty("success").GetBoolean());
            var targetStore = new ConversationStore(invocationCwd, targetSessionDirectory);
            var forkPath = Assert.Single(Directory.GetFiles(targetSessionDirectory, "*.session.json"));
            var fork = await targetStore.LoadAsync(forkPath);
            Assert.Equal(invocationCwd, fork.WorkingDirectory);
            Assert.Equal(sourcePath, fork.ParentSessionPath);
            Assert.Equal(["root prompt", "copied from another project", "source answer"],
                fork.ActiveMessages().Select(message => message.Text));
            Assert.Contains(fork.Tree.Entries, entry => entry.Id == inactiveBranchId);
            Assert.Equal("openai", fork.Provider);
            var expectedSource = piJsonlSource
                ? PiJsonlSessionInterchange.Import(await File.ReadAllTextAsync(sourcePath), invocationCwd)
                : await sourceStore.LoadAsync(nativePath);
            Assert.Equal(expectedSource.Tree.Entries.Select(entry => entry.Id), fork.Tree.Entries.Select(entry => entry.Id));
            Assert.Equal(expectedSource.Tree.HeadId, fork.Tree.HeadId);
            Assert.Equal(originalSource, await File.ReadAllBytesAsync(sourcePath));
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RpcStartupImportsPiSessionIntoItsRecordedProject()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-cross-project-import-" + Guid.NewGuid().ToString("N"));
        var invocation = Path.Combine(root, "invocation");
        var project = Path.Combine(root, "project");
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "stored-sessions");
        Directory.CreateDirectory(invocation);
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(agentDirectory);
        var sourcePath = Path.Combine(invocation, "foreign-project.jsonl");
        await File.WriteAllTextAsync(sourcePath, SessionJsonl(project));

        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = invocation,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--local", "--offline", "--session", sourcePath,
                "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error.WaitAsync(timeout.Token));
            _ = await output.WaitAsync(timeout.Token);
            var store = new ConversationStore(project, sessionDirectory);
            var imported = await store.LoadAsync(Assert.Single(Directory.GetFiles(sessionDirectory, "*.session.json")));
            Assert.Equal(project, imported.WorkingDirectory);
            Assert.Equal("foreign-project", imported.Id);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string SessionJsonl(string workingDirectory) => string.Join('\n',
        JsonSerializer.Serialize(new { type = "session", version = 3, id = "foreign-project", timestamp = "2026-09-26T00:00:00Z", cwd = workingDirectory }),
        JsonSerializer.Serialize(new
        {
            type = "message",
            id = "entry-1",
            parentId = (string?)null,
            timestamp = "2026-09-26T00:00:01Z",
            message = new { role = "user", content = "imported from another project", timestamp = 1790380801000L }
        }), "");
}
