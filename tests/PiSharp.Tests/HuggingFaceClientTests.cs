using System.Net;
using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class HuggingFaceClientTests
{
    [Fact]
    public async Task TokenResolutionUsesEnvironmentThenExplicitAndStandardCacheFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-hf-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tokenPath = Path.Combine(root, "token");
            var home = Path.Combine(root, "home");
            var xdg = Path.Combine(root, "xdg");
            Directory.CreateDirectory(Path.Combine(home, "huggingface"));
            Directory.CreateDirectory(Path.Combine(xdg, "huggingface"));
            await File.WriteAllTextAsync(tokenPath, " explicit-file-token \n");
            await File.WriteAllTextAsync(Path.Combine(home, "token"), "home-token");
            await File.WriteAllTextAsync(Path.Combine(xdg, "huggingface", "token"), "xdg-token");
            var environment = new Dictionary<string, string?>
            {
                ["HF_TOKEN"] = " env-token ",
                ["HF_TOKEN_PATH"] = tokenPath,
                ["HF_HOME"] = home,
                ["XDG_CACHE_HOME"] = xdg
            };

            Assert.Equal("env-token", await HuggingFaceClient.FindTokenAsync(name => environment.GetValueOrDefault(name)));
            environment["HF_TOKEN"] = " ";
            Assert.Equal("explicit-file-token", await HuggingFaceClient.FindTokenAsync(name => environment.GetValueOrDefault(name)));
            var oversizedToken = new string('x', 64 * 1024 + 1);
            await File.WriteAllTextAsync(tokenPath, oversizedToken);
            Assert.Equal(oversizedToken, await HuggingFaceClient.FindTokenAsync(name => environment.GetValueOrDefault(name)));
            await File.WriteAllTextAsync(tokenPath, " \n");
            Assert.Equal("home-token", await HuggingFaceClient.FindTokenAsync(name => environment.GetValueOrDefault(name)));
            environment["HF_TOKEN_PATH"] = Path.Combine(root, "missing-token");
            Assert.Equal("home-token", await HuggingFaceClient.FindTokenAsync(name => environment.GetValueOrDefault(name)));
            environment["HF_TOKEN_PATH"] = null;
            environment["HF_HOME"] = null;
            Assert.Equal("xdg-token", await HuggingFaceClient.FindTokenAsync(name => environment.GetValueOrDefault(name)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SearchUsesGgufDownloadRankingAndBearerCredential()
    {
        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new Handler(request =>
        {
            captured = request;
            return Json("""
                [{"id":"owner/model","downloads":2500},{"name":"invalid"},{"id":"owner/other"}]
                """);
        }));
        var client = new HuggingFaceClient(http, "hf-secret", new Uri("https://hf.test/"));

        var results = await client.SearchAsync("qwen 3", CancellationToken.None);

        Assert.Equal(["owner/model", "owner/other"], results.Select(item => item.Id));
        Assert.Equal(2500, results[0].Downloads);
        Assert.Equal("Bearer hf-secret", captured!.Headers.Authorization!.ToString());
        Assert.Contains("search=qwen+3", captured.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("filter=gguf", captured.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("direction=-1", captured.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("limit=20", captured.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailsDetectGatedAccessAndAggregateSortedQuantizationShards()
    {
        using var http = new HttpClient(new Handler(_ => Json("""
            {
              "id":"owner/model",
              "gated":"manual",
              "siblings":[
                {"rfilename":"model-Q8_0.gguf","size":2000},
                {"rfilename":"model-Q4_K_M-00001-of-00002.gguf","size":100},
                {"rfilename":"model-Q4_K_M-00002-of-00002.gguf","size":150},
                {"rfilename":"mmproj-model-Q5_K_M.gguf","size":700},
                {"rfilename":"model-F16.gguf"},
                {"rfilename":"notes.txt","size":5}
              ]
            }
            """)));
        var client = new HuggingFaceClient(http, null, new Uri("https://hf.test/"));

        var details = await client.GetDetailsAsync("owner/model", CancellationToken.None);

        Assert.Equal("owner/model", details.Id);
        Assert.Equal("manual", details.Gated);
        Assert.Equal(["Q4_K_M", "Q8_0", "F16"], details.Quantizations.Select(item => item.Name));
        Assert.Equal(250, details.Quantizations[0].Size);
        Assert.Equal(2000, details.Quantizations[1].Size);
        Assert.Null(details.Quantizations[2].Size);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
