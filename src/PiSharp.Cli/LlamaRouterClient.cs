using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Cli;

internal sealed record LlamaRouterModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] LlamaRouterModelStatus Status,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string>? Aliases = null,
    [property: JsonPropertyName("architecture")] LlamaRouterArchitecture? Architecture = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("meta")] LlamaRouterModelMetadata? Metadata = null);

internal sealed record LlamaRouterModelStatus(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("args")] IReadOnlyList<string>? Args = null,
    [property: JsonPropertyName("failed")] bool? Failed = null,
    [property: JsonPropertyName("exit_code")] int? ExitCode = null,
    [property: JsonPropertyName("progress")] JsonElement? Progress = null);

internal sealed record LlamaRouterArchitecture(
    [property: JsonPropertyName("input_modalities")] IReadOnlyList<string>? InputModalities = null,
    [property: JsonPropertyName("output_modalities")] IReadOnlyList<string>? OutputModalities = null);

internal sealed record LlamaRouterModelMetadata(
    [property: JsonPropertyName("n_ctx")] int? ContextWindow = null,
    [property: JsonPropertyName("n_ctx_train")] int? TrainingContextWindow = null,
    [property: JsonPropertyName("size")] long? Size = null,
    [property: JsonPropertyName("ftype")] string? FileType = null);

internal sealed record LlamaRouterServerProps(
    [property: JsonPropertyName("models_autoload")] bool? ModelsAutoload = null,
    [property: JsonPropertyName("chat_template")] string? ChatTemplate = null);

internal sealed class LlamaRouterClient(HttpClient http, Uri serverUrl, string? apiKey)
{
    internal const string DefaultServerUrl = "http://127.0.0.1:8080";
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly Uri _serverUrl = NormalizeServerUrl(serverUrl.ToString());

    public Uri ServerUrl => _serverUrl;

    public static Uri NormalizeServerUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(url.UserInfo))
            throw new ArgumentException("Server URL must be an absolute HTTP(S) URL without embedded credentials.", nameof(value));

        var builder = new UriBuilder(url) { Query = "", Fragment = "" };
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.Ordinal)) path = path[..^3].TrimEnd('/');
        builder.Path = path;
        return new Uri(builder.Uri.AbsoluteUri.TrimEnd('/'), UriKind.Absolute);
    }

    public async Task<IReadOnlyList<LlamaRouterModelInfo>> ListAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("models", cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("llama.cpp returned an invalid model catalog.");

        var models = new List<LlamaRouterModelInfo>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
                !item.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object ||
                !status.TryGetProperty("value", out var statusValue) || statusValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("llama.cpp returned an invalid model catalog.");
            var model = item.Deserialize<LlamaRouterModelInfo>(s_json);
            if (model is null) throw new InvalidDataException("llama.cpp returned an invalid model catalog.");
            models.Add(model);
        }
        return models;
    }

    public async Task<LlamaRouterServerProps> GetPropsAsync(string? model, CancellationToken cancellationToken)
    {
        var path = "props";
        if (!string.IsNullOrEmpty(model))
        {
            using var values = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["model"] = model,
                ["autoload"] = "false"
            });
            path += "?" + await values.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        using var document = await GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return new();
        return document.RootElement.Deserialize<LlamaRouterServerProps>(s_json) ?? new();
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(path));
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
        JsonDocument? document = null;
        try { document = JsonDocument.Parse(bytes); }
        catch (JsonException) when (!response.IsSuccessStatusCode) { }
        if (!response.IsSuccessStatusCode)
        {
            var message = document is not null ? ErrorMessage(document.RootElement) : null;
            document?.Dispose();
            throw new HttpRequestException(message ?? $"llama.cpp returned HTTP {(int)response.StatusCode}.", null,
                response.StatusCode);
        }
        return document ?? throw new InvalidDataException("llama.cpp returned invalid JSON.");
    }

    private Uri Endpoint(string path) => new(_serverUrl.AbsoluteUri.TrimEnd('/') + "/" + path, UriKind.Absolute);

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("llama.cpp response exceeds 1MB.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes)
                throw new InvalidDataException("llama.cpp response exceeds 1MB.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string? ErrorMessage(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("error", out var error) &&
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) &&
        message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString())
            ? message.GetString() : null;
}
