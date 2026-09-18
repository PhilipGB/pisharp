using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// The executable-model selection boundary (item 3): the catalogue may know models on
/// provider APIs this build cannot execute, but no selection surface (SetModel, cycling,
/// startup resolution, session restore, CLI --model) may select one.
/// </summary>
public class ModelExecutionSupportTests
{
    private static ModelInfo Model(string provider, string id, string api = ModelApi.OpenAiCompletions) => new()
    {
        Id = id,
        Name = id,
        Api = api,
        Provider = provider,
        BaseUrl = "http://localhost",
        Input = ["text"],
        Cost = new ModelCost { Input = 1, Output = 1, CacheRead = 0, CacheWrite = 0 },
        ContextWindow = 128_000,
        MaxTokens = 8_192,
        Reasoning = true,
    };

    private static ProviderSpec Provider(string id, string api, params ModelInfo[] models) => new()
    {
        Id = id,
        Name = id,
        BaseUrl = "http://localhost",
        Auth = new ProviderAuth(new EnvApiKeyAuth
        {
            Name = id,
            EnvironmentVariableNames = [$"PISHARP_EXEC_{id.ToUpperInvariant()}_KEY"],
        }),
        GetModels = () => models,
        DefaultApi = api,
    };

    private static async Task<ModelRuntime> CreateRuntimeAsync(
        params (string Provider, string Api, string Id, string WireApi)[] models)
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
            var providerModels = group
                .Select(m => Model(m.Provider, m.Id, m.WireApi))
                .ToArray();
            runtime.RegisterProvider(Provider(group.Key, group.First().Api, providerModels));
        }

        return runtime;
    }

    private static Task<SettingsManager> SettingsAsync() =>
        SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(null, null));

    [Fact]
    public void CanExecute_OnlyOpenAiCompletions()
    {
        Assert.True(ModelExecutionSupport.CanExecute(Model("p", "a", ModelApi.OpenAiCompletions)));
        Assert.False(ModelExecutionSupport.CanExecute(Model("p", "b", ModelApi.AnthropicMessages)));
        Assert.False(ModelExecutionSupport.CanExecute(Model("p", "c", ModelApi.MistralConversations)));
        Assert.False(ModelExecutionSupport.CanExecute(Model("p", "d", ModelApi.GoogleGenerativeAi)));
        Assert.Equal([ModelApi.OpenAiCompletions], ModelExecutionSupport.SupportedApis.ToArray());
    }

    [Fact]
    public async Task SetModel_ThrowsModelNotExecutable_ForUnsupportedApi()
    {
        var runtime = await CreateRuntimeAsync(("p", ModelApi.AnthropicMessages, "claude", ModelApi.AnthropicMessages));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var state = new ModelSessionState(runtime, await SettingsAsync());

        var exception = await Assert.ThrowsAsync<ModelNotExecutableException>(() =>
            state.SetModelAsync(runtime.GetModel("p", "claude")!, new ModelMutationOptions()));

        Assert.Contains("anthropic-messages", exception.Message);
        Assert.Contains(ModelApi.OpenAiCompletions, exception.Message);
        Assert.Null(state.Model);
    }

    [Fact]
    public async Task SetModel_ExecutesOpenAiCompletions()
    {
        var runtime = await CreateRuntimeAsync(("p", ModelApi.OpenAiCompletions, "gpt", ModelApi.OpenAiCompletions));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var state = new ModelSessionState(runtime, await SettingsAsync());

        var result = await state.SetModelAsync(runtime.GetModel("p", "gpt")!, new ModelMutationOptions());

        Assert.Equal("gpt", result.Model.Id);
    }

    [Fact]
    public async Task Cycle_SkipsInexecutableModels()
    {
        var runtime = await CreateRuntimeAsync(
            ("p", ModelApi.OpenAiCompletions, "a", ModelApi.OpenAiCompletions),
            ("p", ModelApi.OpenAiCompletions, "c", ModelApi.OpenAiCompletions),
            ("p", ModelApi.AnthropicMessages, "b", ModelApi.AnthropicMessages));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var state = new ModelSessionState(runtime, await SettingsAsync());
        state.ApplySelection(new CurrentModelSelection(runtime.GetModel("p", "a")!, "low", null, null));

        // a -> c -> a; the inexecutable "b" is never visited.
        var first = state.CycleModel("forward", new ModelMutationOptions());
        Assert.Equal("c", first!.Model.Id);
        var second = state.CycleModel("forward", new ModelMutationOptions());
        Assert.Equal("a", second!.Model.Id);
        Assert.NotEqual("b", first.Model.Id);
    }

    [Fact]
    public async Task Cycle_OnlyOneExecutable_ReturnsNull()
    {
        var runtime = await CreateRuntimeAsync(
            ("p", ModelApi.OpenAiCompletions, "a", ModelApi.OpenAiCompletions),
            ("p", ModelApi.AnthropicMessages, "b", ModelApi.AnthropicMessages));
        await runtime.SetRuntimeApiKeyAsync("p", "key");
        var state = new ModelSessionState(runtime, await SettingsAsync());
        state.ApplySelection(new CurrentModelSelection(runtime.GetModel("p", "a")!, "low", null, null));

        // Two models are available, but only one is executable: nothing to cycle.
        Assert.Null(state.CycleModel("forward", new ModelMutationOptions()));
        Assert.Equal("a", state.Model!.Id);
    }

    [Fact]
    public async Task Startup_OnlyInexecutableCatalog_FallbackMessageNamesApis()
    {
        var runtime = await CreateRuntimeAsync(("p", ModelApi.AnthropicMessages, "claude", ModelApi.AnthropicMessages));
        await runtime.SetRuntimeApiKeyAsync("p", "key");

        var result = ModelStartupResolver.Resolve(
            new ModelStartupInput(
                CliProvider: null,
                CliModel: null,
                CliThinking: null,
                ScopedModels: [],
                HasExistingSession: false,
                SavedSessionModel: null,
                SavedSessionThinking: null,
                SavedSessionHasThinkingEntry: false),
            await SettingsAsync(),
            runtime);

        Assert.Null(result.Model);
        Assert.NotNull(result.ModelFallbackMessage);
        Assert.Contains("No executable models available", result.ModelFallbackMessage);
        Assert.Contains(ModelApi.AnthropicMessages, result.ModelFallbackMessage);
        Assert.Contains(ModelApi.OpenAiCompletions, result.ModelFallbackMessage);
    }

    [Fact]
    public async Task Startup_MixedCatalog_SelectsFirstExecutable()
    {
        var runtime = await CreateRuntimeAsync(
            ("p", ModelApi.AnthropicMessages, "claude", ModelApi.AnthropicMessages),
            ("p", ModelApi.OpenAiCompletions, "gpt", ModelApi.OpenAiCompletions));
        await runtime.SetRuntimeApiKeyAsync("p", "key");

        var result = ModelStartupResolver.Resolve(
            new ModelStartupInput(
                CliProvider: null,
                CliModel: null,
                CliThinking: null,
                ScopedModels: [],
                HasExistingSession: false,
                SavedSessionModel: null,
                SavedSessionThinking: null,
                SavedSessionHasThinkingEntry: false),
            await SettingsAsync(),
            runtime);

        Assert.Equal("gpt", result.Model!.Id);
    }

    [Fact]
    public async Task SessionRestore_InexecutableSavedModel_FallsBackToExecutable()
    {
        var runtime = await CreateRuntimeAsync(
            ("p", ModelApi.AnthropicMessages, "claude", ModelApi.AnthropicMessages),
            ("p", ModelApi.OpenAiCompletions, "gpt", ModelApi.OpenAiCompletions));
        await runtime.SetRuntimeApiKeyAsync("p", "key");

        var result = ModelStartupResolver.Resolve(
            new ModelStartupInput(
                CliProvider: null,
                CliModel: null,
                CliThinking: null,
                ScopedModels: [],
                HasExistingSession: true,
                SavedSessionModel: ("p", "claude"),
                SavedSessionThinking: null,
                SavedSessionHasThinkingEntry: false),
            await SettingsAsync(),
            runtime);

        Assert.Equal("gpt", result.Model!.Id);
        Assert.NotNull(result.ModelFallbackMessage);
        Assert.Contains("claude", result.ModelFallbackMessage);
        Assert.Contains("not supported", result.ModelFallbackMessage);
    }

    [Fact]
    public async Task ResolveCliModel_InexecutableModel_FailsWithDiagnostic()
    {
        var runtime = await CreateRuntimeAsync(("p", ModelApi.AnthropicMessages, "claude", ModelApi.AnthropicMessages));
        await runtime.SetRuntimeApiKeyAsync("p", "key");

        var result = ModelResolver.ResolveCliModel(null, "p/claude", null, runtime);

        Assert.Null(result.Model);
        Assert.NotNull(result.Error);
        Assert.Contains("cannot be executed", result.Error);
        Assert.Contains(ModelApi.AnthropicMessages, result.Error);
    }
}
