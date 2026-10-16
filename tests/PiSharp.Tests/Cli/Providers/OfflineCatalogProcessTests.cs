using System.Diagnostics;
using System.Net;
using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class OfflineCatalogProcessTests
{
    [Fact]
    public async Task ProviderModelListDiscoversDynamicRouterModelsWithoutSelectingOneFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-router-model-list-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        var (listener, port) = TestLoopbackListener.Start();
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var requests = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync().WaitAsync(serverCancellation.Token);
                requests.Add(context.Request.Url!.AbsolutePath + context.Request.Url.Query);
                var body = context.Request.Url.AbsolutePath == "/models"
                    ? """{"data":[{"id":"loaded-router-model","status":{"value":"loaded"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]}}]}"""
                    : "{}";
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, serverCancellation.Token);
                context.Response.Close();
            }
        }, serverCancellation.Token);

        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "--provider", "llama.cpp", "--list-models" })
                start.ArgumentList.Add(argument);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["LLAMA_BASE_URL"] = $"http://127.0.0.1:{port}";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            await server;

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await errors);
            Assert.StartsWith("llama.cpp/loaded-router-model\t", await output);
            Assert.Equal(["/models", "/props?model=loaded-router-model&autoload=false"], requests);
        }
        finally
        {
            serverCancellation.Cancel();
            listener.Close();
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, "STATIC", false)]
    [InlineData(true, "STATIC", false)]
    [InlineData(false, "missing", false)]
    [InlineData(false, "fixture", true)]
    public async Task OfflineListModelsUsesStaticMetadataWithoutReachingUnavailableEndpoint(bool viaEnvironment, string pattern, bool settingScope)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-offline-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","authHeader":false,"models":[{"id":"static-only","contextWindow":8192},{"id":"omit-me"}]}}}
                """);
            if (settingScope) await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), """
                {"enabledModels":["fixture/static-only"]}
                """);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "--provider", "fixture", "--list-models", pattern })
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
            if (pattern == "missing")
                Assert.Equal("No models matching \"missing\"\n", await output);
            else
            {
                Assert.Contains("fixture/static-only\tconfigured\t8192", await output);
                if (settingScope) Assert.Contains("fixture/omit-me", await output);
                else Assert.DoesNotContain("omit-me", await output);
            }
            Assert.Equal("STATIC", CliArguments.Parse(["--list-models", "STATIC"]).ListModelsFilter);
            Assert.Null(CliArguments.Parse(["--list-models", "--offline"]).ListModelsFilter);
        }
        finally { Directory.Delete(root, true); }
    }
}
