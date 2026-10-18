using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionSettlementProcessTests
{
    [Theory]
    [InlineData("normal", false, 2)]
    [InlineData("normal", true, 3)]
    [InlineData("retry", false, 3)]
    [InlineData("tools", false, 3)]
    public async Task PackedBoundaryExtensionContinuesSettlesAndResumesDurableEntries(
        string model, bool continueSettlement, int expectedRequests)
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var package = await PackAsync(Path.Combine(fixture.Root, "package"));
        using (var archive = ZipFile.OpenRead(package))
        {
            var spec = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            using var reader = new StreamReader(spec.Open());
            var text = await reader.ReadToEndAsync();
            Assert.Contains("PiSharp.Extensions.Abstractions", text);
            Assert.DoesNotContain("PiSharp.Runtime", text);
            Assert.DoesNotContain("PiSharp.Cli", text);
        }
        var installation = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", package);
        Assert.Equal(0, installation.ExitCode);
        var sessions = Path.Combine(fixture.Root, "sessions");
        Directory.CreateDirectory(sessions);
        var log = Path.Combine(fixture.Root, "settlement.log");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using (var process = Start(fixture, sessions, log, model, continueSettlement))
        {
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await NativeExtensionStateProcessTests.RunModelPromptAsync(process, "boundary", "package prompt", timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await errors);
            }
            finally { await StopAsync(process); }
        }
        var records = await File.ReadAllLinesAsync(log, timeout.Token);
        Assert.Single(records, record => record == "agent-start");
        Assert.Single(records, record => record == "settled:False");
        Assert.Equal(expectedRequests, records.Count(record => record.StartsWith("request:", StringComparison.Ordinal)));
        var mutableIndices = Enumerable.Range(0, records.Length).Where(index =>
            records[index].StartsWith("mutable-turn:", StringComparison.Ordinal)).ToArray();
        var expectedTurns = expectedRequests - (model == "retry" ? 1 : 0);
        Assert.Equal(expectedTurns, mutableIndices.Length);
        // The compatible legacy observer still sees the failed retry attempt; mutable
        // boundaries present only the completed logical turn and commit once.
        Assert.Equal(expectedRequests, records.Count(record => record.StartsWith("legacy-turn:", StringComparison.Ordinal)));
        if (model == "retry") Assert.Contains("legacy-turn:0::0", records);
        foreach (var index in mutableIndices)
        {
            Assert.True(index > 0);
            Assert.StartsWith("legacy-turn:", records[index - 1]);
            var legacy = records[index - 1].Split(':');
            var mutable = records[index].Split(':');
            Assert.Equal(legacy[1], mutable[1]);
            Assert.NotEmpty(legacy[2]);
            Assert.Equal(legacy[3], mutable[3]);
        }
        Assert.Contains(records, record => record.StartsWith("request:", StringComparison.Ordinal) && record.Contains("boundary=True"));
        Assert.Contains("later-handler-sees-context", records);
        if (continueSettlement)
            Assert.Contains(records, record => record.StartsWith("request:", StringComparison.Ordinal) && record.EndsWith("settlement=True", StringComparison.Ordinal));
        if (model == "tools") Assert.Contains(records, record => record.StartsWith("mutable-turn:0:Completed:1", StringComparison.Ordinal));
        Assert.DoesNotContain(records, record => record.StartsWith("mutable-turn:", StringComparison.Ordinal) && record.Contains(":Error:"));
        var path = Assert.Single(Directory.GetFiles(sessions, "*.json"));
        var store = new ConversationStore(fixture.ProjectDirectory, sessions);
        var saved = await store.LoadAsync(path, timeout.Token);
        Assert.Single(saved.Tree.ActivePath(), entry => entry.Type == "extension_state" &&
            entry.Payload.GetProperty("customType").GetString() == "settlement-receipt");
        Assert.Single(saved.Tree.ActivePath(), entry => entry.Type == "custom_message" &&
            entry.Payload.GetProperty("piOriginalEntry").GetProperty("customType").GetString() == "settlement-context");
        Assert.Contains(saved.ContextMessages(), message => message.Text == "boundary package context");

        var resumedLog = Path.Combine(fixture.Root, "resumed.log");
        using (var resumed = Start(fixture, sessions, resumedLog, model, false, path))
        {
            var errors = resumed.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await resumed.StandardInput.WriteLineAsync("{\"id\":\"state\",\"type\":\"get_state\"}".AsMemory(), timeout.Token);
                await resumed.StandardInput.FlushAsync(timeout.Token);
                while (await resumed.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    using var record = JsonDocument.Parse(line);
                    if (record.RootElement.TryGetProperty("id", out var id) && id.GetString() == "state") break;
                }
                resumed.StandardInput.Close();
                await resumed.WaitForExitAsync(timeout.Token);
                Assert.True(resumed.ExitCode == 0, await errors);
            }
            finally { await StopAsync(resumed); }
        }
        Assert.Contains("resumed-receipts:1", await File.ReadAllLinesAsync(resumedLog, timeout.Token));
        var reloaded = await store.LoadAsync(path, timeout.Token);
        Assert.Equal(saved.Tree.Entries.Count, reloaded.Tree.Entries.Count);
    }

    private static Process Start(ManagedNpmFixture fixture, string sessions, string log, string model,
        bool continueSettlement, string? session = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in new[] { "--mode", "rpc", "--provider", "settlement_fixture", "--model", model,
                     "--offline", "--no-builtin-tools", "--session-dir", sessions }) start.ArgumentList.Add(argument);
        if (session is not null)
        {
            start.ArgumentList.Add("--session");
            start.ArgumentList.Add(session);
        }
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = sessions;
        start.Environment["SETTLEMENT_LOG"] = log;
        start.Environment["SETTLEMENT_CONTINUE"] = continueSettlement ? "1" : "0";
        foreach (var name in new[] { "PISHARP_MODEL", "PISHARP_BASE_URL", "PISHARP_SETTINGS_PATH", "PISHARP_MODELS_PATH", "PISHARP_AUTH_PATH" })
            start.Environment.Remove(name);
        return Process.Start(start) ?? throw new IOException("Could not start settlement process.");
    }

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    private static async Task<string> PackAsync(string output)
    {
        Directory.CreateDirectory(output);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("PiSharp.slnx not found.");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "pack", "tests/fixtures/NativeSettlementExtension/NativeSettlementExtension.csproj",
                     "--no-restore", "--output", output, "--nologo", "--disable-build-servers", "-m:1",
                     "-p:NuGetAudit=false" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not pack fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
        return Assert.Single(Directory.GetFiles(output, "*.nupkg"));
    }
}
