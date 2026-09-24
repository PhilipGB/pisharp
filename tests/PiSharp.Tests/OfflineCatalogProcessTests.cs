using System.Diagnostics;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class OfflineCatalogProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineListModelsUsesStaticMetadataWithoutReachingUnavailableEndpoint(bool viaEnvironment)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-offline-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","authHeader":false,"models":[{"id":"static-only","contextWindow":8192}]}}}
                """);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "--provider", "fixture", "--list-models" })
                start.ArgumentList.Add(argument);
            if (viaEnvironment) start.Environment["PI_OFFLINE"] = "yes";
            else { start.Environment.Remove("PI_OFFLINE"); start.ArgumentList.Add("--offline"); }
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await errors);
            Assert.Contains("fixture/static-only\tconfigured\t8192", await output);
        }
        finally { Directory.Delete(root, true); }
    }
}
