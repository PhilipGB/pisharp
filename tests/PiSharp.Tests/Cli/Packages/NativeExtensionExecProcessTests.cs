using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionExecProcessTests
{
    [Fact]
    public async Task ExternalCompiledPackageExecutesFromRpcCommandAndStartupEvent()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var package = await NativeExtensionExecPackageTests.CreatePackageAsync(fixture.Root, "1.0.0");
        var installed = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", package);
        Assert.True(installed.ExitCode == 0, installed.Output + installed.Error);
        var eventLog = Path.Combine(fixture.Root, "event.json");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in new[] { "--mode", "rpc", "--offline", "--no-session" }) start.ArgumentList.Add(argument);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_NATIVE_EXEC_EVENT_LOG"] = eventLog;
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":\"exec\",\"type\":\"prompt\",\"message\":\"/native-exec literal \\\"quoted\\\" 漢🙂\"}");
            await process.StandardInput.FlushAsync();
            var records = new List<string>();
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                records.Add(line);
                if (line.Contains("fixture-error", StringComparison.Ordinal) && line.Contains("literal", StringComparison.Ordinal)) break;
            }
            var captured = string.Join('\n', records);
            Assert.Contains("fixture-error", captured);
            Assert.Contains("literal", captured);
            await ProcessTestHelpers.WaitForFileAsync(eventLog, deadline.Token);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(eventLog, deadline.Token));
            Assert.Equal("event-output", result.RootElement.GetProperty("Stdout").GetString());
            Assert.Equal("event-error", result.RootElement.GetProperty("Stderr").GetString());
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(deadline.Token);
            await errors;
        }
    }
}
