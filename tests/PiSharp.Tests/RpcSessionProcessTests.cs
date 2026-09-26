using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcSessionProcessTests
{
    [Fact]
    public async Task NewSessionSwitchesTheActiveRpcSessionAndTracksItsParent()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-new-session-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(agentDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            foreach (var argument in new[] { "--mode", "rpc", "--local", "--offline", "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"new\",\"type\":\"new_session\",\"parentSession\":\"/sessions/parent.jsonl\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"state\",\"type\":\"get_state\"}");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error.WaitAsync(timeout.Token));
            var responses = (await output.WaitAsync(timeout.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var created = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "new");
                Assert.True(created.RootElement.GetProperty("success").GetBoolean());
                Assert.False(created.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());
                var state = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "state");
                Assert.True(state.RootElement.GetProperty("success").GetBoolean());
                var sessionId = state.RootElement.GetProperty("data").GetProperty("sessionId").GetString();
                Assert.Equal(2, Directory.GetFiles(sessionDirectory, "*.session.json").Length);
                var store = new ConversationStore(root, sessionDirectory);
                var createdSession = await store.LoadAsync(Assert.Single(Directory.GetFiles(sessionDirectory, "*.session.json",
                    SearchOption.TopDirectoryOnly), path =>
                    ConversationSession.Parse(File.ReadAllText(path)).Id == sessionId));
                Assert.Equal("/sessions/parent.jsonl", createdSession.ParentSessionPath);
            }
            finally { foreach (var response in responses) response.Dispose(); }
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
}
