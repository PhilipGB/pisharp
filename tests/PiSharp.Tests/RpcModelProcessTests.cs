using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcModelProcessTests
{
    [Fact]
    public async Task RpcStartupRestoresThinkingLevelFromTheSelectedSessionBranch()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-thinking-restore-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-reasoning","reasoning":true}]}}}
            """);
        var sessionPath = Path.Combine(root, "restore.session.json");
        var session = new ConversationSession(root, "fixture-reasoning", "http://127.0.0.1:1/v1", "fixture");
        session.AppendThinkingLevelChange("max");
        await File.WriteAllTextAsync(sessionPath, session.ToJson());
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
            foreach (var argument in new[] { "--mode", "rpc", "--session", sessionPath, "--offline" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stderr = process.StandardError.ReadToEndAsync();
            await WriteCommandAsync(process, new { id = "restored-state", type = "get_state" }, timeout.Token);
            var lines = await ReadResponsesAsync(process, ["restored-state"], timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            using var response = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"restored-state\"", StringComparison.Ordinal)));
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            var state = response.RootElement.GetProperty("data");
            Assert.Equal("fixture-reasoning", state.GetProperty("model").GetString());
            Assert.Equal("max", state.GetProperty("thinkingLevel").GetString());
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
    public async Task RpcModelAndThinkingCommandsSelectAndCycleTheRunningRuntime()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-model-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","reasoning":false},{"id":"fixture-next","reasoning":false},{"id":"fixture-reasoning","reasoning":true}]}}}
            """);
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "fixture-model", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stderr = process.StandardError.ReadToEndAsync();
            await WriteCommandAsync(process, new
            {
                id = "switch",
                type = "set_model",
                provider = "fixture",
                modelId = "fixture-reasoning"
            }, timeout.Token);
            await WriteCommandAsync(process, new
            {
                id = "cycle",
                type = "cycle_model"
            }, timeout.Token);
            await WriteCommandAsync(process, new
            {
                id = "unknown",
                type = "set_model",
                provider = "fixture",
                modelId = "not-configured"
            }, timeout.Token);
            await WriteCommandAsync(process, new { id = "state", type = "get_state" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "unsupported-thinking", type = "set_thinking_level", level = "high" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "cycle-no-thinking", type = "cycle_thinking_level" }, timeout.Token);
            await WriteCommandAsync(process, new
            {
                id = "reasoning-model",
                type = "set_model",
                provider = "fixture",
                modelId = "fixture-reasoning"
            }, timeout.Token);
            await WriteCommandAsync(process, new { id = "thinking-levels", type = "get_available_thinking_levels" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "set-max", type = "set_thinking_level", level = "max" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "state-max", type = "get_state" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "thinking-entries", type = "get_entries" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "cycle-thinking", type = "cycle_thinking_level" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "state-off", type = "get_state" }, timeout.Token);
            var lines = await ReadResponsesAsync(process,
                ["switch", "cycle", "unknown", "state", "unsupported-thinking", "cycle-no-thinking", "reasoning-model",
                    "thinking-levels", "set-max", "state-max", "thinking-entries", "cycle-thinking", "state-off"], timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            using var switched = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"switch\"", StringComparison.Ordinal)));
            Assert.True(switched.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("fixture-reasoning", switched.RootElement.GetProperty("data").GetProperty("id").GetString());
            Assert.Equal("fixture", switched.RootElement.GetProperty("data").GetProperty("provider").GetString());
            using var cycled = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"cycle\"", StringComparison.Ordinal)));
            Assert.True(cycled.RootElement.GetProperty("success").GetBoolean());
            var cycleData = cycled.RootElement.GetProperty("data");
            Assert.Equal("fixture-model", cycleData.GetProperty("model").GetProperty("id").GetString());
            Assert.Equal("fixture", cycleData.GetProperty("model").GetProperty("provider").GetString());
            Assert.Equal("off", cycleData.GetProperty("thinkingLevel").GetString());
            Assert.False(cycleData.GetProperty("isScoped").GetBoolean());
            using var unknown = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"unknown\"", StringComparison.Ordinal)));
            Assert.False(unknown.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("Model not found: fixture/not-configured", unknown.RootElement.GetProperty("error").GetString());
            using var state = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"state\"", StringComparison.Ordinal)));
            Assert.Equal("fixture-model", state.RootElement.GetProperty("data").GetProperty("model").GetString());
            Assert.Equal("off", state.RootElement.GetProperty("data").GetProperty("thinkingLevel").GetString());
            using var unsupportedThinking = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"unsupported-thinking\"", StringComparison.Ordinal)));
            Assert.True(unsupportedThinking.RootElement.GetProperty("success").GetBoolean());
            using var noThinking = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"cycle-no-thinking\"", StringComparison.Ordinal)));
            Assert.Equal(JsonValueKind.Null, noThinking.RootElement.GetProperty("data").ValueKind);
            using var reasoningModel = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"reasoning-model\"", StringComparison.Ordinal)));
            Assert.Equal("fixture-reasoning", reasoningModel.RootElement.GetProperty("data").GetProperty("id").GetString());
            using var levels = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"thinking-levels\"", StringComparison.Ordinal)));
            Assert.Equal(["off", "minimal", "low", "medium", "high", "xhigh", "max"],
                levels.RootElement.GetProperty("data").GetProperty("levels").EnumerateArray().Select(level => level.GetString()));
            using var setMax = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"set-max\"", StringComparison.Ordinal)));
            Assert.True(setMax.RootElement.GetProperty("success").GetBoolean());
            Assert.False(setMax.RootElement.TryGetProperty("data", out _));
            using var maxState = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"state-max\"", StringComparison.Ordinal)));
            Assert.Equal("max", maxState.RootElement.GetProperty("data").GetProperty("thinkingLevel").GetString());
            using var entries = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"thinking-entries\"", StringComparison.Ordinal)));
            Assert.Contains(entries.RootElement.GetProperty("data").GetProperty("entries").EnumerateArray(), entry =>
                entry.GetProperty("Type").GetString() == "thinking_level_change" &&
                entry.GetProperty("Payload").GetProperty("thinkingLevel").GetString() == "max");
            using var cycledThinking = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"cycle-thinking\"", StringComparison.Ordinal)));
            Assert.Equal("off", cycledThinking.RootElement.GetProperty("data").GetProperty("level").GetString());
            using var offState = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"state-off\"", StringComparison.Ordinal)));
            Assert.Equal("off", offState.RootElement.GetProperty("data").GetProperty("thinkingLevel").GetString());
            Assert.DoesNotContain(lines, line => line.Contains("\"type\":\"error\"", StringComparison.Ordinal));
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

    private static async Task<List<string>> ReadResponsesAsync(Process process, IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var remaining = ids.ToHashSet(StringComparer.Ordinal);
        var lines = new List<string>();
        while (remaining.Count > 0)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException("RPC process closed stdout before returning all command responses.");
            lines.Add(line);
            using var record = JsonDocument.Parse(line);
            var root = record.RootElement;
            if (root.GetProperty("type").GetString() == "response" && root.TryGetProperty("id", out var id))
                remaining.Remove(id.GetString() ?? "");
        }
        return lines;
    }
}
