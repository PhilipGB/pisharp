using System.Net.Http.Headers;
using System.Text.Json;

namespace PiSharp.Runtime.Providers;

public sealed record ModelDescriptor(string Id, string? Owner, int? ContextLength, string? Status);

/// <summary>Discover OpenAI-compatible model IDs without coupling model metadata to a specific SDK.</summary>
public static class ModelCatalog
{
    public static async Task<IReadOnlyList<ModelDescriptor>> ListAsync(HttpClient http, Uri? endpoint, string apiKey,
        CancellationToken cancellationToken = default)
    {
        var address = new Uri((endpoint?.ToString() ?? "https://api.openai.com/v1").TrimEnd('/') + "/models");
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        const int maxBytes = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("Model catalogue exceeds 1MB.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[maxBytes + 1];
        var length = 0;
        int read;
        while (length < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken)) > 0)
            length += read;
        if (length > maxBytes) throw new InvalidDataException("Model catalogue exceeds 1MB.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Model catalogue has no data array.");
        var models = new List<ModelDescriptor>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var name) ||
                name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) continue;
            var id = name.GetString()!;
            if (models.Any(model => model.Id == id)) continue;
            static string? StringProperty(JsonElement element, string property) =>
                element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var context = item.TryGetProperty("context_length", out var size) && size.ValueKind == JsonValueKind.Number &&
                size.TryGetInt32(out var parsed) && parsed > 0 ? parsed : (int?)null;
            var status = item.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.Object ?
                StringProperty(state, "value") : null;
            models.Add(new(id, StringProperty(item, "owned_by"), context, status));
        }
        return models;
    }
}
