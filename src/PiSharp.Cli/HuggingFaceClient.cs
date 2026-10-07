using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiSharp.Cli;

internal sealed record HuggingFaceModel(string Id, long Downloads);
internal sealed record HuggingFaceQuantization(string Name, long? Size);
internal sealed record HuggingFaceModelDetails(string Id, string? Gated,
    IReadOnlyList<HuggingFaceQuantization> Quantizations);

internal sealed class HuggingFaceClient(HttpClient http, string? token, Uri? baseUrl = null)
{
    private const int MaxResponseBytes = 5 * 1024 * 1024;
    private const string DefaultBaseUrl = "https://huggingface.co";
    private static readonly Regex s_quantization = new(
        "(?:^|[-_.])((?:UD-)?(?:IQ\\d(?:_[A-Z0-9]+)+|Q\\d(?:_[A-Z0-9]+)+|BF16|F16|F32|MXFP\\d(?:_[A-Z0-9]+)*))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex s_shardSuffix = new("-\\d{5}-of-\\d{5}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly Uri _baseUrl = new((baseUrl?.ToString() ?? DefaultBaseUrl).TrimEnd('/') + "/");

    internal static async Task<string?> FindTokenAsync(Func<string, string?> environment,
        CancellationToken cancellationToken = default)
    {
        var fromEnvironment = environment("HF_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment)) return fromEnvironment;
        var paths = new[]
        {
            environment("HF_TOKEN_PATH"),
            string.IsNullOrWhiteSpace(environment("HF_HOME")) ? null : Path.Combine(environment("HF_HOME")!, "token"),
            string.IsNullOrWhiteSpace(environment("XDG_CACHE_HOME")) ? null :
                Path.Combine(environment("XDG_CACHE_HOME")!, "huggingface", "token"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "token")
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path!);
                if (info.Length > 64 * 1024) continue;
                var value = (await File.ReadAllTextAsync(path!, cancellationToken).ConfigureAwait(false)).Trim();
                if (!string.IsNullOrEmpty(value)) return value;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                                              NotSupportedException or System.Security.SecurityException)
            { }
        }
        return null;
    }

    public async Task<IReadOnlyList<HuggingFaceModel>> SearchAsync(string query,
        CancellationToken cancellationToken)
    {
        using var values = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["search"] = query,
            ["filter"] = "gguf",
            ["sort"] = "downloads",
            ["direction"] = "-1",
            ["limit"] = "20"
        });
        var encodedQuery = await values.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = await GetAsync("api/models?" + encodedQuery, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Hugging Face returned invalid search results.");
        return document.RootElement.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(value => new HuggingFaceModel(value.GetProperty("id").GetString()!,
                value.TryGetProperty("downloads", out var downloads) && downloads.TryGetInt64(out var count) ? count : 0))
            .ToArray();
    }

    public async Task<HuggingFaceModelDetails> GetDetailsAsync(string repository,
        CancellationToken cancellationToken)
    {
        var encoded = string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
        using var document = await GetAsync($"api/models/{encoded}?blobs=true", cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Hugging Face returned invalid model details.");
        var id = document.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()! : repository;
        var gated = document.RootElement.TryGetProperty("gated", out var gatedElement) &&
                    gatedElement.ValueKind == JsonValueKind.String && gatedElement.GetString() is "auto" or "manual"
            ? gatedElement.GetString() : null;
        var sizes = new Dictionary<string, (long Total, bool Complete)>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("siblings", out var siblings) && siblings.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in siblings.EnumerateArray())
            {
                if (!file.TryGetProperty("rfilename", out var filenameElement) || filenameElement.ValueKind != JsonValueKind.String)
                    continue;
                var filename = filenameElement.GetString()!;
                if (!filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                var name = filename.Split('/').Last();
                if (name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
                var stem = s_shardSuffix.Replace(name[..^5], "");
                var match = s_quantization.Match(stem);
                if (!match.Success) continue;
                var quantization = match.Groups[1].Value.ToUpperInvariant();
                var current = sizes.TryGetValue(quantization, out var existing)
                    ? existing : (Total: 0L, Complete: true);
                if (file.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes))
                    current.Total += bytes;
                else current.Complete = false;
                sizes[quantization] = current;
            }
        }
        var quantizations = sizes.Select(entry => new HuggingFaceQuantization(entry.Key,
                entry.Value.Complete ? entry.Value.Total : null))
            .OrderByDescending(entry => entry.Name == "Q4_K_M")
            .ThenBy(entry => entry.Size ?? long.MaxValue)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        return new(id, gated, quantizations);
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUrl, path));
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
        JsonDocument? document = null;
        try { if (bytes.Length > 0) document = JsonDocument.Parse(bytes); }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode)
        {
            var message = document is not null ? ErrorMessage(document.RootElement) : null;
            document?.Dispose();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = ParseRateLimitDelay(response.Headers.RetryAfter?.Delta?.TotalSeconds,
                    response.Headers.TryGetValues("ratelimit", out var limits) ? limits.FirstOrDefault() : null);
                throw new HttpRequestException(delay is > 0
                    ? $"Hugging Face rate limit reached; retry in {delay.Value:0}s"
                    : "Hugging Face rate limit reached", null, response.StatusCode);
            }
            throw new HttpRequestException(message ?? $"Hugging Face returned HTTP {(int)response.StatusCode}.", null,
                response.StatusCode);
        }
        return document ?? throw new InvalidDataException("Hugging Face returned invalid JSON.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("Hugging Face response exceeds 5MB.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes)
                throw new InvalidDataException("Hugging Face response exceeds 5MB.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string? ErrorMessage(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("error", out var error) &&
        error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString())
            ? error.GetString() : null;

    private static int? ParseRateLimitDelay(double? retryAfter, string? rateLimit)
    {
        if (retryAfter is > 0) return (int)Math.Round(retryAfter.Value, MidpointRounding.AwayFromZero);
        var match = Regex.Match(rateLimit ?? "", "(?:^|;)t=(\\d+)", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var seconds) ? seconds : null;
    }
}
