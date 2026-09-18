using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Shared test kit: a model runtime with a single OpenAI-compatible "local/test-model"
/// plus a runtime API key, so wiring tests run the real model startup path without any
/// network or environment dependencies.
/// </summary>
internal static class ModelRuntimeTestKit
{
    public const string ProviderId = "local";
    public const string ModelId = "test-model";
    public const string Reference = $"{ProviderId}/{ModelId}";
    public const string SecondModelId = "test-model-2";
    public const string SecondReference = $"{ProviderId}/{SecondModelId}";
    public const string ReasoningReference = "thinker/mini";

    /// <summary>
    /// Creates the kit. <paramref name="contextWindow"/>/<paramref name="maxOutput"/> let
    /// compaction/limit tests pin the model metadata; <paramref name="secondModel"/> adds a
    /// second model on the same provider (model-switch tests) and <paramref name="reasoningModel"/>
    /// adds a "thinker/mini" reasoning model (thinking-serialization tests).
    /// </summary>
    public static async Task<(ModelRuntime Runtime, ModelSessionState State, SettingsManager Settings)> CreateAsync(
        int contextWindow = 128_000,
        int maxOutput = 16_384,
        bool secondModel = false,
        bool reasoningModel = false)
    {
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage());
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
            Id = ModelId,
            Name = "Test Model",
            Api = "openai-completions",
            Provider = ProviderId,
            BaseUrl = "http://localhost:9/v1",
            Input = ["text"],
            Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
            ContextWindow = contextWindow,
            MaxTokens = maxOutput,
        };

        var models = new List<ModelInfo> { model };
        if (secondModel)
        {
            models.Add(new ModelInfo
            {
                Id = SecondModelId,
                Name = "Test Model 2",
                Api = "openai-completions",
                Provider = ProviderId,
                BaseUrl = "http://localhost:9/v1",
                Input = ["text"],
                Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
                ContextWindow = contextWindow,
                MaxTokens = 2_048,
            });
        }

        runtime.RegisterProvider(new ProviderSpec
        {
            Id = ProviderId,
            Name = "Local",
            BaseUrl = "http://localhost:9/v1",
            Auth = new ProviderAuth(
                new EnvApiKeyAuth { Name = "k", EnvironmentVariableNames = ["LOCAL_TEST_KEY"] }),
            GetModels = () => models,
            DefaultApi = "openai-completions",
        });

        await runtime.SetRuntimeApiKeyAsync(ProviderId, "test-key");

        if (reasoningModel)
        {
            var thinker = new ModelInfo
            {
                Id = "mini",
                Name = "Thinker Mini",
                Api = "openai-completions",
                Provider = "thinker",
                BaseUrl = "http://localhost:9/v1",
                Input = ["text"],
                Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
                ContextWindow = 32_000,
                MaxTokens = 8_192,
                Reasoning = true,
            };

            runtime.RegisterProvider(new ProviderSpec
            {
                Id = "thinker",
                Name = "Thinker",
                BaseUrl = "http://localhost:9/v1",
                Auth = new ProviderAuth(
                    new EnvApiKeyAuth { Name = "k", EnvironmentVariableNames = ["THINKER_KEY"] }),
                GetModels = () => new[] { thinker },
                DefaultApi = "openai-completions",
            });

            await runtime.SetRuntimeApiKeyAsync("thinker", "thinker-key");
        }

        var state = new ModelSessionState(runtime, settings);
        return (runtime, state, settings);
    }
}
