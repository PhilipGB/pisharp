using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Deterministic startup model/thinking precedence (pinned main.ts buildSessionOptions +
/// sdk.ts createAgentSession).
/// </summary>
public class ModelStartupResolverTests
{
    private static ModelInfo Model(string provider, string id, bool reasoning = true) => new()
    {
        Id = id,
        Name = id,
        Api = "openai-completions",
        Provider = provider,
        BaseUrl = "http://localhost",
        Input = ["text"],
        Cost = new ModelCost { Input = 1, Output = 1, CacheRead = 0, CacheWrite = 0 },
        ContextWindow = 128_000,
        MaxTokens = 8_192,
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
            EnvironmentVariableNames = [$"PISHARP_STARTUP_{id.ToUpperInvariant()}_KEY"],
        }),
        GetModels = () => models,
        DefaultApi = "openai-completions",
    };

    private static async Task<ModelRuntime> RuntimeAsync(
        params (string Provider, string Id, bool Reasoning)[] models)
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
        foreach (var group in models.GroupBy(m => m.Provider))
        {
            runtime.RegisterProvider(Provider(group.Key, group.Select(m => Model(m.Provider, m.Id, m.Reasoning)).ToArray()));
        }

        return runtime;
    }

    private static ModelStartupInput Input(
        string? cliModel = null,
        string? cliThinking = null,
        IReadOnlyList<ScopedModel>? scoped = null,
        bool hasExistingSession = false,
        (string, string)? savedModel = null,
        string? savedThinking = null,
        bool savedHasThinkingEntry = false) => new(
        CliProvider: null,
        CliModel: cliModel,
        CliThinking: cliThinking,
        ScopedModels: scoped ?? [],
        HasExistingSession: hasExistingSession,
        SavedSessionModel: savedModel is { } m ? (m.Item1, m.Item2) : null,
        SavedSessionThinking: savedThinking,
        SavedSessionHasThinkingEntry: savedHasThinkingEntry);

    [Fact]
    public async Task CliModel_BeatsSettingsDefaultAndSession()
    {
        var runtime = await RuntimeAsync(("p", "a", true), ("p", "b", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(
            new InMemorySettingsStorage("""{"defaultProvider":"p","defaultModel":"b"}""", null));

        var result = ModelStartupResolver.Resolve(
            Input(cliModel: "a", hasExistingSession: true, savedModel: ("p", "b")),
            settings,
            runtime);

        Assert.Equal("a", result.Model!.Id);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CliModel_Unknown_FailsWithDiagnostic()
    {
        var runtime = await RuntimeAsync(("p", "a", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));

        var result = ModelStartupResolver.Resolve(Input(cliModel: "nope/missing"), settings, runtime);

        Assert.Null(result.Model);
        Assert.NotNull(result.Error);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task CliModel_UnknownIdWithKnownProvider_UsesCustomModelWarning()
    {
        var runtime = await RuntimeAsync(("p", "a", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));

        var result = ModelStartupResolver.Resolve(Input(cliModel: "p/custom-local"), settings, runtime);

        // Pinned: a known provider with an unknown id resolves to a custom model id.
        Assert.Equal("custom-local", result.Model!.Id);
        Assert.Single(result.Warnings);
        Assert.Contains("custom model id", result.Warnings[0]);
    }

    [Fact]
    public async Task CliModel_PatternThinking_Shortcut()
    {
        var runtime = await RuntimeAsync(("p", "a", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));

        var result = ModelStartupResolver.Resolve(Input(cliModel: "a:high"), settings, runtime);

        Assert.Equal("high", result.ThinkingLevel);
        Assert.True(result.CliThinkingOverride);
    }

    [Fact]
    public async Task CliThinking_BeatsPatternLevel()
    {
        var runtime = await RuntimeAsync(("p", "a", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));

        var result = ModelStartupResolver.Resolve(Input(cliModel: "a:high", cliThinking: "low"), settings, runtime);

        Assert.Equal("low", result.ThinkingLevel);
        Assert.True(result.CliThinkingOverride);
    }

    [Fact]
    public async Task ScopedModels_NewSession_FirstOrSavedDefault()
    {
        var runtime = await RuntimeAsync(("p", "a", true), ("p", "b", true), ("p", "c", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(
            new InMemorySettingsStorage("""{"defaultProvider":"p","defaultModel":"c"}""", null));
        var scoped = new[]
        {
            new ScopedModel(runtime.GetModel("p", "a")!, null),
            new ScopedModel(runtime.GetModel("p", "c")!, null),
        };

        // Saved default "c" is in scope: it wins over the first scoped model.
        var result = ModelStartupResolver.Resolve(Input(scoped: scoped), settings, runtime);
        Assert.Equal("c", result.Model!.Id);

        // Without a saved default the first scoped model applies.
        var plainSettings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));
        var result2 = ModelStartupResolver.Resolve(Input(scoped: scoped), plainSettings, runtime);
        Assert.Equal("a", result2.Model!.Id);
    }

    [Fact]
    public async Task ScopedModels_IgnoredForExistingSession_SessionRestores()
    {
        var runtime = await RuntimeAsync(("p", "a", true), ("p", "b", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));
        var scoped = new[] { new ScopedModel(runtime.GetModel("p", "a")!, null) };

        var result = ModelStartupResolver.Resolve(
            Input(scoped: scoped, hasExistingSession: true, savedModel: ("p", "b"), savedThinking: "high", savedHasThinkingEntry: true),
            settings,
            runtime);

        Assert.Equal("b", result.Model!.Id);
        Assert.Equal("high", result.ThinkingLevel);
    }

    [Fact]
    public async Task SessionRestore_UsesSettingsThinkingWithoutThinkingEntry()
    {
        var runtime = await RuntimeAsync(("p", "b", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(
            new InMemorySettingsStorage("""{"defaultThinkingLevel":"low"}""", null));

        var result = ModelStartupResolver.Resolve(
            Input(hasExistingSession: true, savedModel: ("p", "b"), savedThinking: "medium"),
            settings,
            runtime);

        Assert.Equal("b", result.Model!.Id);
        // No thinking_level_change entry: the settings default applies, not the stale session value.
        Assert.Equal("low", result.ThinkingLevel);
    }

    [Fact]
    public async Task SessionRestore_MissingModel_FallsBackWithMessage()
    {
        var runtime = await RuntimeAsync(("p", "b", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));

        var result = ModelStartupResolver.Resolve(
            Input(hasExistingSession: true, savedModel: ("p", "gone")),
            settings,
            runtime);

        Assert.Equal("b", result.Model!.Id);
        Assert.NotNull(result.ModelFallbackMessage);
        Assert.Contains("gone", result.ModelFallbackMessage);
    }

    [Fact]
    public async Task SessionRestore_UnknownProvider_NoAuth_FallsBack()
    {
        var runtime = await RuntimeAsync(("p", "b", true), ("q", "c", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        // "q" has no auth configured, so its saved model cannot be restored.
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));

        var result = ModelStartupResolver.Resolve(
            Input(hasExistingSession: true, savedModel: ("q", "c")),
            settings,
            runtime);

        Assert.Equal("p/b", result.Model!.Reference);
        Assert.NotNull(result.ModelFallbackMessage);
    }

    [Fact]
    public async Task SettingsDefault_AppliesWhenNothingElse()
    {
        var runtime = await RuntimeAsync(("p", "a", true), ("p", "b", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(
            new InMemorySettingsStorage("""{"defaultProvider":"p","defaultModel":"b"}""", null));

        var result = ModelStartupResolver.Resolve(Input(), settings, runtime);

        Assert.Equal("b", result.Model!.Id);
        Assert.Equal("medium", result.ThinkingLevel);
    }

    [Fact]
    public async Task FirstAvailable_WhenNoDefault()
    {
        var runtime = await RuntimeAsync(("p", "a", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));

        var result = ModelStartupResolver.Resolve(Input(), settings, runtime);

        Assert.Equal("a", result.Model!.Id);
    }

    [Fact]
    public async Task NoModels_FallbackMessageAndOffThinking()
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
        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));

        var result = ModelStartupResolver.Resolve(Input(), settings, runtime);

        Assert.Null(result.Model);
        Assert.NotNull(result.ModelFallbackMessage);
        Assert.Equal("off", result.ThinkingLevel);
    }

    [Fact]
    public async Task Thinking_ClampsToModelCapabilities()
    {
        // "a" supports no xhigh/max.
        var runtime = await RuntimeAsync(("p", "a", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(
            new InMemorySettingsStorage("""{"defaultModel":"a","defaultProvider":"p","defaultThinkingLevel":"xhigh"}""", null));

        var result = ModelStartupResolver.Resolve(Input(), settings, runtime);

        Assert.Equal("high", result.ThinkingLevel);
    }

    [Fact]
    public async Task PerModelThinking_AppliesAtStartup()
    {
        var runtime = await RuntimeAsync(("p", "a", true), ("p", "b", true));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var settings = await SettingsManager.CreateFromStorageAsync(
            new InMemorySettingsStorage("""{"defaultProvider":"p","defaultModel":"a"}""", null));
        settings.SetModelThinkingLevel("p", "a", "minimal");

        var result = ModelStartupResolver.Resolve(Input(), settings, runtime);

        Assert.Equal("minimal", result.ThinkingLevel);
    }
}
