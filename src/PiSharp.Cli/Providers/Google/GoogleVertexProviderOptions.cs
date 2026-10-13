using Google.Apis.Auth.OAuth2;

namespace PiSharp.Cli;

/// <summary>Project-scoped ambient configuration for the Google Vertex provider.</summary>
public sealed record GoogleVertexProviderOptions(string? Project, string? Location, string? CredentialsFile)
{
    public const string AdcAuthSource = "Google Vertex Application Default Credentials";
    internal const string AdcCredentialMarker = "gcp-vertex-credentials";
    internal const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    public static GoogleVertexProviderOptions FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new(
            First(environment("GOOGLE_CLOUD_PROJECT"), environment("GCLOUD_PROJECT")),
            environment("GOOGLE_CLOUD_LOCATION"),
            environment("GOOGLE_APPLICATION_CREDENTIALS"));
    }

    internal bool HasConfiguredApplicationDefaultCredentials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Project) || string.IsNullOrWhiteSpace(Location)) return false;
            return File.Exists(ResolveCredentialsFilePath(CredentialsFile));
        }
    }

    private static string? First(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;

    internal static string ResolveCredentialsFilePath(string? credentialsFile)
    {
        if (!string.IsNullOrWhiteSpace(credentialsFile)) return ExpandPath(credentialsFile);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "gcloud", "application_default_credentials.json");
    }

    private static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]);
        return expanded;
    }
}

internal sealed record GoogleVertexRequestOptions(string? Project, string? Location,
    bool IncludeProjectLocation, string ApiVersion = "v1",
    Func<CancellationToken, Task<string>>? AccessTokenProvider = null);

internal sealed class GoogleVertexAccessTokenProvider(GoogleVertexProviderOptions options)
{
    private readonly Lazy<Task<GoogleCredential>> _credential = new(() => LoadCredentialAsync(options),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var credential = await _credential.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (credential.IsCreateScopedRequired)
            credential = credential.CreateScoped(GoogleVertexProviderOptions.CloudPlatformScope);
        if (credential.UnderlyingCredential is not ITokenAccess tokenAccess)
            throw new InvalidOperationException("The configured Google credential cannot provide access tokens.");
        return await tokenAccess.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<GoogleCredential> LoadCredentialAsync(GoogleVertexProviderOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CredentialsFile))
            return await CredentialFactory.FromFileAsync<GoogleCredential>(
                GoogleVertexProviderOptions.ResolveCredentialsFilePath(options.CredentialsFile), CancellationToken.None)
                .ConfigureAwait(false);
        return await GoogleCredential.GetApplicationDefaultAsync().ConfigureAwait(false);
    }
}
