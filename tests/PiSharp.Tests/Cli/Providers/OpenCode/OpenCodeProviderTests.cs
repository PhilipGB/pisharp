using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class OpenCodeProviderTests
{
    private const string FixtureKey = "opencode-fixture-secret-key";

    [Fact]
    public async Task BuiltInOpenCodeProfilesExposePinnedPiCatalogues()
    {
        var root = NewRoot();
        try
        {
            using var http = new HttpClient();
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, EnvironmentWithKey, http, offline: true);

            var zen = runtime.GetProvider("opencode");
            Assert.Equal("OpenCode Zen", zen.Name);
            Assert.Equal("https://opencode.ai/inference", zen.Endpoint.AbsoluteUri);
            Assert.Equal("OPENCODE_API_KEY", zen.ApiKeyEnvironment);
            Assert.True(zen.ApiKeySupported);
            Assert.False(zen.OAuthSupported);
            Assert.True(zen.AuthRequired);

            var go = runtime.GetProvider("opencode-go");
            Assert.Equal("OpenCode Go", go.Name);
            Assert.Equal("https://opencode.ai/inference/go", go.Endpoint.AbsoluteUri);
            Assert.Equal("OPENCODE_API_KEY", go.ApiKeyEnvironment);
            Assert.True(go.ApiKeySupported);
            Assert.False(go.OAuthSupported);
            Assert.NotEqual(zen.Endpoint, go.Endpoint);

            var zenModels = await runtime.ListModelsAsync("opencode");
            var goModels = await runtime.ListModelsAsync("opencode-go");
            Assert.Equal(79, zenModels.Count);
            Assert.Equal(30, goModels.Count);
            Assert.All(zenModels, model => Assert.True(model.Available, model.Id));
            Assert.All(goModels, model => Assert.True(model.Available, model.Id));
            Assert.Equal("kimi-k2.6", zenModels[0].Id);
            Assert.Equal("kimi-k3", goModels[0].Id);
            Assert.Equal(ExpectedZenCatalog.Trim(), Catalog(zenModels));
            Assert.Equal(ExpectedGoCatalog.Trim(), Catalog(goModels));

            var haiku = Assert.Single(goModels, model => model.Id == "claude-haiku-5-5");
            Assert.Equal("anthropic-messages", haiku.Api);
            Assert.Equal("https://opencode.ai/inference/go", haiku.BaseUrl);
            Assert.Equal(1_000_000, haiku.ContextLength);
            Assert.Equal(128_000, haiku.MaxOutputTokens);
            Assert.True(haiku.Reasoning);
            Assert.Equal(["text", "image"], haiku.Input);
            Assert.Equal(0.1m, haiku.Pricing!.Input);
            Assert.Equal(0.5m, haiku.Pricing.Output);
            Assert.Equal(0.01m, haiku.Pricing.CachedInput);
            Assert.Equal(0.125m, haiku.Pricing.CachedWrite);
            var haikuTier = Assert.Single(haiku.Pricing.Tiers!);
            Assert.Equal(100_000, haikuTier.InputTokensAbove);
            Assert.Equal(0.5m, haikuTier.Input);
            Assert.Equal(2.5m, haikuTier.Output);
            Assert.Equal(0.05m, haikuTier.CachedInput);
            Assert.Equal(0.625m, haikuTier.CachedWrite);
            AssertCompatibility(haiku, "forceAdaptiveThinking", true);
            AssertCompatibility(haiku, "supportsTemperature", false);
            Assert.Equal("max", ThinkingMap(haiku, "max"));
            Assert.Equal("xhigh", ThinkingMap(haiku, "xhigh"));

            var google = Assert.Single(goModels, model => model.Id == "gemini-4-argon");
            Assert.Equal("google-generative-ai", google.Api);
            Assert.Equal("https://opencode.ai/inference/go/v1", google.BaseUrl);
            Assert.Equal(1_048_576, google.ContextLength);
            Assert.Equal(65_536, google.MaxOutputTokens);

            var step = Assert.Single(goModels, model => model.Id == "step-5-preview");
            Assert.Equal("openai-completions", step.Api);
            AssertCompatibility(step, "maxTokensField", "max_tokens");
            AssertCompatibility(step, "supportsDeveloperRole", false);
            AssertCompatibility(step, "supportsStore", false);
            AssertCompatibility(step, "supportsStrictMode", true);
            Assert.Null(ThinkingMap(step, "off"));

            var deepSeek = Assert.Single(goModels, model => model.Id == "deepseek-v4.1-flash");
            AssertCompatibility(deepSeek, "requiresReasoningContentOnAssistantMessages", true);
            AssertCompatibility(deepSeek, "thinkingFormat", "deepseek");

            var luna = Assert.Single(goModels, model => model.Id == "gpt-6-luna");
            Assert.Equal("openai-responses", luna.Api);
            Assert.Equal("https://opencode.ai/inference/go/v1", luna.BaseUrl);
            AssertCompatibility(luna, "sessionAffinityFormat", "openai-nosession");
            Assert.Equal("max", ThinkingMap(luna, "max"));

            var qwen = Assert.Single(goModels, model => model.Id == "qwen3.8-flash");
            AssertCompatibility(qwen, "allowEmptySignature", true);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OpenCodeGoQualifiedSelectionUsesPinnedProtocolAndModelEndpoint()
    {
        var root = NewRoot();
        try
        {
            using var http = new HttpClient();
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, EnvironmentWithKey, http, offline: true);

            var haiku = await runtime.ResolveAsync("opencode-go", "claude-haiku-5-5");
            Assert.Equal("opencode-go", haiku.Provider.Id);
            Assert.Equal("claude-haiku-5-5", haiku.Model.Id);
            Assert.Equal("anthropic-messages", ProviderChatClientFactory.ResolveProtocol(haiku));
            Assert.Equal("https://opencode.ai/inference/go", haiku.Connection.Endpoint!.AbsoluteUri);
            Assert.Equal(FixtureKey, haiku.ApiKey);
            Assert.Equal("OPENCODE_API_KEY", haiku.AuthSource);

            var qualified = await runtime.ResolveAsync(null, "opencode-go/gemini-4-argon");
            Assert.Equal("opencode-go", qualified.Provider.Id);
            Assert.Equal("google-generative-ai", ProviderChatClientFactory.ResolveProtocol(qualified));
            Assert.Equal("https://opencode.ai/inference/go/v1", qualified.Connection.Endpoint!.AbsoluteUri);

            var zen = await runtime.ResolveAsync(null, "opencode/gemini-3.5-flash");
            Assert.Equal("opencode", zen.Provider.Id);
            Assert.Equal("google-generative-ai", ProviderChatClientFactory.ResolveProtocol(zen));
            Assert.Equal("https://opencode.ai/inference/v1", zen.Connection.Endpoint!.AbsoluteUri);

            var protocols = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claude-haiku-5-5"] = "anthropic-messages",
                ["gemini-4-argon"] = "google-generative-ai",
                ["kimi-k3"] = "openai-completions",
                ["gpt-6-luna"] = "openai-responses"
            };
            foreach (var (modelId, protocol) in protocols)
                Assert.Equal(protocol, ProviderChatClientFactory.ResolveProtocol(
                    await runtime.ResolveAsync("opencode-go", modelId)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StoredOpenCodeCredentialIsScopedAndLogoutLeavesSiblingsIntact()
    {
        var root = NewRoot();
        try
        {
            using var http = new HttpClient();
            var authPath = Path.Combine(root, "auth.json");
            var storage = new AuthStorage(authPath);
            await storage.StoreApiKeyAsync("opencode", "stored-zen-key");
            await storage.StoreApiKeyAsync("opencode-go", "stored-go-key");
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, EnvironmentWithKey, http, offline: true);

            var zen = await runtime.ResolveAuthAsync("opencode");
            Assert.Equal("stored-zen-key", zen.Key);
            Assert.Equal("stored API key", zen.Source);

            var go = await runtime.ResolveAuthAsync("opencode-go");
            Assert.Equal("stored API key", go.Source);
            Assert.Equal("stored-go-key", go.Key);

            var unrelated = await runtime.ResolveAuthAsync("anthropic");
            Assert.True(!unrelated.Authenticated, $"source={unrelated.Source};key={unrelated.Key}");

            Assert.True(await runtime.LogoutAsync("opencode"));
            var zenAfterLogout = await runtime.ResolveAuthAsync("opencode");
            Assert.Equal("OPENCODE_API_KEY", zenAfterLogout.Source);
            Assert.Equal(FixtureKey, zenAfterLogout.Key);
            Assert.Null(await storage.ReadAsync("opencode"));
            Assert.Equal("stored-go-key", (await storage.ReadAsync("opencode-go"))!.Key);

            Assert.True(await runtime.LogoutAsync("opencode-go"));
            Assert.Null(await storage.ReadAsync("opencode-go"));
            var goAfterLogout = await runtime.ResolveAuthAsync("opencode-go");
            Assert.Equal("OPENCODE_API_KEY", goAfterLogout.Source);
            Assert.Equal(FixtureKey, goAfterLogout.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private const string ExpectedGoCatalog = """
beam-free|Beam Free|openai-completions|https://opencode.ai/inference/go/v1|1048576|131072|text
claude-haiku-5-5|Claude Haiku 5.5|anthropic-messages|https://opencode.ai/inference/go|1000000|128000|text,image
deepseek-v4-pro|DeepSeek V4 Pro (New)|openai-completions|https://opencode.ai/inference/go/v1|1000000|384000|text
deepseek-v4.1-flash|DeepSeek V4.1 Flash|openai-completions|https://opencode.ai/inference/go/v1|1000000|384000|text,image
gemini-4-argon|Gemini 4 Argon|google-generative-ai|https://opencode.ai/inference/go/v1|1048576|65536|text,image
glm-5.2|GLM-5.2|openai-completions|https://opencode.ai/inference/go/v1|1000000|131072|text
glm-5.3|GLM-5.3|openai-completions|https://opencode.ai/inference/go/v1|1000000|131072|text
glm-5.3-flash|GLM-5.3-Flash|openai-completions|https://opencode.ai/inference/go/v1|1000000|131072|text,image
gpt-5.6-luna|GPT-5.6 Luna|openai-responses|https://opencode.ai/inference/go/v1|1050000|128000|text,image
gpt-6-luna|GPT-6 Luna|openai-responses|https://opencode.ai/inference/go/v1|1050000|128000|text,image
grok-4.7|Grok 4.7|openai-responses|https://opencode.ai/inference/go/v1|500000|500000|text,image
hy3|Hy3|openai-completions|https://opencode.ai/inference/go/v1|256000|128000|text
hy4-preview|Hy4 preview|openai-completions|https://opencode.ai/inference/go/v1|1024000|64000|text
iceman-contributor-free|Iceman Contributor Free|openai-completions|https://opencode.ai/inference/go/v1|262144|131072|text
kimi-k2.7-code|Kimi K2.7 Code|openai-completions|https://opencode.ai/inference/go/v1|262144|262144|text,image
kimi-k3|Kimi K3|openai-completions|https://opencode.ai/inference/go/v1|1048576|131072|text,image
kimi-k3.1-pro|Kimi K3.1 Pro|openai-completions|https://opencode.ai/inference/go/v1|1048576|131072|text,image
longcat-2.0|LongCat-2.0|openai-completions|https://opencode.ai/inference/go/v1|1000000|131072|text
mimo-v2.5|MiMo V2.5|openai-completions|https://opencode.ai/inference/go/v1|1000000|128000|text,image
mimo-v2.5-pro|MiMo V2.5 Pro|openai-completions|https://opencode.ai/inference/go/v1|1048576|128000|text
mimo-v2.6-flash|MiMo-V2.6-Flash|openai-completions|https://opencode.ai/inference/go/v1|1048576|131072|text,image
mimo-v2.6-pro|MiMo-V2.6-Pro|openai-completions|https://opencode.ai/inference/go/v1|1048576|131072|text,image
minimax-m3|MiniMax-M3|anthropic-messages|https://opencode.ai/inference/go|1000000|131072|text,image
minimax-m3.1-flash|MiniMax M3.1 Flash|openai-completions|https://opencode.ai/inference/go/v1|1000000|512000|text,image
muse-spark-1.2-contributor|Muse Spark 1.2 Contributor|openai-responses|https://opencode.ai/inference/go/v1|1048576|131072|text,image
muse-spark-1.3-contributor|Muse Spark 1.3 Contributor|openai-responses|https://opencode.ai/inference/go/v1|1048576|131072|text,image
qwen3.7-plus|Qwen3.7 Plus|anthropic-messages|https://opencode.ai/inference/go|1000000|65536|text,image
qwen3.8-flash|Qwen3.8 Flash|anthropic-messages|https://opencode.ai/inference/go|1000000|131072|text,image
qwen3.8-max|Qwen3.8 Max|anthropic-messages|https://opencode.ai/inference/go|1000000|131072|text,image
step-5-preview|Step 5 Preview|openai-completions|https://opencode.ai/inference/go/v1|1000000|65536|text,image
""";

    private const string ExpectedZenCatalog = """
beam-free|Beam Free|openai-completions|https://opencode.ai/inference/v1|1048576|131072|text
big-pickle|Big Pickle|openai-completions|https://opencode.ai/inference/v1|200000|32000|text
claude-fable-5|Claude Fable 5|anthropic-messages|https://opencode.ai/inference|1000000|128000|text,image
claude-fable-5-1|Claude Fable 5.1|anthropic-messages|https://opencode.ai/inference|1000000|128000|text,image
claude-haiku-4-5|Claude Haiku 4.5|anthropic-messages|https://opencode.ai/inference|200000|64000|text,image
claude-haiku-5-5|Claude Haiku 5.5|anthropic-messages|https://opencode.ai/inference|1000000|128000|text,image
claude-opus-4-5|Claude Opus 4.5|anthropic-messages|https://opencode.ai/inference|200000|64000|text,image
claude-opus-4-6|Claude Opus 4.6|anthropic-messages|https://opencode.ai/inference|1000000|128000|text,image
claude-opus-4-7|Claude Opus 4.7|anthropic-messages|https://opencode.ai/inference|1000000|128000|text,image
claude-opus-4-8|Claude Opus 4.8|anthropic-messages|https://opencode.ai/inference|1000000|128000|text,image
claude-opus-5|Claude Opus 5|anthropic-messages|https://opencode.ai/inference|1000000|128000|text,image
claude-opus-5-5|Claude Opus 5.5|anthropic-messages|https://opencode.ai/inference|1000000|128000|text,image
claude-sonnet-4-5|Claude Sonnet 4.5|anthropic-messages|https://opencode.ai/inference|200000|64000|text,image
claude-sonnet-4-6|Claude Sonnet 4.6|anthropic-messages|https://opencode.ai/inference|1000000|64000|text,image
claude-sonnet-5|Claude Sonnet 5|anthropic-messages|https://opencode.ai/inference|1000000|128000|text,image
claude-sonnet-5-5|Claude Sonnet 5.5|anthropic-messages|https://opencode.ai/inference|1000000|128000|text,image
deepseek-v4-pro|DeepSeek V4 Pro|openai-completions|https://opencode.ai/inference/v1|1000000|384000|text
deepseek-v4.1-flash|DeepSeek V4.1 Flash|openai-completions|https://opencode.ai/inference/v1|1000000|384000|text,image
deepseek-v4.1-flash-free|DeepSeek V4.1 Flash Free|openai-completions|https://opencode.ai/inference/v1|200000|32000|text,image
flint-40b-free|Flint 40B Free|openai-completions|https://opencode.ai/inference/v1|262144|65536|text
gemini-3-flash|Gemini 3 Flash|google-generative-ai|https://opencode.ai/inference/v1|1048576|65536|text,image
gemini-3.1-pro|Gemini 3.1 Pro Preview|google-generative-ai|https://opencode.ai/inference/v1|1048576|65536|text,image
gemini-3.5-flash|Gemini 3.5 Flash|google-generative-ai|https://opencode.ai/inference/v1|1048576|65536|text,image
gemini-3.5-flash-lite|Gemini 3.5 Flash Lite|google-generative-ai|https://opencode.ai/inference/v1|1048576|65536|text,image
gemini-3.6-flash|Gemini 3.6 Flash|google-generative-ai|https://opencode.ai/inference/v1|1048576|65536|text,image
gemini-3.7-flash|Gemini 3.7 Flash|google-generative-ai|https://opencode.ai/inference/v1|1048576|65536|text,image
gemini-3.8-flash|Gemini 3.8 Flash|google-generative-ai|https://opencode.ai/inference/v1|1048576|65536|text,image
gemini-4-argon|Gemini 4 Argon|google-generative-ai|https://opencode.ai/inference/v1|1048576|65536|text,image
glm-5|GLM-5|openai-completions|https://opencode.ai/inference/v1|204800|131072|text
glm-5.1|GLM-5.1|openai-completions|https://opencode.ai/inference/v1|204800|131072|text
glm-5.2|GLM-5.2|openai-completions|https://opencode.ai/inference/v1|1000000|131072|text
glm-5.3|GLM-5.3|openai-completions|https://opencode.ai/inference/v1|1000000|131072|text
glm-5.3-flash|GLM-5.3-Flash|openai-completions|https://opencode.ai/inference/v1|1000000|131072|text,image
glm-5.3-flash-free|GLM-5.3-Flash Free|openai-completions|https://opencode.ai/inference/v1|200000|32000|text,image
gpt-5|GPT-5|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5-codex|GPT-5 Codex|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5-nano|GPT-5 Nano|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5.1|GPT-5.1|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5.1-codex|GPT-5.1 Codex|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5.1-codex-max|GPT-5.1 Codex Max|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5.1-codex-mini|GPT-5.1 Codex Mini|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5.2|GPT-5.2|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5.2-codex|GPT-5.2 Codex|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5.3-codex|GPT-5.3 Codex|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5.4|GPT-5.4|openai-responses|https://opencode.ai/inference/v1|272000|128000|text,image
gpt-5.4-mini|GPT-5.4 Mini|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5.4-nano|GPT-5.4 Nano|openai-responses|https://opencode.ai/inference/v1|400000|128000|text,image
gpt-5.4-pro|GPT-5.4 Pro|openai-responses|https://opencode.ai/inference/v1|1050000|128000|text,image
gpt-5.5|GPT-5.5|openai-responses|https://opencode.ai/inference/v1|1050000|128000|text,image
gpt-5.5-pro|GPT-5.5 Pro|openai-responses|https://opencode.ai/inference/v1|1050000|128000|text,image
gpt-5.6-luna|GPT-5.6 Luna|openai-responses|https://opencode.ai/inference/v1|1050000|128000|text,image
gpt-5.6-sol|GPT-5.6 Sol|openai-responses|https://opencode.ai/inference/v1|1050000|128000|text,image
gpt-5.6-terra|GPT-5.6 Terra|openai-responses|https://opencode.ai/inference/v1|1050000|128000|text,image
gpt-6-astra|GPT-6 Astra|openai-responses|https://opencode.ai/inference/v1|1050000|128000|text,image
gpt-6-luna|GPT-6 Luna|openai-responses|https://opencode.ai/inference/v1|1050000|128000|text,image
gpt-6-sol|GPT-6 Sol|openai-responses|https://opencode.ai/inference/v1|1050000|128000|text,image
gpt-6.1-sol|GPT-6.1 Sol|openai-responses|https://opencode.ai/inference/v1|1050000|128000|text,image
grok-4.5|Grok 4.5|openai-responses|https://opencode.ai/inference/v1|500000|500000|text,image
grok-4.6|Grok 4.6|openai-responses|https://opencode.ai/inference/v1|500000|500000|text,image
grok-4.7|Grok 4.7|openai-responses|https://opencode.ai/inference/v1|500000|500000|text,image
grok-build-0.1|Grok Build 0.1|openai-responses|https://opencode.ai/inference/v1|256000|256000|text,image
kimi-k2.5|Kimi K2.5|openai-completions|https://opencode.ai/inference/v1|262144|65536|text,image
kimi-k2.6|Kimi K2.6|openai-completions|https://opencode.ai/inference/v1|262144|65536|text,image
kimi-k2.7-code|Kimi K2.7 Code|openai-completions|https://opencode.ai/inference/v1|262144|262144|text,image
kimi-k3|Kimi K3|openai-completions|https://opencode.ai/inference/v1|1048576|131072|text,image
mimo-v2.6-flash-free|MiMo-V2.6-Flash Free|openai-completions|https://opencode.ai/inference/v1|200000|32000|text,image
minimax-m2.5|MiniMax-M2.5|openai-completions|https://opencode.ai/inference/v1|204800|131072|text
minimax-m2.7|MiniMax-M2.7|openai-completions|https://opencode.ai/inference/v1|204800|131072|text
minimax-m3|MiniMax-M3|openai-completions|https://opencode.ai/inference/v1|512000|128000|text,image
mistral-large-4|Mistral Large 4|openai-completions|https://opencode.ai/inference/v1|524288|262144|text,image
muse-spark-1.2|Muse Spark 1.2|openai-responses|https://opencode.ai/inference/v1|1048576|131072|text,image
muse-spark-1.3|Muse Spark 1.3|openai-responses|https://opencode.ai/inference/v1|1048576|131072|text,image
muse-wonder-1.0|Muse Wonder|openai-responses|https://opencode.ai/inference/v1|1048576|131072|text,image
nemotron-3.5-lightning-free|Nemotron 3.5 Lightning Free|openai-completions|https://opencode.ai/inference/v1|262144|262144|text
nemotron-3.5-super-vl-free|Nemotron 3.5 Super VL Free|openai-completions|https://opencode.ai/inference/v1|1048576|131072|text,image
qwen3.5-plus|Qwen3.5 Plus|anthropic-messages|https://opencode.ai/inference|262144|65536|text,image
qwen3.6-plus|Qwen3.6 Plus|anthropic-messages|https://opencode.ai/inference|262144|65536|text,image
qwen3.8-flash|Qwen3.8 Flash|anthropic-messages|https://opencode.ai/inference|1000000|131072|text,image
qwen3.8-max|Qwen3.8 Max|openai-completions|https://opencode.ai/inference/v1|262144|131072|text,image
""";

    [Theory]
    [InlineData("opencode-go")]
    [InlineData("OpenCode-Go")]
    public async Task ModelsJsonCanOverrideOpenCodeGoAsASiblingSharingTheCredential(string providerKey)
    {
        var root = NewRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), """
                {"providers":{"__PROVIDER__":{"name":"OpenCode Go custom",
                 "baseUrl":"https://opencode.ai/inference/go"}}}
                """.Replace("__PROVIDER__", providerKey, StringComparison.Ordinal));
            using var http = new HttpClient();
            var runtime = await ProviderModelRuntime.CreateAsync(root, false, EnvironmentWithKey, http, offline: true);
            var provider = runtime.GetProvider("opencode-go");
            Assert.Equal("OpenCode Go custom", provider.Name);
            Assert.Equal("OPENCODE_API_KEY", provider.ApiKeyEnvironment);
            Assert.Equal("https://opencode.ai/inference/go", provider.Endpoint.AbsoluteUri);
            Assert.Equal(30, provider.Models.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Func<string, string?> EnvironmentWithKey => name => name switch
    {
        "OPENCODE_API_KEY" => FixtureKey,
        _ => null
    };

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-opencode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Catalog(IEnumerable<ModelDescriptor> models) => string.Join('\n', models
        .OrderBy(model => model.Id, StringComparer.Ordinal)
        .Select(model => string.Join('|', model.Id, model.Name, model.Api, model.BaseUrl,
            model.ContextLength?.ToString(), model.MaxOutputTokens?.ToString(), string.Join(',', model.Input ?? []))));

    private static void AssertCompatibility(ModelDescriptor model, string name, object expected)
    {
        Assert.Equal(JsonValueKind.Object, model.Compatibility?.ValueKind);
        Assert.True(model.Compatibility!.Value.TryGetProperty(name, out var value), $"{model.Id} has no {name}.");
        Assert.Equal(expected, value.ValueKind == JsonValueKind.Null ? null :
            value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetBoolean());
    }

    private static string? ThinkingMap(ModelDescriptor model, string level)
    {
        Assert.Equal(JsonValueKind.Object, model.ThinkingLevelMap?.ValueKind);
        return model.ThinkingLevelMap!.Value.TryGetProperty(level, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? SessionHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SessionHeader = request.Headers.TryGetValues("x-opencode-session", out var values)
                ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
