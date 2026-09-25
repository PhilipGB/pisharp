using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcModelProcessTests
{
    [Fact]
    public async Task RpcSetModelReplacesTheRunningProviderRuntime()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-model-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","reasoning":false},{"id":"fixture-next","reasoning":false}]}}}
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
                modelId = "fixture-next"
            }, timeout.Token);
            await WriteCommandAsync(process, new
            {
                id = "unknown",
                type = "set_model",
                provider = "fixture",
                modelId = "not-configured"
            }, timeout.Token);
            await WriteCommandAsync(process, new { id = "state", type = "get_state" }, timeout.Token);
            var lines = await ReadResponsesAsync(process, ["switch", "unknown", "state"], timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            using var switched = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"switch\"", StringComparison.Ordinal)));
            Assert.True(switched.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("fixture-next", switched.RootElement.GetProperty("data").GetProperty("id").GetString());
            Assert.Equal("fixture", switched.RootElement.GetProperty("data").GetProperty("provider").GetString());
            using var unknown = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"unknown\"", StringComparison.Ordinal)));
            Assert.False(unknown.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("Model not found: fixture/not-configured", unknown.RootElement.GetProperty("error").GetString());
            using var state = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"state\"", StringComparison.Ordinal)));
            Assert.Equal("fixture-next", state.RootElement.GetProperty("data").GetProperty("model").GetString());
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
