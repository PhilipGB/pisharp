using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcModelReplacementProcessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaleSaveRestoresSelectedModelThinkingAndHeadAndAllowsRetry(bool changeModel)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-model-rollback-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var endpoint = new Uri("http://127.0.0.1:1/v1");
        var store = new ConversationStore(root, sessionDirectory);
        var conversation = new ConversationSession(root, "first", endpoint.ToString(), "fixture");
        conversation.AppendThinkingLevelChange("high");
        var sessionPath = store.NewPath(conversation);
        await store.SaveAsync(conversation, sessionPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new
                {
                    fixture = new
                    {
                        baseUrl = endpoint.ToString(),
                        apiKeyEnv = "PISHARP_FIXTURE_KEY",
                        models = new[]
                        {
                            new { id = "first", api = "openai-completions", reasoning = true },
                            new { id = "second", api = "openai-completions", reasoning = false }
                        }
                    }
                }
            }));
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "first",
                "--thinking", "high", "--session", sessionPath, "--session-dir", sessionDirectory,
                "--offline", "--no-tools" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var records = new List<JsonElement>();
            async Task<JsonElement> SendAsync(string id, string type)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    id,
                    type,
                    provider = "fixture",
                    modelId = "second",
                    level = "low"
                }));
                await process.StandardInput.FlushAsync(timeout.Token);
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    Assert.NotNull(line);
                    using var document = JsonDocument.Parse(line);
                    var record = document.RootElement.Clone();
                    records.Add(record);
                    if (record.TryGetProperty("id", out var recordId) && recordId.GetString() == id)
                        return record;
                }
            }

            var beforeState = (await SendAsync("before-state", "get_state")).GetProperty("data");
            var beforeEntries = (await SendAsync("before-entries", "get_entries")).GetProperty("data");
            Assert.Equal("first", beforeState.GetProperty("model").GetProperty("id").GetString());
            Assert.Equal("high", beforeState.GetProperty("thinkingLevel").GetString());
            var savedBytes = await File.ReadAllBytesAsync(sessionPath, timeout.Token);
            await File.AppendAllTextAsync(sessionPath, "\n", timeout.Token);
            var staleBytes = await File.ReadAllBytesAsync(sessionPath, timeout.Token);

            var command = changeModel ? "set_model" : "set_thinking_level";
            var failureStart = records.Count;
            var failed = await SendAsync("failed-change", command);
            Assert.False(failed.GetProperty("success").GetBoolean());
            Assert.Equal("Session changed on disk; reopen before writing.", failed.GetProperty("error").GetString());
            Assert.DoesNotContain(records.Skip(failureStart), record =>
                record.GetProperty("type").GetString() == "thinking_level_changed");
            var afterState = (await SendAsync("after-state", "get_state")).GetProperty("data");
            var afterEntries = (await SendAsync("after-entries", "get_entries")).GetProperty("data");
            Assert.Equal("first", afterState.GetProperty("model").GetProperty("id").GetString());
            Assert.Equal("high", afterState.GetProperty("thinkingLevel").GetString());
            Assert.Equal(beforeState.GetProperty("sessionId").GetString(), afterState.GetProperty("sessionId").GetString());
            Assert.Equal(beforeEntries.GetProperty("leafId").GetString(), afterEntries.GetProperty("leafId").GetString());
            Assert.Equal(beforeEntries.GetProperty("entries").GetArrayLength() + (changeModel ? 2 : 1),
                afterEntries.GetProperty("entries").GetArrayLength());
            Assert.Equal(staleBytes, await File.ReadAllBytesAsync(sessionPath, timeout.Token));

            await File.WriteAllBytesAsync(sessionPath, savedBytes, timeout.Token);
            Assert.True((await SendAsync("retry-change", command)).GetProperty("success").GetBoolean());
            var retryState = (await SendAsync("retry-state", "get_state")).GetProperty("data");
            Assert.Equal(changeModel ? "second" : "first", retryState.GetProperty("model").GetProperty("id").GetString());
            Assert.Equal(changeModel ? "off" : "low", retryState.GetProperty("thinkingLevel").GetString());
            var persisted = await new ConversationStore(root, sessionDirectory).LoadAsync(sessionPath, timeout.Token);
            Assert.Equal(changeModel ? "second" : "first", persisted.Model);
            Assert.Equal(changeModel ? "off" : "low", PiJsonlSessionInterchange.GetThinkingLevel(persisted));
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
