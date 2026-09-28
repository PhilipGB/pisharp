using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Microsoft.Extensions.AI;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class BedrockModelCatalogTests
{
    [Fact]
    public void CatalogMatchesCurrentPiAndRetainsModelCapabilities()
    {
        var models = BedrockModelCatalog.Load();
        var claude = Assert.Single(models, model => model.Id == "global.anthropic.claude-sonnet-4-6");
        var nova = Assert.Single(models, model => model.Id == "amazon.nova-2-lite-v1:0");

        Assert.Equal(174, models.Count);
        Assert.All(models, model =>
        {
            Assert.Equal("amazon-bedrock", model.Provider);
            Assert.Equal("bedrock-converse-stream", model.Api);
            Assert.NotNull(model.BaseUrl);
        });
        Assert.Equal(1_000_000, claude.ContextLength);
        Assert.Equal(128_000, claude.MaxOutputTokens);
        Assert.Equal(["text", "image"], claude.Input);
        Assert.Equal(3m, claude.Pricing?.Input);
        Assert.True(nova.Reasoning);
        Assert.NotNull(nova.InputLimits?.Images?.Resize);
    }

    [Fact]
    public async Task ClientConfigurationPreservesProfileRegionEndpointAndBearerPrecedence()
    {
        var model = BedrockModelCatalog.Load().Single(item => item.Id == "global.anthropic.claude-sonnet-4-6");
        var profilePath = Path.Combine(Path.GetTempPath(), "pisharp-bedrock-profile-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(profilePath,
            "[work-sso]\naws_access_key_id=profile-access-key\naws_secret_access_key=profile-secret-key\n");
        try
        {
            var profiled = new BedrockProviderOptions(null, "work-sso", "AWS_PROFILE", ProfileLocation: profilePath);
            var profileConfig = profiled.CreateClientConfig(model, null);
            Assert.Equal("work-sso", profileConfig.Profile?.Name);
            Assert.NotNull(profileConfig.DefaultAWSCredentials);
            Assert.Null(profileConfig.ServiceURL);
            Assert.Null(profileConfig.RegionEndpoint);
            var credentials = await profileConfig.DefaultAWSCredentials.GetCredentialsAsync();
            Assert.Equal("profile-access-key", credentials.AccessKey);
        }
        finally { File.Delete(profilePath); }

        var regional = new BedrockProviderOptions("eu-west-1", null, null);
        var regionalConfig = regional.CreateClientConfig(model, null);
        Assert.Equal("eu-west-1", regionalConfig.RegionEndpoint?.SystemName);
        Assert.Null(regionalConfig.ServiceURL);

        const string bearer = "bedrock-fixture-bearer-token";
        var bearerConfig = regional.CreateClientConfig(model, bearer);
        Assert.Contains("httpBearerAuth", bearerConfig.AuthSchemePreference);
        var resolvedToken = await bearerConfig.AWSTokenProvider!.TryResolveTokenAsync();
        Assert.True(resolvedToken.Success);
        Assert.Equal(bearer, resolvedToken.Value.Token);
    }

    [Fact]
    public void AmbientCredentialStatusKeepsBearerAndProfileSourcesDistinct()
    {
        var bearer = BedrockProviderOptions.FromEnvironment(name => name switch
        {
            "AWS_BEARER_TOKEN_BEDROCK" => "secret-value",
            _ => null
        });
        var profile = BedrockProviderOptions.FromEnvironment(name => name switch
        {
            "AWS_PROFILE" => "work-sso",
            "AWS_ACCESS_KEY_ID" => "ambient-access-key",
            "AWS_SECRET_ACCESS_KEY" => "ambient-secret-key",
            _ => null
        });

        Assert.Null(bearer.AmbientAuthSource);
        Assert.Equal("work-sso", profile.Profile);
        Assert.Equal("AWS_PROFILE", profile.AmbientAuthSource);
    }
}
