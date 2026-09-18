using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Live model/thinking session state (pinned AgentSession setModel/cycleModel/
/// setThinkingLevel/cycleThinkingLevel semantics).
/// </summary>
public class ModelSessionStateTests
{
    private static ModelInfo Model(string provider, string id, bool reasoning = true, int contextWindow = 128_000, int maxTokens = 8_192) => new()
    {
        Id = id,
        Name = id,
        Api = "openai-completions",
        Provider = provider,
        BaseUrl = "http://localhost",
        Input = ["text"],
        Cost = new ModelCost { Input = 1, Output = 1, CacheRead = 0, CacheWrite = 0 },
        ContextWindow = contextWindow,
        MaxTokens = maxTokens,
        Reasoning = reasoning,
    };

    private static ProviderSpec Provider(string id, params ModelInfo[] models) => new()
    {
        Id = id,
        Name = id,
        BaseUrl = "http://localhost",
        Auth = new ProviderAuth(new EnvApiKeyAuth
        {
            Name = id,
            EnvironmentVariableNames = [$"PISHARP_TEST_{id.ToUpperInvariant()}_KEY"],
        }),
        GetModels = () => models,
        DefaultApi = "openai-completions",
    };

    private static Task<ModelRuntime> RuntimeAsync(params (string Provider, string Id, bool Reasoning, int ContextWindow, int MaxTokens)[] models)
    {
        var list = models
            .Select(m => Model(m.Provider, m.Id, m.Reasoning, m.ContextWindow, m.MaxTokens))
            .GroupBy(m => m.Provider)
            .Select(g => (Provider: g.Key, Models: g.ToArray()));
        return CreateAsync(list);

        static async Task<ModelRuntime> CreateAsync(IEnumerable<(string Provider, ModelInfo[] Models)> grouped)
        {
            var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
            {
                Credentials = new InMemoryCredentialStore(),
                Builtins = [],
                ModelsStore = new InMemoryModelsStore(),
                ModelsPath = null,
                NetworkEnabled = false,
                RefreshOnCreate = false,
            });
            foreach (var (provider, models) in grouped)
            {
                runtime.RegisterProvider(Provider(provider, models));
            }
            return runtime;
        }
    }

    private static Task<SettingsManager> SettingsAsync(string? json = null) =>
        SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(json, null));

    [Fact]
    public async Task SetModel_SwitchesModelAndAppliesThinkingDefault()
    {
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096), ("p", "b", false, 32_000, 2_048));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var state = new ModelSessionState(runtime, await SettingsAsync());
        var current = Model("p", "a", true, 100_000, 4_096);
        state.ApplySelection(new CurrentModelSelection(current, "low", null, null));

        var target = runtime.GetModel("p", "b")!;
        var result = await state.SetModelAsync(target, new ModelMutationOptions());

        Assert.Equal("b", state.Model!.Id);
        // Target model is non-reasoning: the default "medium" clamps to "off".
        Assert.Equal("off", state.ThinkingLevel);
        Assert.True(result.ModelChanged);
        Assert.True(result.ThinkingChanged);
        // Session overrides survive a model switch.
        state.ApplySelection(state.Current!.WithModel(current, "low"));
    }

    [Fact]
    public async Task SetModel_KeepsContextOverrideAcrossSwitch()
    {
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096), ("p", "b", true, 32_000, 2_048));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var state = new ModelSessionState(runtime, await SettingsAsync());
        state.ApplySelection(new CurrentModelSelection(runtime.GetModel("p", "a")!, "low", 64_000, null));

        var result = await state.SetModelAsync(runtime.GetModel("p", "b")!, new ModelMutationOptions());

        Assert.Equal(64_000, state.Current!.EffectiveContextWindow);
        Assert.Equal(64_000, state.Current.ContextWindowOverride);
        Assert.False(result.ThinkingChanged);
    }

    [Fact]
    public async Task SetModel_ThrowsWithoutAuth()
    {
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096));
        var state = new ModelSessionState(runtime, await SettingsAsync());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => state.SetModelAsync(runtime.GetModel("p", "a")!, new ModelMutationOptions()));
        Assert.Contains("No API key for p/a", ex.Message);
    }

    [Fact]
    public async Task SetModel_PersistStoresDefaultAndExtendsScope()
    {
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096), ("p", "b", true, 32_000, 2_048));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsAsync();
        var state = new ModelSessionState(
            runtime,
            settings,
            scopedModels: [new ScopedModel(runtime.GetModel("p", "a")!, null)]);

        await state.SetModelAsync(runtime.GetModel("p", "b")!, new ModelMutationOptions(Persist: true));

        Assert.Equal("p", settings.GetDefaultProvider());
        Assert.Equal("b", settings.GetDefaultModel());
        // Persisted default joined the non-empty scope.
        Assert.Equal(2, state.ScopedModels.Count);
        Assert.Equal("b", state.ScopedModels[1].Model.Id);
    }

    [Fact]
    public async Task CycleModel_ScopedForwardAndBackwardWrap()
    {
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096), ("p", "b", true, 32_000, 2_048));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var a = runtime.GetModel("p", "a")!;
        var b = runtime.GetModel("p", "b")!;
        var state = new ModelSessionState(
            runtime,
            await SettingsAsync(),
            scopedModels: [new ScopedModel(a, null), new ScopedModel(b, null)]);
        state.ApplySelection(new CurrentModelSelection(a, "low", null, null));

        var forward = state.CycleModel("forward", new ModelMutationOptions());
        Assert.NotNull(forward);
        Assert.Equal("b", forward!.Model.Id);
        Assert.True(forward.IsScoped);

        var backward = state.CycleModel("backward", new ModelMutationOptions());
        Assert.Equal("a", backward!.Model.Id);

        // Wrap-around: from "a" forward lands on "b" again.
        var wrapped = state.CycleModel("forward", new ModelMutationOptions());
        Assert.Equal("b", wrapped!.Model.Id);
    }

    [Fact]
    public async Task CycleModel_SkipsUnavailableScopedModels()
    {
        // "c" is declared in the scope but not available (no auth): it is filtered out.
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096), ("q", "c", true, 100_000, 4_096));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var state = new ModelSessionState(
            runtime,
            await SettingsAsync(),
            scopedModels:
            [
                new ScopedModel(runtime.GetModel("p", "a")!, null),
                new ScopedModel(runtime.GetModel("q", "c")!, null),
            ]);
        state.ApplySelection(new CurrentModelSelection(runtime.GetModel("p", "a")!, "low", null, null));

        Assert.Null(state.CycleModel("forward", new ModelMutationOptions()));
    }

    [Fact]
    public async Task CycleModel_UnscopedUsesAvailableSnapshot()
    {
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096), ("p", "b", true, 32_000, 2_048));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var state = new ModelSessionState(runtime, await SettingsAsync());
        state.ApplySelection(new CurrentModelSelection(runtime.GetModel("p", "a")!, "low", null, null));

        var result = state.CycleModel("forward", new ModelMutationOptions());

        Assert.NotNull(result);
        Assert.Equal("b", result!.Model.Id);
        Assert.False(result.IsScoped);
    }

    [Fact]
    public async Task SetThinkingLevel_ClampsAndPersistsRequested()
    {
        // Model supports up to "high" (no xhigh/max mapping).
        var model = Model("p", "a", true, 100_000, 4_096);
        model = model with
        {
            ThinkingLevelMap = new Dictionary<string, string?>
            {
                ["xhigh"] = null,
                ["max"] = null,
            },
        };
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsAsync();
        var state = new ModelSessionState(runtime, settings);
        state.ApplySelection(new CurrentModelSelection(runtime.GetModel("p", "a")!, "low", null, null));

        var result = state.SetThinkingLevel("xhigh", new ModelMutationOptions(Persist: true));

        Assert.Equal("xhigh", result.Requested);
        Assert.Equal("high", result.Effective);
        Assert.Equal("high", state.ThinkingLevel);
        // The requested (not clamped) level is persisted.
        Assert.Equal("xhigh", settings.GetDefaultThinkingLevel());
        Assert.True(result.Changed);

        // Same effective level again: no change reported.
        var again = state.SetThinkingLevel("xhigh", new ModelMutationOptions());
        Assert.False(again.Changed);
    }

    [Fact]
    public async Task CycleThinkingLevel_FollowsModelSupport()
    {
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096), ("p", "b", false, 32_000, 2_048));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var state = new ModelSessionState(runtime, await SettingsAsync());
        state.ApplySelection(new CurrentModelSelection(runtime.GetModel("p", "a")!, "off", null, null));

        var next = state.CycleThinkingLevel(new ModelMutationOptions());
        Assert.Equal("minimal", next);
        Assert.Equal("minimal", state.ThinkingLevel);

        // Non-reasoning model: cycling is unavailable.
        await state.SetModelAsync(runtime.GetModel("p", "b")!, new ModelMutationOptions());
        Assert.Null(state.CycleThinkingLevel(new ModelMutationOptions()));
    }

    [Fact]
    public async Task AvailableThinkingLevels_MirrorModelCapabilities()
    {
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096), ("p", "b", false, 32_000, 2_048));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var state = new ModelSessionState(runtime, await SettingsAsync());
        Assert.Equal(ThinkingLevel.Options, state.GetAvailableThinkingLevels());

        state.ApplySelection(new CurrentModelSelection(runtime.GetModel("p", "a")!, "low", null, null));
        Assert.Contains("off", state.GetAvailableThinkingLevels());

        await state.SetModelAsync(runtime.GetModel("p", "b")!, new ModelMutationOptions());
        Assert.Equal(["off"], state.GetAvailableThinkingLevels());
    }

    [Fact]
    public async Task ThinkingLevelForModelSwitch_FollowsPrecedence()
    {
        var runtime = await RuntimeAsync(("p", "a", true, 100_000, 4_096), ("p", "b", true, 32_000, 2_048));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsAsync();
        settings.SetDefaultThinkingLevel("high");
        settings.SetModelThinkingLevel("p", "b", "low");
        var state = new ModelSessionState(runtime, settings);
        state.ApplySelection(new CurrentModelSelection(runtime.GetModel("p", "a")!, "medium", null, null));

        // Per-model override wins over the global default.
        Assert.Equal("low", state.GetThinkingLevelForModelSwitch(runtime.GetModel("p", "b")!));
        // Global default applies to models without an override.
        Assert.Equal("high", state.GetThinkingLevelForModelSwitch(runtime.GetModel("p", "a")!));
        // Explicit level wins over everything.
        Assert.Equal("minimal", state.GetThinkingLevelForModelSwitch(runtime.GetModel("p", "b")!, "minimal"));
    }
}
