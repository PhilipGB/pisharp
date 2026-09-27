using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcThinkingLevelMapProcessTests
{
    [Fact]
    public async Task ThinkingCommandsExposeMappedLevelsAndClampWithinTheCurrentModelInTheCliProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-thinking-map-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[
             {"id":"mapped","api":"openai-completions","reasoning":true,"thinkingLevelMap":{"off":null,"high":null,"xhigh":null,"max":"max"}}]}}}
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
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "mapped", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stderr = process.StandardError.ReadToEndAsync();

            await WriteAsync(process, new { id = "available", type = "get_available_thinking_levels" }, timeout.Token);
            var availableLines = await ReadThroughResponseAsync(process, "available", timeout.Token);
            using var available = JsonDocument.Parse(availableLines.Single(line => line.Contains("\"id\":\"available\"", StringComparison.Ordinal)));
            Assert.Equal(["minimal", "low", "medium", "max"],
                available.RootElement.GetProperty("data").GetProperty("levels").EnumerateArray().Select(item => item.GetString()));

            await WriteAsync(process, new { id = "set", type = "set_thinking_level", level = "xhigh" }, timeout.Token);
            var setLines = await ReadThroughResponseAsync(process, "set", timeout.Token);
            var setEvent = setLines.FindIndex(line => line.Contains("\"type\":\"thinking_level_changed\"", StringComparison.Ordinal));
            var setResponse = setLines.FindIndex(line => line.Contains("\"id\":\"set\"", StringComparison.Ordinal));
            Assert.True(setEvent >= 0 && setEvent < setResponse);

            await WriteAsync(process, new { id = "state", type = "get_state" }, timeout.Token);
            var stateLines = await ReadThroughResponseAsync(process, "state", timeout.Token);
            using var state = JsonDocument.Parse(stateLines.Single(line => line.Contains("\"id\":\"state\"", StringComparison.Ordinal)));
            Assert.Equal("max", state.RootElement.GetProperty("data").GetProperty("thinkingLevel").GetString());

            await WriteAsync(process, new { id = "cycle", type = "cycle_thinking_level" }, timeout.Token);
            _ = await ReadThroughResponseAsync(process, "cycle", timeout.Token);
            await WriteAsync(process, new { id = "after-cycle", type = "get_state" }, timeout.Token);
            var afterCycleLines = await ReadThroughResponseAsync(process, "after-cycle", timeout.Token);
            using var afterCycle = JsonDocument.Parse(afterCycleLines.Single(line => line.Contains("\"id\":\"after-cycle\"", StringComparison.Ordinal)));
            Assert.Equal("minimal", afterCycle.RootElement.GetProperty("data").GetProperty("thinkingLevel").GetString());

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
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

    private static Task WriteAsync(Process process, object command, CancellationToken cancellationToken) =>
        process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), cancellationToken);

    private static async Task<List<string>> ReadThroughResponseAsync(Process process, string id,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            if (line.Contains($"\"id\":\"{id}\"", StringComparison.Ordinal)) return lines;
        }
        throw new EndOfStreamException($"RPC process exited before response '{id}'.");
    }
}
