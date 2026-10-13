namespace PiSharp.Cli;

internal static class AzureOpenAiEndpoint
{
    private const string MissingEndpointHost = "azure-openai-endpoint-required.invalid";
    private static readonly Uri MissingEndpoint = new($"https://{MissingEndpointHost}/openai/v1");

    public static Uri FromEnvironment(Func<string, string?> environment)
    {
        var configured = environment("AZURE_OPENAI_BASE_URL")?.Trim();
        if (!string.IsNullOrEmpty(configured)) return ParseAndNormalize(configured);

        var resourceName = environment("AZURE_OPENAI_RESOURCE_NAME")?.Trim();
        if (string.IsNullOrEmpty(resourceName)) return MissingEndpoint;
        if (resourceName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("AZURE_OPENAI_RESOURCE_NAME must contain only ASCII letters, digits, and hyphens.");
        return new Uri($"https://{resourceName}.openai.azure.com/openai/v1");
    }

    public static Uri RequireConfigured(Uri endpoint)
    {
        if (endpoint.Host.Equals(MissingEndpointHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Azure OpenAI requires AZURE_OPENAI_BASE_URL or AZURE_OPENAI_RESOURCE_NAME, or a model baseUrl.");
        return Normalize(endpoint);
    }

    private static Uri ParseAndNormalize(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
            throw new ArgumentException("AZURE_OPENAI_BASE_URL must be an absolute HTTP(S) URL.");
        return Normalize(endpoint);
    }

    private static Uri Normalize(Uri endpoint)
    {
        if (endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Azure OpenAI endpoint must be an absolute HTTP(S) URL without user info or a fragment.");

        var isAzureHost = endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Host.EndsWith(".ai.azure.com", StringComparison.OrdinalIgnoreCase);
        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (isAzureHost && (path is "" or "/openai" or "/openai/v1/responses"))
        {
            var normalized = new UriBuilder(endpoint)
            {
                Path = "/openai/v1",
                Query = ""
            };
            return normalized.Uri;
        }

        return new UriBuilder(endpoint) { Path = path.Length == 0 ? "/" : path }.Uri;
    }
}
