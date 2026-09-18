using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Wire-level tests for the provider bridge: the request body must carry the CURRENT model
/// selection (model id, max output, reasoning effort) on every call so /model and /thinking
/// take effect on the next request without rebuilding any client (pinned AgentSession
/// semantics), and credentials flow from the runtime auth resolution.
/// </summary>
public sealed class ModelRuntimeBridgeTests
{
    private const string CompletionTemplate =
        """{"id":"c1","object":"chat.completion","created":1,"model":"__MODEL__","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    [Fact]
    public async Task RequestCarriesCurrentModelMaxOutputAndCredential()
    {
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync(maxOutput: 4_096);
        using var recorder = new RecordingHandler();
        using var bridge = new ModelRuntimeChatClient(runtime, () => state.Current, () => 0, recorder);

        await SetModelAsync(runtime, state, ModelRuntimeTestKit.Reference);
        await bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var (headers, body) = recorder.Last();
        var json = JsonDocument.Parse(body);
        Assert.Equal(ModelRuntimeTestKit.ModelId, json.RootElement.GetProperty("model").GetString());
        Assert.Equal(4_096, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("Bearer test-key", headers["Authorization"]);
    }

    [Fact]
    public async Task ModelSwitchAppliesToNextRequestWithoutRebuild()
    {
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync(secondModel: true);
        using var recorder = new RecordingHandler();
        using var bridge = new ModelRuntimeChatClient(runtime, () => state.Current, () => 0, recorder);

        await SetModelAsync(runtime, state, ModelRuntimeTestKit.Reference);
        await bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "one")]);

        // Switch without touching the bridge: the same client instance must serve the new model.
        await SetModelAsync(runtime, state, ModelRuntimeTestKit.SecondReference);
        await bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "two")]);

        Assert.Equal(2, recorder.Requests.Count);
        Assert.Equal(ModelRuntimeTestKit.ModelId, ModelFrom(recorder.Requests[0].Body));
        Assert.Equal(ModelRuntimeTestKit.SecondModelId, ModelFrom(recorder.Requests[1].Body));
        // The second model pins its own max output (2048 in the kit).
        var second = JsonDocument.Parse(recorder.Requests[1].Body);
        Assert.Equal(2_048, second.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task ThinkingLevelSerializesAsReasoningEffortAndOffOmitsIt()
    {
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync(reasoningModel: true);
        using var recorder = new RecordingHandler();
        using var bridge = new ModelRuntimeChatClient(runtime, () => state.Current, () => 0, recorder);

        await SetModelAsync(runtime, state, ModelRuntimeTestKit.ReasoningReference);
        state.SetThinkingLevel("minimal", new ModelMutationOptions());
        await bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "think")]);
        var withEffort = JsonDocument.Parse(recorder.Last().Body);
        Assert.Equal("minimal", withEffort.RootElement.GetProperty("reasoning_effort").GetString());

        state.SetThinkingLevel("off", new ModelMutationOptions());
        await bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "nothink")]);
        var withoutEffort = JsonDocument.Parse(recorder.Last().Body);
        Assert.False(withoutEffort.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task KeylessModelSendsNoAuthorizationHeader()
    {
        // A runtime whose provider declares auth but has none configured: the bridge must
        // issue the request without an Authorization header (pinned keyless endpoints).
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Builtins = [],
            ModelsStore = new InMemoryModelsStore(),
            ModelsPath = null,
            NetworkEnabled = false,
            RefreshOnCreate = false,
        });

        var model = new ModelInfo
        {
            Id = "raw",
            Name = "Raw",
            Api = "openai-completions",
            Provider = "raw",
            BaseUrl = "http://localhost:9/v1",
            Input = ["text"],
            Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
            ContextWindow = 8_000,
            MaxTokens = 1_000,
        };

        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "raw",
            Name = "Raw",
            BaseUrl = "http://localhost:9/v1",
            Auth = new ProviderAuth(
                new EnvApiKeyAuth
                {
                    Name = "k",
                    EnvironmentVariableNames = ["RAW_KEY"],
                }),
            GetModels = () => new[] { model },
            DefaultApi = "openai-completions",
        });

        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage());
        var state = new ModelSessionState(runtime, settings);
        state.ApplySelection(new CurrentModelSelection(model, "off", null, null));

        using var recorder = new RecordingHandler();
        using var bridge = new ModelRuntimeChatClient(runtime, () => state.Current, () => 0, recorder);
        await bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var (headers, _) = recorder.Last();
        Assert.False(headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task UnsupportedApiFailsAtRequestTime()
    {
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync();
        var anthropic = new ModelInfo
        {
            Id = "claude-1",
            Name = "Claude",
            Api = "anthropic-messages",
            Provider = "anthropic",
            BaseUrl = "http://localhost:9/v1",
            Input = ["text"],
            Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
            ContextWindow = 8_000,
            MaxTokens = 1_000,
        };

        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "anthropic",
            Name = "Anthropic",
            BaseUrl = "http://localhost:9/v1",
            Auth = new ProviderAuth(
                new EnvApiKeyAuth
                {
                    Name = "k",
                    EnvironmentVariableNames = ["ANTHROPIC_KEY"],
                }),
            GetModels = () => new[] { anthropic },
            DefaultApi = "anthropic-messages",
        });

        state.ApplySelection(new CurrentModelSelection(anthropic, "off", null, null));
        using var bridge = new ModelRuntimeChatClient(runtime, () => state.Current, () => 0, new RecordingHandler());

        await Assert.ThrowsAsync<UnsupportedCapabilityException>(
            () => bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
    }

    [Fact]
    public async Task NoSelectionFailsWithGuidance()
    {
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync();
        using var bridge = new ModelRuntimeChatClient(runtime, () => state.Current, () => 0, new RecordingHandler());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bridge.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Contains("No model is selected", error.Message);
    }

    /// <summary>Resolves a model reference through the runtime and sets it on the state.</summary>
    private static async Task SetModelAsync(ModelRuntime runtime, ModelSessionState state, string reference)
    {
        var slash = reference.IndexOf('/');
        var model = runtime.GetModel(reference[..slash], reference[(slash + 1)..])
            ?? throw new InvalidOperationException($"model not found: {reference}");
        await state.SetModelAsync(model, new ModelMutationOptions());
    }

    private static string ModelFrom(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;

    /// <summary>Records every request and answers with a canned chat completion.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(Dictionary<string, string> Headers, string Body)> _requests = [];

        public IReadOnlyList<(Dictionary<string, string> Headers, string Body)> Requests => _requests;

        public (Dictionary<string, string> Headers, string Body) Last() => _requests[^1];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            _requests.Add((headers, body));

            var model = ModelFrom(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Completion(model), Encoding.UTF8, "application/json"),
            };
        }

        private static string Completion(string model) => CompletionTemplate.Replace("__MODEL__", model);
    }
}
