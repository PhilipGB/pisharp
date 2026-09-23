namespace PiSharp.Cli;

/// <summary>Connection settings for OpenAI Chat Completions and opt-in llama-server testing.</summary>
public sealed record ConnectionSettings(string Model, Uri? Endpoint, string ApiKey)
{
    public const string LocalModel = "Qwen3.8-27B-GGUF";
    public const string LocalEndpoint = "http://192.168.0.97:8000/v1";

    public static ConnectionSettings Resolve(bool local, Func<string, string?> getEnvironmentVariable)
    {
        var model = getEnvironmentVariable("PISHARP_MODEL") ?? (local ? LocalModel : "gpt-4o-mini");
        var baseUrl = getEnvironmentVariable("PISHARP_BASE_URL") ?? (local ? LocalEndpoint : null);
        Uri? endpoint = null;
        if (baseUrl is not null)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("PISHARP_BASE_URL must be an absolute HTTP(S) URL (include /v1 for llama-server).");
        }

        // Never forward an unrelated OpenAI account key to a custom (possibly insecure) endpoint.
        var apiKey = endpoint is null
            ? getEnvironmentVariable("OPENAI_API_KEY") ?? getEnvironmentVariable("PISHARP_API_KEY")
            : getEnvironmentVariable("PISHARP_API_KEY") ?? "not-needed";
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException(endpoint is null
                ? "Missing OPENAI_API_KEY or PISHARP_API_KEY. Use --help for setup."
                : "PISHARP_API_KEY cannot be empty when set.");
        return new ConnectionSettings(model, endpoint, apiKey);
    }
}
