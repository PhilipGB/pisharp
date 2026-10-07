using System.Net;
using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class LlamaRouterProviderTests
{
    [Fact]
    public async Task EnvironmentConfiguredRouterProjectsLiveModelsAndUsesInferenceEndpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var requests = new List<(string Method, string Path, string? Authorization)>();
            var handler = new RouterHandler(request =>
            {
                requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString()));
                return request.RequestUri.PathAndQuery switch
                {
                    "/models" => Json("""
                        {"data":[
                          {"id":"qwen","status":{"value":"loaded","args":["llama-server","--ctx-size","8192"]},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"source":"local","meta":{"n_ctx":32768,"n_ctx_train":65536}},
                          {"id":"sleeping","status":{"value":"sleeping"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"meta":{"n_ctx":16384}},
                          {"id":"autoload-preset","status":{"value":"unloaded","args":["llama-server","-c","4096"]},"source":"preset","meta":{"n_ctx_train":32768}},
                          {"id":"not-autoloaded","status":{"value":"unloaded"},"source":"file","meta":{"n_ctx_train":8192}},
                          {"id":"decision-only","status":{"value":"loaded"},"architecture":{"input_modalities":["text"],"output_modalities":["decisions"]},"meta":{"n_ctx":2048}}
                        ]}
                        """),
                    "/props" => Json("""{"models_autoload":true}"""),
                    "/props?model=qwen&autoload=false" => Json("""{"chat_template":"{% if enable_thinking %}think{% endif %}"}"""),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            });
            using var http = new HttpClient(handler);
            var environment = new Dictionary<string, string?>
            {
                ["LLAMA_BASE_URL"] = "http://llama.test:8080/v1/",
                ["LLAMA_API_KEY"] = "router-secret"
            };
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http);

            var models = await runtime.ListModelsAsync("llama.cpp");

            Assert.Equal(["qwen", "sleeping", "autoload-preset"], models.Select(model => model.Id));
            var qwen = Assert.Single(models, model => model.Id == "qwen");
            Assert.Equal("llama.cpp", qwen.Provider);
            Assert.Equal("openai-completions", qwen.Api);
            Assert.Equal("http://llama.test:8080/v1", qwen.BaseUrl);
            Assert.Equal(32768, qwen.ContextLength);
            Assert.Equal(32768, qwen.MaxOutputTokens);
            Assert.Equal(["text", "image"], qwen.Input);
            Assert.True(qwen.Reasoning);
            Assert.Equal("qwen-chat-template", qwen.Compatibility?.GetProperty("thinkingFormat").GetString());

            var selection = await runtime.ResolveAsync("llama.cpp", "qwen");
            Assert.Equal("router-secret", selection.ApiKey);
            Assert.Equal(new Uri("http://llama.test:8080/v1"), selection.Connection.Endpoint);
            Assert.Equal("openai-completions", ProviderChatClientFactory.ResolveProtocol(selection));

            var classifiers = runtime.GetProvider("llama.cpp").Classifiers!;
            Assert.Contains(classifiers, model => model.Id == "qwen" && model.Api == "llama-cpp-classify" &&
                model.BaseUrl == new Uri("http://llama.test:8080"));
            Assert.Contains(classifiers, model => model.Id == "decision-only" && model.Api == "typesafe-system-one" &&
                model.BaseUrl == new Uri("http://llama.test:8080/v1"));
            Assert.Contains(requests, request => request.Path == "/models" && request.Authorization == "Bearer router-secret");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RouterKeyDoesNotAuthenticateWithoutConfiguredServerUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var requests = 0;
            using var http = new HttpClient(new RouterHandler(_ =>
            {
                requests++;
                throw new InvalidOperationException("A router URL was not configured.");
            }));
            var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                name => name == "LLAMA_API_KEY" ? "environment-key" : null, http,
                runtimeApiKey: "command-line-key");

            var auth = await runtime.ResolveAuthAsync("llama.cpp", useRuntimeOverride: true);
            var models = await runtime.ListModelsAsync("llama.cpp");

            Assert.False(auth.Authenticated);
            Assert.Equal("LLAMA_BASE_URL is not configured", auth.Source);
            Assert.Empty(models);
            Assert.Equal(0, requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CodemodeClassifierAvailabilityRefreshesLiveRouterIncludingNativeDecisionModels()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-codemode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var requests = new List<(string Path, string? Authorization)>();
            using var http = new HttpClient(new RouterHandler(request =>
            {
                requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));
                return request.RequestUri.PathAndQuery switch
                {
                    "/models" => Json("""
                        {"data":[
                          {"id":"chat-model","status":{"value":"loaded"},"architecture":{"output_modalities":["text"]}},
                          {"id":"decision-model","status":{"value":"loaded"},"architecture":{"output_modalities":["decisions"]}}
                        ]}
                        """),
                    "/props?model=chat-model&autoload=false" => Json("{}"),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }));
            var environment = new Dictionary<string, string?>
            {
                ["LLAMA_BASE_URL"] = "http://llama.test:8080/v1",
                ["LLAMA_API_KEY"] = "router-secret"
            };
            var providers = await ProviderModelRuntime.CreateAsync(root, false,
                name => environment.GetValueOrDefault(name), http);
            var models = await new ProviderCodemodeModels(providers, http)
                .GetAvailableAsync("classifier", "llama.cpp", CancellationToken.None);

            var chat = Assert.Single(models, model => model.GetProperty("id").GetString() == "chat-model");
            Assert.Equal("llama-cpp-classify", chat.GetProperty("api").GetString());
            Assert.Equal("http://llama.test:8080/", chat.GetProperty("baseUrl").GetString());
            var decision = Assert.Single(models, model => model.GetProperty("id").GetString() == "decision-model");
            Assert.Equal("typesafe-system-one", decision.GetProperty("api").GetString());
            Assert.Equal("http://llama.test:8080/v1", decision.GetProperty("baseUrl").GetString());
            Assert.Contains(requests, request => request.Path == "/models" &&
                request.Authorization == "Bearer router-secret");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RouterLoginValidatesAtRouterRootWithoutInventingAnOptionalBearerKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string? path = null;
            string? authorization = "not-checked";
            using var http = new HttpClient(new RouterHandler(request =>
            {
                path = request.RequestUri!.PathAndQuery;
                authorization = request.Headers.Authorization?.ToString();
                return Json("""{"data":[]}""");
            }));
            var providers = await ProviderModelRuntime.CreateAsync(root, false, _ => null, http);

            await providers.LoginLlamaRouterAsync(null, "http://llama.test:8080/v1/");

            Assert.Equal("/models", path);
            Assert.Null(authorization);
            var credential = await new AuthStorage(Path.Combine(root, "auth.json")).ReadAsync("llama.cpp");
            Assert.Null(credential?.Key);
            Assert.Equal("http://llama.test:8080", credential?.Env?["LLAMA_BASE_URL"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("http://llama.test:8080/router/v1/", "http://llama.test:8080/router")]
    [InlineData("http://llama.test:8080/router/V1/", "http://llama.test:8080/router/V1")]
    public void RouterRootNormalizationMatchesCurrentPiCaseSensitiveVersionSuffix(string value, string expected) =>
        Assert.Equal(new Uri(expected), LlamaRouterClient.NormalizeServerUrl(value));

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class RouterHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
