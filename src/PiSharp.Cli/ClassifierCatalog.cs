using System.Text.Json;
using PiSharp.Runtime.Classifiers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

internal static class ClassifierCatalog
{
    internal static bool IsBuiltinProvider(string id) => id is "typesafe" or "vercel-ai-gateway" or "opencode" or "openrouter" or "cloudflare-workers-ai";

    public static void AddBuiltins(Dictionary<string, ProviderProfile> providers, Func<string, string?> environment)
    {
        using var stream = typeof(ClassifierCatalog).Assembly.GetManifestResourceStream("PiSharp.Cli.Providers.Data.classifier-models.json")!;
        using var document = JsonDocument.Parse(stream);
        foreach (var group in document.RootElement.GetProperty("models").EnumerateArray().GroupBy(item => item.GetProperty("provider").GetString()!))
        {
            var models = group.Select(item => Parse(group.Key, item,
                environment("CLOUDFLARE_ACCOUNT_ID"))).ToArray();
            var id = group.Key;
            if (providers.TryGetValue(id, out var existing)) providers[id] = existing with { Classifiers = models };
            else
            {
                var keyEnvironment = id switch
                {
                    "typesafe" => "TYPESAFE_API_KEY",
                    "vercel-ai-gateway" => "AI_GATEWAY_API_KEY",
                    "opencode" => "OPENCODE_API_KEY",
                    "cloudflare-workers-ai" => "CLOUDFLARE_API_KEY",
                    _ => throw new InvalidDataException($"Unknown classifier provider {id}.")
                };
                providers[id] = new(id, id, models[0].BaseUrl, true, false, keyEnvironment, null, [], Classifiers: models);
            }
        }
    }

    public static ClassifierModel Parse(string provider, JsonElement item, string? cloudflareAccount = null, Uri? defaultEndpoint = null)
    {
        var url = item.TryGetProperty("baseUrl", out var endpoint) ? endpoint.GetString()! : defaultEndpoint?.AbsoluteUri ??
            throw new InvalidDataException("Classifier model requires a baseUrl.");
        if (cloudflareAccount is not null) url = url.Replace("{CLOUDFLARE_ACCOUNT_ID}", Uri.EscapeDataString(cloudflareAccount), StringComparison.Ordinal);
        var pricing = item.TryGetProperty("cost", out var cost) ? new ModelPricing(
            cost.GetProperty("input").GetDecimal(), cost.GetProperty("output").GetDecimal()) : null;
        return new(provider, item.GetProperty("id").GetString()!, item.GetProperty("api").GetString()!,
            ProviderProfileLoader.ParseEndpoint(url, "Classifier baseUrl"),
            item.TryGetProperty("contextWindow", out var window) ? window.GetInt32() : 0, pricing);
    }
}
