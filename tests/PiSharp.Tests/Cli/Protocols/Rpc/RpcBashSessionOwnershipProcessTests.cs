using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcBashSessionOwnershipProcessTests
{
    [Fact]
    public async Task EofCancelsAndPersistsAnActiveBashResultToItsOrigin()
    {
        if (!OperatingSystem.IsLinux()) return;

        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-bash-eof-owner-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(sessionDirectory);
        var store = new ConversationStore(root, sessionDirectory);
        var source = new ConversationSession(root, ConnectionSettings.LocalModel,
            new Uri(ConnectionSettings.LocalEndpoint).ToString(), "local");
        source.Append(new ChatMessage(ChatRole.User, "eof-origin-marker"));
        var sourcePath = store.NewPath(source);
        await store.SaveAsync(source, sourcePath);

        const string command = "printf 'eof-bash-ready\\n'; while [ ! -f release-bash ]; do /bin/sleep 0.02; done; printf 'eof-bash-complete\\n'";
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--local", "--offline", "--session", sourcePath,
                "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;

            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();
            await WriteCommandAsync(process, new { id = "eof-bash", type = "bash", command }, timeout.Token);
            await ReadUntilAsync(process, lines, record =>
                record.GetProperty("type").GetString() == "bash_execution_update" &&
                record.GetProperty("delta").GetString()!.Contains("eof-bash-ready", StringComparison.Ordinal),
                timeout.Token);

            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line) lines.Add(line);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(timeout.Token));

            using var bashResponse = JsonDocument.Parse(Assert.Single(lines, line =>
            {
                using var record = JsonDocument.Parse(line);
                return record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "eof-bash" &&
                    record.RootElement.GetProperty("type").GetString() == "response";
            }));
            Assert.True(bashResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.True(bashResponse.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());
            Assert.DoesNotContain("eof-bash-complete", bashResponse.RootElement.GetProperty("data")
                .GetProperty("output").GetString(), StringComparison.Ordinal);

            var restoredSource = await store.LoadAsync(sourcePath);
            var originResult = Assert.Single(restoredSource.Tree.ActivePath(), entry =>
                entry.Type == "bash_execution" && entry.Payload.GetProperty("command").GetString() == command);
            Assert.True(originResult.Payload.GetProperty("cancelled").GetBoolean());
            Assert.DoesNotContain("eof-bash-complete", originResult.Payload.GetProperty("output").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("switch_session")]
    [InlineData("new_session")]
    [InlineData("fork")]
    [InlineData("clone")]
    public async Task BlockedBashIsCancelledAndSettledOnlyInItsOriginAfterSessionTransition(string transition)
    {
        if (!OperatingSystem.IsLinux()) return;

        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-bash-owner-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(sessionDirectory);

        var store = new ConversationStore(root, sessionDirectory);
        var source = new ConversationSession(root, ConnectionSettings.LocalModel,
            new Uri(ConnectionSettings.LocalEndpoint).ToString(), "local");
        source.Append(new ChatMessage(ChatRole.User, "origin-session-marker"));
        var sourcePath = store.NewPath(source);
        await store.SaveAsync(source, sourcePath);

        var targetPath = Path.Combine(sessionDirectory, "switch-target.session.json");
        if (transition == "switch_session")
        {
            var target = new ConversationSession(root, ConnectionSettings.LocalModel,
                new Uri(ConnectionSettings.LocalEndpoint).ToString(), "local");
            target.Append(new ChatMessage(ChatRole.User, "target-session-marker"));
            await store.SaveAsync(target, targetPath);
        }

        var gatePath = Path.Combine(root, "release-bash");
        var command = $"printf 'ownership-bash-ready\\n'; while [ ! -f {ProcessTestHelpers.ShellQuote(gatePath)} ]; do /bin/sleep 0.01; done; printf 'ownership-bash-complete\\n'";
        Process? process = null;

        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--local", "--offline", "--session", sourcePath,
                "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;

            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stderr = process.StandardError.ReadToEndAsync();
            var lines = new List<string>();

            await WriteCommandAsync(process, new { id = "owner-fork-messages", type = "get_fork_messages" }, timeout.Token);
            using var forkMessages = await ReadResponseAsync(process, lines, "owner-fork-messages", timeout.Token);
            var sourceEntryId = Assert.Single(forkMessages.RootElement.GetProperty("data").GetProperty("messages")
                .EnumerateArray()).GetProperty("entryId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sourceEntryId));

            await WriteCommandAsync(process, new { id = "owner-bash", type = "bash", command }, timeout.Token);
            await ReadUntilAsync(process, lines, record =>
                record.GetProperty("type").GetString() == "bash_execution_update" &&
                record.GetProperty("delta").GetString()!.Contains("ownership-bash-ready", StringComparison.Ordinal),
                timeout.Token);

            object transitionCommand = transition switch
            {
                "switch_session" => new { id = "owner-transition", type = transition, sessionPath = targetPath },
                "fork" => new { id = "owner-transition", type = transition, entryId = sourceEntryId },
                _ => new { id = "owner-transition", type = transition }
            };
            await WriteCommandAsync(process, transitionCommand, timeout.Token);
            using var transitionResponse = await ReadResponseAsync(process, lines, "owner-transition", timeout.Token);
            Assert.True(transitionResponse.RootElement.GetProperty("success").GetBoolean());

            using var bashResponse = await ReadResponseAsync(process, lines, "owner-bash", timeout.Token);
            Assert.True(bashResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.True(bashResponse.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());
            Assert.DoesNotContain("ownership-bash-complete",
                bashResponse.RootElement.GetProperty("data").GetProperty("output").GetString(), StringComparison.Ordinal);
            Assert.Single(lines, line =>
            {
                using var record = JsonDocument.Parse(line);
                return record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "owner-bash" &&
                    record.RootElement.GetProperty("type").GetString() == "response";
            });
            Assert.True(IndexOfResponse(lines, "owner-bash") < IndexOfResponse(lines, "owner-transition"),
                "Pinned Pi settles the cancelled Bash response before the session transition response.");

            const string targetCommand = "printf target-session-marker";
            await WriteCommandAsync(process, new { id = "owner-target-bash", type = "bash", command = targetCommand }, timeout.Token);
            using var targetBashResponse = await ReadResponseAsync(process, lines, "owner-target-bash", timeout.Token);
            Assert.True(targetBashResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("target-session-marker", targetBashResponse.RootElement.GetProperty("data")
                .GetProperty("output").GetString());

            await WriteCommandAsync(process, new { id = "owner-current-state", type = "get_state" }, timeout.Token);
            using var currentState = await ReadResponseAsync(process, lines, "owner-current-state", timeout.Token);
            var targetSessionPath = currentState.RootElement.GetProperty("data").GetProperty("sessionFile").GetString();
            Assert.False(string.IsNullOrWhiteSpace(targetSessionPath));
            Assert.NotEqual(Path.GetFullPath(sourcePath), Path.GetFullPath(targetSessionPath!));

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await stderr.WaitAsync(timeout.Token));

            var restoredSource = await store.LoadAsync(sourcePath);
            var originResults = restoredSource.Tree.ActivePath()
                .Where(entry => entry.Type == "bash_execution" &&
                    entry.Payload.GetProperty("command").GetString() == command).ToArray();
            var originResult = Assert.Single(originResults);
            Assert.True(originResult.Payload.GetProperty("cancelled").GetBoolean());
            Assert.DoesNotContain("ownership-bash-complete", originResult.Payload.GetProperty("output").GetString(),
                StringComparison.Ordinal);

            var restoredTarget = await store.LoadAsync(targetSessionPath!);
            Assert.Contains(restoredTarget.Tree.Entries, entry =>
                entry.Type == "bash_execution" && entry.Payload.GetProperty("command").GetString() == targetCommand);
            Assert.DoesNotContain(restoredTarget.Tree.Entries, entry =>
                entry.Type == "bash_execution" && entry.Payload.GetProperty("command").GetString() == command);
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

    private static async Task WriteCommandAsync(Process process, object command, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process, ICollection<string> lines, string id,
        CancellationToken cancellationToken)
    {
        foreach (var existing in lines)
        {
            using var record = JsonDocument.Parse(existing);
            if (record.RootElement.GetProperty("type").GetString() == "response" &&
                record.RootElement.TryGetProperty("id", out var responseId) && responseId.GetString() == id)
                return JsonDocument.Parse(existing);
        }
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("type").GetString() == "response" &&
                record.RootElement.TryGetProperty("id", out var responseId) && responseId.GetString() == id)
                return JsonDocument.Parse(line);
        }
        throw new EndOfStreamException($"The RPC process exited before returning response '{id}'.");
    }

    private static int IndexOfResponse(IReadOnlyList<string> lines, string id)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            using var record = JsonDocument.Parse(lines[index]);
            if (record.RootElement.GetProperty("type").GetString() == "response" &&
                record.RootElement.TryGetProperty("id", out var responseId) && responseId.GetString() == id)
                return index;
        }
        return -1;
    }

    private static async Task ReadUntilAsync(Process process, ICollection<string> lines, Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            using var record = JsonDocument.Parse(line);
            if (predicate(record.RootElement)) return;
        }
        throw new EndOfStreamException("The RPC process exited before writing the expected Bash update.");
    }
}
