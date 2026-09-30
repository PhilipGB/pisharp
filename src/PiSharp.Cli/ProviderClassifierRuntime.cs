using PiSharp.Runtime.Classifiers;

namespace PiSharp.Cli;

public sealed class ProviderClassifierRuntime(ProviderModelRuntime providers, HttpClient http)
{
    public IReadOnlyList<ClassifierModel> ListModels(string? provider = null) =>
        (provider is null ? providers.Providers : [providers.GetProvider(provider)])
        .SelectMany(profile => profile.Classifiers ?? []).ToArray();

    public async Task<ClassifierResult> ClassifyAsync(string provider, string model, ClassifierContext context,
        CancellationToken cancellationToken = default, ClassifierRequestOptions? options = null)
    {
        var descriptor = ListModels(provider).SingleOrDefault(item => item.Id == model) ??
            throw new ArgumentException($"Unknown classifier '{provider}/{model}'.");
        try
        {
            var auth = await providers.ResolveAuthAsync(provider, useRuntimeOverride: true, cancellationToken);
            if (descriptor.BaseUrl.AbsoluteUri.Contains("CLOUDFLARE_ACCOUNT_ID", StringComparison.Ordinal))
                throw new InvalidOperationException("Cloudflare Workers AI requires CLOUDFLARE_ACCOUNT_ID.");
            return await new SystemOneClassifierClient(http).ClassifyAsync(descriptor, context,
                auth.Authenticated ? auth.Key : null, cancellationToken, options).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new(descriptor.Api, provider, model, new Dictionary<string, ClassifierAnswer>(),
                cancellationToken.IsCancellationRequested ? "aborted" : "error", ErrorMessage: error.Message,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }
}
