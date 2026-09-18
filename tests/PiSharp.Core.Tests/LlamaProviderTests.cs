using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Core.Tests;

/// <summary>
/// llama.cpp provider tests: server URL normalization, router model mapping, the dynamic
/// catalog refresh against a live in-process router, auth resolution/login, and the
/// Hugging Face discovery client used by /llama.
/// </summary>
public sealed class LlamaProviderTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/v1/", "http://127.0.0.1:8080")]
    [InlineData("http://host:9999/a/b", "http://host:9999/a/b")]
    [InlineData("https://h.example.com/p?x=1#frag", "https://h.example.com/p")]
    public void NormalizeLlamaServerUrlStripsV1AndFrills(string input, string expected)
    {
        Assert.Equal(expected, LlamaUrls.Normalize(input));
    }

    [Fact]
    public void NormalizeLlamaServerUrlRejectsNonHttpSchemes()
    {
        var error = Assert.Throws<InvalidOperationException>(() => LlamaUrls.Normalize("ftp://host:21/x"));
        Assert.Equal("Server URL must use http or https", error.Message);
    }

    [Fact]
    public void InferenceUrlAppendsV1ToNormalizedServer()
    {
        Assert.Equal("http://127.0.0.1:8080/v1", LlamaUrls.InferenceUrl("http://127.0.0.1:8080/v1/"));
    }

    [Fact]
    public void ToPiModelUsesGgufMetadataAndZeroCost()
    {
        var model = BuiltinProviders.LlamaToPiModel(new LlamaModelInfo
        {
            Id = "model-a",
            Status = new LlamaModelStatus { Value = LlamaModelStatusValues.Loaded },
            Meta = new LlamaModelMeta { NCtx = 4096 },
            Architecture = new LlamaArchitecture { InputModalities = ["text", "image"] },
        }, "http://127.0.0.1:8080");

        Assert.Equal("model-a", model.Id);
        Assert.Equal("llama.cpp", model.Provider);
        Assert.Equal("http://127.0.0.1:8080/v1", model.BaseUrl);
        Assert.Equal(4096, model.ContextWindow);
        Assert.Equal(4096, model.MaxTokens);
        Assert.Equal(["text", "image"], model.Input);
        Assert.False(model.Reasoning);
        Assert.Equal(0, model.Cost.Input);
        Assert.Equal(0, model.Cost.Output);
    }

    [Fact]
    public void ToPiModelFallsBackTo128kWithoutMetadata()
    {
        var model = BuiltinProviders.LlamaToPiModel(new LlamaModelInfo
        {
            Id = "bare",
            Status = new LlamaModelStatus { Value = LlamaModelStatusValues.Unloaded },
        }, "http://127.0.0.1:8080");

        Assert.Equal(128_000, model.ContextWindow);
        Assert.Equal(["text"], model.Input);
    }

    [Theory]
    [InlineData(LlamaModelStatusValues.Loaded, "preset", false, false, true)]
    [InlineData(LlamaModelStatusValues.Sleeping, "file", false, false, true)]
    [InlineData(LlamaModelStatusValues.Unloaded, "preset", false, true, true)]
    [InlineData(LlamaModelStatusValues.Unloaded, "preset", false, false, false)]
    [InlineData(LlamaModelStatusValues.Unloaded, "preset", true, true, false)]
    [InlineData(LlamaModelStatusValues.Unloaded, "file", false, true, false)]
    [InlineData(LlamaModelStatusValues.Downloading, "file", false, true, false)]
    public void ModelIsSelectableFollowsPinnedRules(
        string status,
        string source,
        bool failed,
        bool routerAutoload,
        bool expected)
    {
        var model = new LlamaModelInfo
        {
            Id = "m",
            Status = new LlamaModelStatus { Value = status, Failed = failed },
            Source = source,
        };

        Assert.Equal(expected, BuiltinProviders.LlamaModelIsSelectable(model, routerAutoload));
    }

    [Fact]
    public async Task DynamicProviderSyncsCatalogFromServerAfterLogin()
    {
        await using var router = new FakeLlamaRouter(RouterCatalogJson, autoload: true);
        var store = new InMemoryModelsStore();
        var runtime = await CreateRuntimeAsync(new InMemoryCredentialStore(), store, networkEnabled: true);

        // No credential yet: the refresh made no request and the catalog is empty.
        Assert.Equal(0, router.RequestCount);
        Assert.Empty(runtime.GetModels(BuiltinProviders.LlamaCppProviderId));

        // /login-style flow: scripted URL + key, verified against the live router.
        await runtime.LoginAsync(
            BuiltinProviders.LlamaCppProviderId,
            "api_key",
            new ScriptedInteraction(new[] { router.BaseUrl, "router-key-1" }));
        Assert.True(router.RequestCount > 0, "login verification did not reach the router");

        // Pinned post-login refresh is offline-only; the live catalog arrives via the
        // /llama sync (an online provider refresh).
        await runtime.RefreshAsync(new ModelsRefreshOptions
        {
            Providers = [BuiltinProviders.LlamaCppProviderId],
            AllowNetwork = true,
            Force = true,
        });

        // The post-login refresh published the selectable models with mapped metadata.
        var models = runtime.GetModels(BuiltinProviders.LlamaCppProviderId);
        var ids = models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["loaded-1", "sleepy-1", "preset-1"], ids);
        Assert.Equal(4096, models.Single(model => model.Id == "loaded-1").ContextWindow);
        Assert.Equal(8192, models.Single(model => model.Id == "sleepy-1").ContextWindow);

        // The synced catalog was persisted for offline restore.
        var entry = await store.ReadAsync(BuiltinProviders.LlamaCppProviderId);
        Assert.NotNull(entry);
        Assert.Equal(3, entry!.Models.Count);
    }

    [Fact]
    public async Task DynamicProviderRestoresCatalogOfflineFromStore()
    {
        await using var router = new FakeLlamaRouter(RouterCatalogJson, autoload: false);
        var store = new InMemoryModelsStore();
        var credentials = new InMemoryCredentialStore();
        var runtime = await CreateRuntimeAsync(credentials, store, networkEnabled: true);
        await runtime.LoginAsync(
            BuiltinProviders.LlamaCppProviderId,
            "api_key",
            new ScriptedInteraction(new[] { router.BaseUrl, string.Empty }));
        await runtime.RefreshAsync(new ModelsRefreshOptions
        {
            Providers = [BuiltinProviders.LlamaCppProviderId],
            AllowNetwork = true,
            Force = true,
        });
        Assert.Equal(2, runtime.GetModels(BuiltinProviders.LlamaCppProviderId).Count); // no autoload: no presets

        // A fresh runtime over the same store, offline: the last sync is restored.
        var requestsBeforeOffline = router.RequestCount;
        var offline = await CreateRuntimeAsync(credentials, store, networkEnabled: false);
        var models = offline.GetModels(BuiltinProviders.LlamaCppProviderId);
        Assert.Equal(["loaded-1", "sleepy-1"], models.Select(model => model.Id).ToArray());
        Assert.Equal(requestsBeforeOffline, router.RequestCount); // offline startup never touches the router
    }

    [Fact]
    public async Task DynamicProviderWithoutCredentialMakesNoRequests()
    {
        await using var router = new FakeLlamaRouter(RouterCatalogJson, autoload: true);
        var runtime = await CreateRuntimeAsync(new InMemoryCredentialStore(), new InMemoryModelsStore(), networkEnabled: true);

        Assert.Empty(runtime.GetModels(BuiltinProviders.LlamaCppProviderId));
        Assert.Equal(0, router.RequestCount);
    }

    [Fact]
    public async Task LlamaAuthResolvePrefersCredentialThenEnvThenLocal()
    {
        var auth = new BuiltinProviders.LlamaServerApiKeyAuth { Name = "llama.cpp server" };

        // Stored credential: its URL and key win.
        var fromCredential = await auth.ResolveAsync(new ApiKeyAuthInput
        {
            Context = EnvContext(new Dictionary<string, string> { ["LLAMA_BASE_URL"] = "http://env-host:1", ["LLAMA_API_KEY"] = "env-key" }),
            Credential = new ApiKeyCredential("stored-key", new Dictionary<string, string>
            {
                [LlamaUrls.BaseUrlEnvironmentVariable] = "http://stored-host:2/v1",
            }),
            CancellationToken = CancellationToken.None,
        });
        Assert.Equal("http://stored-host:2/v1", fromCredential?.Auth.BaseUrl);
        Assert.Equal("stored-key", fromCredential?.Auth.ApiKey);
        Assert.Equal("stored credential", fromCredential?.Source);

        // Env-only: URL from LLAMA_BASE_URL, key from LLAMA_API_KEY.
        var fromEnv = await auth.ResolveAsync(new ApiKeyAuthInput
        {
            Context = EnvContext(new Dictionary<string, string> { ["LLAMA_BASE_URL"] = "http://env-host:1", ["LLAMA_API_KEY"] = "env-key" }),
            CancellationToken = CancellationToken.None,
        });
        Assert.Equal("http://env-host:1/v1", fromEnv?.Auth.BaseUrl);
        Assert.Equal("env-key", fromEnv?.Auth.ApiKey);
        Assert.Equal("LLAMA_BASE_URL", fromEnv?.Source);

        // URL without any key: the keyless "local" default.
        var keyless = await auth.ResolveAsync(new ApiKeyAuthInput
        {
            Context = EnvContext(new Dictionary<string, string> { ["LLAMA_BASE_URL"] = "http://env-host:1" }),
            CancellationToken = CancellationToken.None,
        });
        Assert.Equal("local", keyless?.Auth.ApiKey);

        // No URL anywhere: not configured.
        var unconfigured = await auth.ResolveAsync(new ApiKeyAuthInput
        {
            Context = EnvContext(new Dictionary<string, string>()),
            CancellationToken = CancellationToken.None,
        });
        Assert.Null(unconfigured);
    }

    [Fact]
    public async Task LlamaAuthCheckReportsConfigurationState()
    {
        var auth = new BuiltinProviders.LlamaServerApiKeyAuth
        {
            Name = "llama.cpp server",
            Check = async input =>
            {
                var serverUrl = await BuiltinProviders.LlamaServerApiKeyAuth.ResolveServerUrlAsync(input);
                return serverUrl is null
                    ? null
                    : new AuthCheck("stored credential", "api_key");
            },
        };
        var configured = await auth.Check!(new ApiKeyAuthInput
        {
            Context = EnvContext(new Dictionary<string, string>()),
            Credential = new ApiKeyCredential(null, new Dictionary<string, string>
            {
                [LlamaUrls.BaseUrlEnvironmentVariable] = "http://127.0.0.1:8080",
            }),
            CancellationToken = CancellationToken.None,
        });
        Assert.NotNull(configured);
        Assert.Null(await auth.Check!(new ApiKeyAuthInput
        {
            Context = EnvContext(new Dictionary<string, string>()),
            CancellationToken = CancellationToken.None,
        }));
    }

    [Fact]
    public async Task LlamaLoginPersistsServerUrlInCredentialEnv()
    {
        await using var router = new FakeLlamaRouter(RouterCatalogJson, autoload: true);
        var credentials = new InMemoryCredentialStore();
        var runtime = await CreateRuntimeAsync(credentials, new InMemoryModelsStore(), networkEnabled: true);

        await runtime.LoginAsync(
            BuiltinProviders.LlamaCppProviderId,
            "api_key",
            new ScriptedInteraction(new[] { router.BaseUrl, string.Empty }));

        var stored = await credentials.ReadAsync(BuiltinProviders.LlamaCppProviderId);
        Assert.IsType<ApiKeyCredential>(stored);
        var api = Assert.IsType<ApiKeyCredential>(stored);
        Assert.Null(api.Key); // optional key left blank
        Assert.Equal(router.BaseUrl, api.Env?[LlamaUrls.BaseUrlEnvironmentVariable]);
    }

    [Fact]
    public void CredentialJsonRoundTripsApiKeyEnv()
    {
        var credentials = new Dictionary<string, Credential>
        {
            ["llama.cpp"] = new ApiKeyCredential("k", new Dictionary<string, string>
            {
                [LlamaUrls.BaseUrlEnvironmentVariable] = "http://127.0.0.1:8080",
            }),
        };

        var text = CredentialJson.Serialize(credentials);
        Assert.Contains("\"env\"", text);

        var roundTripped = CredentialJson.Deserialize(text);
        var api = Assert.IsType<ApiKeyCredential>(roundTripped["llama.cpp"]);
        Assert.Equal("k", api.Key);
        Assert.Equal("http://127.0.0.1:8080", api.Env?[LlamaUrls.BaseUrlEnvironmentVariable]);
    }

    [Fact]
    public async Task HuggingFaceSearchParsesResults()
    {
        var handler = new RecordingHandler(JsonSerializer.Serialize(new object[]
        {
            new { id = "org/model-a", downloads = 120 },
            new { id = "org/model-b" },
            "garbage",
        }));
        var client = new HuggingFaceClient("hf-token", "http://hf.local", new HttpClient(handler));

        var results = await client.SearchAsync("llama 8b", CancellationToken.None);

        Assert.Equal(["org/model-a", "org/model-b"], results.Select(model => model.Id).ToArray());
        Assert.Equal(120, results[0].Downloads);
        Assert.Equal(0, results[1].Downloads);
        Assert.Equal("Bearer hf-token", handler.Authorization);
        Assert.Contains("search=llama%208b", handler.LastPath);
        Assert.Contains("filter=gguf", handler.LastPath);
    }

    [Fact]
    public async Task HuggingFaceDetailsAggregateQuantizations()
    {
        var payload = new
        {
            id = "org/model",
            gated = false,
            siblings = new object[]
            {
                // Q4_K_M split into two shards: sizes aggregate.
                new { rfilename = "model-q4_k_m-00001-of-00002.gguf", size = 1_000L },
                new { rfilename = "model-q4_k_m-00002-of-00002.gguf", size = 500 },
                // Single-file Q8_0.
                new { rfilename = "subdir/model-q8_0.gguf", size = 9_000L },
                // Missing size -> size reported as unknown.
                new { rfilename = "model-bf16.gguf" },
                // Projector sidecars and non-gguf files are skipped.
                new { rfilename = "mmproj-model-f16.gguf", size = 100 },
                new { rfilename = "model.bin", size = 100 },
            },
        };
        var client = new HuggingFaceClient(null, "http://hf.local",
            new HttpClient(new RecordingHandler(JsonSerializer.Serialize(payload))));

        var details = await client.DetailsAsync("org/model", CancellationToken.None);

        Assert.Equal("org/model", details.Id);
        Assert.Null(details.Gated);
        // Q4_K_M sorts first, then by size ascending, unknown sizes last.
        Assert.Equal(["Q4_K_M", "Q8_0", "BF16"], details.Quantizations.Select(entry => entry.Name).ToArray());
        Assert.Equal(1_500, details.Quantizations[0].Size);
        Assert.Equal(9_000, details.Quantizations[1].Size);
        Assert.Null(details.Quantizations[2].Size);
    }

    [Fact]
    public async Task HuggingFaceRateLimitReportsRetryDelay()
    {
        var handler = new RecordingHandler(string.Empty, statusCode: 429);
        handler.RetryAfter = "17";
        var client = new HuggingFaceClient(null, "http://hf.local", new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SearchAsync("x", CancellationToken.None));
        Assert.Equal("Hugging Face rate limit reached; retry in 17s", error.Message);
    }

    [Fact]
    public async Task FindHuggingFaceTokenPrefersEnvironment()
    {
        Environment.SetEnvironmentVariable("HF_TOKEN", "hf-env-token");
        try
        {
            Assert.Equal("hf-env-token", await HuggingFaceClient.FindHuggingFaceToken());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HF_TOKEN", null);
        }
    }

    [Theory]
    [InlineData(LlamaModelStatusValues.Loaded, 4096, null, "loaded · 4k context")]
    [InlineData(LlamaModelStatusValues.Sleeping, null, "32768", "loaded · 33k context")]
    [InlineData(LlamaModelStatusValues.Downloading, null, null, "downloading")]
    [InlineData(LlamaModelStatusValues.Unloaded, 8192, null, "")]
    public void ModelDescriptionFollowsPinnedFormatting(
        string status,
        int? nCtx,
        string? ctxArg,
        string expected)
    {
        var args = ctxArg is null
            ? null
            : new[] { "--ctx-size", ctxArg };
        var model = new LlamaModelInfo
        {
            Id = "m",
            Status = new LlamaModelStatus { Value = status, Args = args },
            Meta = nCtx is { } value ? new LlamaModelMeta { NCtx = value } : null,
        };

        Assert.Equal(expected, LlamaCommands.ModelDescription(model));
    }

    [Theory]
    [InlineData("org/model", "org/model", null)]
    [InlineData("org/model:Q4_K_M", "org/model", "Q4_K_M")]
    [InlineData("org/model:F16", "org/model", "F16")]
    [InlineData("org:model:Q8_0", "org", "model:Q8_0")]  // no slash: first colon wins (pinned)
    public void ParseHuggingFaceModelSplitsQuantization(string input, string expectedRepo, string? expectedQuantization)
    {
        var (repository, quantization) = LlamaCommands.ParseHuggingFaceModel(input);
        Assert.Equal(expectedRepo, repository);
        Assert.Equal(expectedQuantization, quantization);
    }

    [Fact]
    public void ConnectionErrorMessageMapsTransportFailures()
    {
        Assert.Equal("Could not connect to the server.",
            LlamaCommands.ConnectionErrorMessage(new System.Net.Http.HttpRequestException("Connection refused")));
        Assert.Equal("boom", LlamaCommands.ConnectionErrorMessage(new InvalidOperationException("boom")));
    }

    [Theory]
    [InlineData(10, "10 B")]
    [InlineData(2048, "2.00 KiB")]
    [InlineData(5_242_880, "5.00 MiB")]
    [InlineData(12_884_901_888L, "12.0 GiB")]
    public void FormatBytesMatchesPinned(long bytes, string expected)
    {
        Assert.Equal(expected, LlamaClient.FormatBytes(bytes));
    }

    [Fact]
    public void ParseDownloadProgressSumsFiles()
    {
        var progress = LlamaClient.ParseDownloadProgress(new Dictionary<string, LlamaProgressRange>(StringComparer.Ordinal)
        {
            ["file-1"] = new(30, 100),
            ["file-2"] = new(25, 50),
        });

        Assert.NotNull(progress);
        Assert.Equal(0.36666666666, progress.Ratio!.Value, 5);
        Assert.Equal("55 B / 150 B", progress.Detail);
        Assert.Null(LlamaClient.ParseDownloadProgress(new Dictionary<string, LlamaProgressRange>(StringComparer.Ordinal)
        {
            ["file-1"] = new(0, 0),
        }));
    }

    private const string RouterCatalogJson = """
        [
          { "id": "loaded-1", "status": { "value": "loaded" }, "meta": { "n_ctx": 4096 }, "source": "file" },
          { "id": "sleepy-1", "status": { "value": "sleeping" }, "meta": { "n_ctx": 8192 }, "source": "file" },
          { "id": "preset-1", "status": { "value": "unloaded" }, "source": "preset" },
          { "id": "preset-failed", "status": { "value": "unloaded", "failed": true }, "source": "preset" },
          { "id": "plain-unloaded", "status": { "value": "unloaded" }, "source": "file" },
          { "id": "downloading-1", "status": { "value": "downloading" } }
        ]
        """;

    private static async Task<ModelRuntime> CreateRuntimeAsync(
        InMemoryCredentialStore credentials,
        InMemoryModelsStore store,
        bool networkEnabled)
    {
        return await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = credentials,
            Builtins = [BuiltinProviders.CreateLlamaCppProvider()],
            ModelsStore = store,
            ModelsPath = null,
            NetworkEnabled = networkEnabled,
            AllowModelNetwork = networkEnabled,
            RefreshOnCreate = true,
        });
    }

    private static AuthContext EnvContext(IDictionary<string, string> values) => new()
    {
        Env = name => Task.FromResult(values.TryGetValue(name, out var value) ? value : null),
        FileExists = _ => Task.FromResult(false),
    };

    /// <summary>Scripted IAuthInteraction: canned answers in prompt order.</summary>
    private sealed class ScriptedInteraction(IEnumerable<string> answers) : IAuthInteraction
    {
        private readonly Queue<string> _answers = new(answers);

        public CancellationToken Signal => CancellationToken.None;

        public Task<string> PromptAsync(AuthPromptStep prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(_answers.Dequeue());

        public void Notify(AuthEvent evt)
        {
        }
    }

    /// <summary>
    /// In-process llama.cpp router stand-in: serves the canned catalog on GET /models and
    /// the autoload flag on GET /props, and counts every request.
    /// </summary>
    private sealed class FakeLlamaRouter : IAsyncDisposable
    {
        private readonly string _catalogJson;
        private readonly bool _autoload;
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public string BaseUrl { get; }
        public int RequestCount => _requestCount;
        private int _requestCount;

        public FakeLlamaRouter(string catalogJson, bool autoload)
        {
            _catalogJson = catalogJson;
            _autoload = autoload;
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return; // listener stopped
                }

                Interlocked.Increment(ref _requestCount);
                var path = context.Request.Url?.AbsolutePath;
                string body;
                if (path == "/props")
                {
                    body = $"{{\"models_autoload\":{(_autoload ? "true" : "false")}}}";
                }
                else if (path is "/models" or "/models/")
                {
                    body = $"{{\"data\":{_catalogJson}}}";
                }
                else
                {
                    body = "{}";
                }

                var buffer = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(buffer);
                context.Response.Close();
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
                // Listener already stopped.
            }

            try
            {
                await _loop;
            }
            catch
            {
                // Serve loop ends when the listener stops.
            }
        }
    }

    /// <summary>Canned-response handler for the HF client: records path/authorization.</summary>
    private sealed class RecordingHandler(string body, int statusCode = 200) : HttpMessageHandler
    {
        public string RetryAfter { get; set; } = string.Empty;
        public string? Authorization { get; private set; }
        public string? LastPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            LastPath = request.RequestUri!.PathAndQuery;
            var response = new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (RetryAfter.Length > 0)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", RetryAfter);
            }

            await Task.Yield();
            return response;
        }
    }
}
