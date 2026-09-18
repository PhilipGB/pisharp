using PiSharp.Cli;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Core.Tests;

/// <summary>
/// Login/logout command semantics (pinned getLoginProviderOptions /
/// getLogoutProviderOptions / completeProviderAuthentication): which providers offer which
/// login methods, and the post-login model selection precedence (existing model stays;
/// otherwise the provider default is selected, with the pinned guidance errors).
/// </summary>
public sealed class LoginCommandTests
{
    [Fact]
    public async Task LoginOptionsListEachDeclaredAuthMethodSortedByName()
    {
        var (runtime, _) = await CreateRuntimeAsync();
        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "beta",
            Name = "Beta",
            BaseUrl = "http://localhost:9/v1",
            Auth = new ProviderAuth(
                new EnvApiKeyAuth { Name = "b", EnvironmentVariableNames = ["B_KEY"] }),
            GetModels = () => [],
            DefaultApi = "openai-completions",
        });
        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "alpha",
            Name = "Alpha",
            BaseUrl = "http://localhost:9/v1",
            Auth = new ProviderAuth(
                new EnvApiKeyAuth { Name = "a", EnvironmentVariableNames = ["A_KEY"] }),
            GetModels = () => [],
            DefaultApi = "openai-completions",
        });

        var options = LoginCommands.GetLoginProviderOptions(runtime);

        Assert.Equal(["Alpha", "Beta"], options.Select(o => o.Name).ToArray());
        Assert.All(options, option => Assert.Equal("api_key", option.AuthType));
        Assert.Empty(LoginCommands.GetLoginProviderOptions(runtime, "oauth"));
    }

    [Fact]
    public async Task LogoutOptionsListStoredCredentials()
    {
        var (runtime, store) = await CreateRuntimeAsync();
        await store.ModifyAsync("alpha", _ => Task.FromResult<Credential?>(new ApiKeyCredential("k")), CancellationToken.None);

        var options = await LoginCommands.GetLogoutProviderOptionsAsync(runtime, CancellationToken.None);

        var option = Assert.Single(options);
        Assert.Equal("alpha", option.Id);
        Assert.Equal("api_key", option.AuthType);
        Assert.Equal("stored credential", option.StatusLabel);
    }

    [Fact]
    public async Task PostLoginSelectionKeepsAnExistingModel()
    {
        var runtime = await CreateRuntimeCore();
        var current = new ModelInfo
        {
            Id = "keep",
            Name = "Keep",
            Api = "openai-completions",
            Provider = "alpha",
            BaseUrl = "http://localhost:9/v1",
            Input = ["text"],
            Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
            ContextWindow = 4_096,
            MaxTokens = 512,
        };

        var selection = LoginCommands.ResolvePostLoginSelection(
            new LoginCommands.LoginOption("openai", "OpenAI", "api_key", null), current, runtime);

        Assert.Null(selection.SelectedModel);
        Assert.Null(selection.SelectionError);
        Assert.Equal("Saved API key for OpenAI", selection.ActionLabel);
    }

    [Fact]
    public async Task PostLoginSelectionWithoutADefaultModelReportsGuidance()
    {
        var runtime = await CreateRuntimeCore();
        var selection = LoginCommands.ResolvePostLoginSelection(
            new LoginCommands.LoginOption("local", "Local", "api_key", null), null, runtime);

        Assert.Null(selection.SelectedModel);
        Assert.Equal(
            "Saved API key for Local, but no default model is configured for provider \"local\". Use /model to select a model.",
            selection.SelectionError);
    }

    [Fact]
    public async Task PostLoginSelectionWithNoAvailableModelsReportsGuidance()
    {
        var runtime = await CreateRuntimeCore();
        var selection = LoginCommands.ResolvePostLoginSelection(
            new LoginCommands.LoginOption("openai", "OpenAI", "api_key", null), null, runtime);

        Assert.Null(selection.SelectedModel);
        Assert.Equal(
            "Saved API key for OpenAI, but no models are available for that provider. Use /model to select a model.",
            selection.SelectionError);
    }

    [Fact]
    public async Task PostLoginSelectionPicksTheProviderDefaultModel()
    {
        var runtime = await CreateRuntimeCore();
        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "xai",
            Name = "XAI",
            BaseUrl = "http://localhost:9/v1",
            Auth = new ProviderAuth(
                new EnvApiKeyAuth { Name = "x", EnvironmentVariableNames = ["X_KEY"] }),
            GetModels = () => new[]
            {
                Model("xai", "grok-4.6", "Grok 4.6"),
                Model("xai", "grok-4.5", "Grok 4.5"),
            },
            DefaultApi = "openai-completions",
        });
        await runtime.SetRuntimeApiKeyAsync("xai", "k");

        var selection = LoginCommands.ResolvePostLoginSelection(
            new LoginCommands.LoginOption("xai", "XAI", "api_key", null), null, runtime);

        Assert.Equal("grok-4.6", selection.SelectedModel?.Id);
        Assert.Null(selection.SelectionError);
    }

    private static ModelInfo Model(string provider, string id, string name) => new()
    {
        Id = id,
        Name = name,
        Api = "openai-completions",
        Provider = provider,
        BaseUrl = "http://localhost:9/v1",
        Input = ["text"],
        Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
        ContextWindow = 4_096,
        MaxTokens = 512,
    };

    private static Task<ModelRuntime> CreateRuntimeCore() => ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
    {
        Credentials = new InMemoryCredentialStore(),
        ModelsStore = new InMemoryModelsStore(),
        Builtins = [],
        NetworkEnabled = false,
        RefreshOnCreate = false,
    });

    private static async Task<(ModelRuntime Runtime, InMemoryCredentialStore Store)> CreateRuntimeAsync()
    {
        var store = new InMemoryCredentialStore();
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = store,
            ModelsStore = new InMemoryModelsStore(),
            Builtins = [],
            NetworkEnabled = false,
            RefreshOnCreate = false,
        });
        return (runtime, store);
    }
}
