using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

internal static class RadiusProviderOptions
{
    public const string DefaultGateway = "https://radius.pi.dev";

    public static Uri FromEnvironment(Func<string, string?> environment)
    {
        var configured = environment("PISHARP_RADIUS_GATEWAY")?.Trim();
        if (string.IsNullOrWhiteSpace(configured)) return new Uri(DefaultGateway);
        if (!configured.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !configured.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            configured = "https://" + configured;
        return ProviderProfileLoader.ParseEndpoint(configured.TrimEnd('/'), "PISHARP_RADIUS_GATEWAY");
    }
}

/// <summary>Loads the pinned public Radius catalogue and the authenticated gateway catalogue.</summary>
internal static class RadiusModelCatalog
{
    public const string SourcePiRevision = "fd889a2741891ee45116cb6131052d7fad220886";
    private const string ResourceName = "PiSharp.Cli.Providers.Data.radius-models.json";
    private const int MaxCatalogBytes = 1024 * 1024;
    private static readonly Lazy<IReadOnlyList<ModelDescriptor>> BuiltinModels = new(LoadEmbedded);

    public static IReadOnlyList<ModelDescriptor> LoadBuiltin(Uri gateway) =>
        IsDefaultGateway(gateway) ? BuiltinModels.Value : [];

    public static async Task<IReadOnlyList<ModelDescriptor>> LoadAsync(HttpClient http, string providerId,
        Uri gateway, string? apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(gateway, "/v1/config"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Radius gateway catalogue request failed ({(int)response.StatusCode}).",
                null, response.StatusCode);

        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var baseUrlText = GetString(root, "baseUrl");
        if (root.ValueKind != JsonValueKind.Object || baseUrlText is null ||
            !root.TryGetProperty("models", out var modelsValue) || modelsValue.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Radius gateway catalogue has an invalid shape.");
        var baseUrl = ProviderProfileLoader.ParseEndpoint(baseUrlText, "Radius gateway model baseUrl")
            .ToString().TrimEnd('/');
        var models = new List<ModelDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in modelsValue.EnumerateArray())
        {
            var model = ParseModel(value, providerId, "Radius gateway", baseUrl, requireProviderFields: true);
            if (model is not null && seen.Add(model.Id)) models.Add(model);
        }
        return models;
    }

    public static IReadOnlyList<ModelDescriptor> Merge(IReadOnlyList<ModelDescriptor> baseline,
        IReadOnlyList<ModelDescriptor> refreshed)
    {
        var models = baseline.ToList();
        foreach (var model in refreshed)
        {
            var existing = models.FindIndex(candidate => candidate.Id.Equals(model.Id, StringComparison.Ordinal));
            if (existing >= 0) models[existing] = model;
            else models.Add(model);
        }
        return models;
    }

    private static IReadOnlyList<ModelDescriptor> LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The pinned Radius model catalogue resource is missing.");
        using var document = JsonDocument.Parse(stream);
        if (!string.Equals(GetString(document.RootElement, "sourcePiRevision"), SourcePiRevision,
                StringComparison.Ordinal) ||
            !document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The pinned Radius model catalogue is invalid.");
        var result = new List<ModelDescriptor>();
        foreach (var value in models.EnumerateArray())
        {
            var model = ParseModel(value, "radius", "pinned Radius catalog", null, requireProviderFields: false);
            if (model is not null) result.Add(model);
        }
        return result;
    }

    private static ModelDescriptor? ParseModel(JsonElement value, string providerId, string source,
        string? baseUrl, bool requireProviderFields)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var id = GetString(value, "id");
        var name = GetString(value, "name");
        var reasoning = GetBoolean(value, "reasoning");
        var contextWindow = GetPositiveInt(value, "contextWindow");
        var maxTokens = GetPositiveInt(value, "maxTokens");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || reasoning is null ||
            contextWindow is null || maxTokens is null ||
            !value.TryGetProperty("input", out var inputValue) || inputValue.ValueKind != JsonValueKind.Array ||
            !value.TryGetProperty("cost", out var costValue) || costValue.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetDecimal(costValue, "input", out var inputCost) ||
            !TryGetDecimal(costValue, "output", out var outputCost)) return null;
        var input = inputValue.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => item is "text" or "image")
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (input.Length == 0) return null;
        if (requireProviderFields && baseUrl is null) return null;

        var cachedRead = TryGetDecimal(costValue, "cacheRead", out var read) ? read : (decimal?)null;
        var cachedWrite = TryGetDecimal(costValue, "cacheWrite", out var write) ? write : (decimal?)null;
        var thinkingMap = value.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind == JsonValueKind.Object
            ? map.Clone() : (JsonElement?)null;
        var limits = ModelInputLimitsParser.Parse(value, $"{source} model '{id}'", strict: false);
        var modelBaseUrl = baseUrl ?? GetString(value, "baseUrl");
        var modelStatus = GetBoolean(value, "enabled") == false ? "disabled" : source;
        return new ModelDescriptor(id, GetString(value, "lab"), contextWindow, modelStatus, reasoning,
            new ModelPricing(inputCost, outputCost, cachedRead, CachedWrite: cachedWrite), providerId,
            Available: GetBoolean(value, "enabled") != false, Name: name, MaxOutputTokens: maxTokens,
            Input: input, Api: "pi-messages", InputLimits: limits, BaseUrl: modelBaseUrl,
            ThinkingLevelMap: thinkingMap);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxCatalogBytes)
            throw new InvalidDataException("Radius gateway catalogue exceeds 1MB.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxCatalogBytes)
                throw new InvalidDataException("Radius gateway catalogue exceeds 1MB.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool IsDefaultGateway(Uri gateway) =>
        string.Equals(gateway.GetLeftPart(UriPartial.Path).TrimEnd('/'), RadiusProviderOptions.DefaultGateway,
            StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) &&
        child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    private static bool? GetBoolean(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) &&
        child.ValueKind is JsonValueKind.True or JsonValueKind.False ? child.GetBoolean() : null;

    private static int? GetPositiveInt(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) &&
        child.ValueKind == JsonValueKind.Number && child.TryGetInt32(out var number) && number > 0 ? number : null;

    private static bool TryGetDecimal(JsonElement value, string property, out decimal result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var child) ||
            child.ValueKind != JsonValueKind.Number || !child.TryGetDecimal(out result)) return false;
        return result >= 0;
    }
}
