using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Non-secret AWS configuration and credential-chain presence for Bedrock.</summary>
public sealed record BedrockProviderOptions(string? Region, string? Profile, string? AmbientAuthSource,
    bool EnablePromptCaching = true, bool OneHourPromptCache = false, bool ForcePromptCaching = false,
    string? ProfileLocation = null)
{
    public const string BearerTokenEnvironment = "AWS_BEARER_TOKEN_BEDROCK";
    public const string BearerTokenAuthSource = "AWS_BEARER_TOKEN_BEDROCK";

    public static BedrockProviderOptions FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var profile = NonBlank(environment("AWS_PROFILE"));
        var region = NonBlank(environment("AWS_REGION")) ?? NonBlank(environment("AWS_DEFAULT_REGION"));
        var authSource = ResolveAmbientAuthSource(environment, profile);
        var cacheRetention = NonBlank(environment("PI_CACHE_RETENTION"));
        return new(region, profile, authSource, cacheRetention != "none", cacheRetention == "long",
            environment("AWS_BEDROCK_FORCE_CACHE") == "1",
            NonBlank(environment("AWS_CONFIG_FILE")) ?? NonBlank(environment("AWS_SHARED_CREDENTIALS_FILE")));
    }

    internal AmazonBedrockRuntimeConfig CreateClientConfig(ModelDescriptor model, string? bearerToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var baseUrl = model.BaseUrl ?? "https://bedrock-runtime.us-east-1.amazonaws.com";
        var endpointRegion = GetStandardEndpointRegion(baseUrl);
        var modelRegion = GetModelArnRegion(model.Id);
        var hasConfiguredProfile = !string.IsNullOrWhiteSpace(Profile);
        var isStandardEndpoint = IsStandardEndpoint(baseUrl);
        var useExplicitEndpoint = !isStandardEndpoint ||
            (Region is null && !hasConfiguredProfile && endpointRegion is not null);
        var region = modelRegion ?? Region ??
            (endpointRegion is not null && useExplicitEndpoint ? endpointRegion : null) ??
            (!hasConfiguredProfile ? "us-east-1" : null);

        var config = new AmazonBedrockRuntimeConfig
        {
            MaxErrorRetry = 0
        };
        if (hasConfiguredProfile)
        {
            config.Profile = ProfileLocation is null ? new Profile(Profile!) : new Profile(Profile!, ProfileLocation);
            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                var profileChain = new CredentialProfileStoreChain(ProfileLocation);
                if (!profileChain.TryGetAWSCredentials(Profile!, out var credentials))
                    throw new InvalidOperationException($"AWS profile '{Profile}' could not be resolved from its configured profile store.");
                // The AWS .NET default chain checks access-key environment variables before profiles.
                // Bind a selected profile explicitly so ambient keys cannot take over Pi's choice.
                config.DefaultAWSCredentials = credentials;
            }
        }

        if (useExplicitEndpoint)
        {
            config.ServiceURL = baseUrl;
            if (region is not null) config.AuthenticationRegion = region;
        }
        else if (region is not null)
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            config.AWSTokenProvider = new StaticBedrockTokenProvider(bearerToken);
            config.AuthSchemePreference = ["httpBearerAuth"];
        }
        return config;
    }

    internal static bool IsStandardEndpoint(string value) => GetStandardEndpointRegion(value) is not null;

    private static string? ResolveAmbientAuthSource(Func<string, string?> environment, string? profile)
    {
        var accessKey = NonBlank(environment("AWS_ACCESS_KEY_ID"));
        var secretKey = NonBlank(environment("AWS_SECRET_ACCESS_KEY"));
        if (profile is not null) return "AWS_PROFILE";
        if (accessKey is not null && secretKey is not null) return "AWS access keys";
        if (NonBlank(environment("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI")) is not null ||
            NonBlank(environment("AWS_CONTAINER_CREDENTIALS_FULL_URI")) is not null)
            return "ECS task role";
        if (NonBlank(environment("AWS_WEB_IDENTITY_TOKEN_FILE")) is not null) return "web identity token";
        try
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials("default", out _)) return "AWS default profile";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
        {
            // Auth status is intentionally local and best-effort; the SDK remains the authority
            // when it resolves the complete ambient provider chain for an actual request.
        }
        return null;
    }

    internal static string? GetModelArnRegion(string modelId)
    {
        var match = System.Text.RegularExpressions.Regex.Match(modelId,
            "^arn:aws(?:-[a-z0-9-]+)?:bedrock:([a-z0-9-]+):", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? GetStandardEndpointRegion(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(uri.Host,
            "^bedrock-runtime(?:-fips)?\\.([a-z0-9-]+)\\.amazonaws\\.com(?:\\.cn)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class StaticBedrockTokenProvider(string token) : IAWSTokenProvider
    {
        public Task<TryResponse<AWSToken>> TryResolveTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TryResponse<AWSToken>
            {
                Success = true,
                Value = new AWSToken { Token = token }
            });
        }
    }
}
