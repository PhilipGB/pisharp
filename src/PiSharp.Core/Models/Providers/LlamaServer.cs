using System.Text.Json;

namespace PiSharp.Core.Models.Providers;

/// <summary>llama.cpp router model statuses (pinned llama extension: LlamaModelStatus).</summary>
public static class LlamaModelStatusValues
{
    public const string Unloaded = "unloaded";
    public const string Loading = "loading";
    public const string Loaded = "loaded";
    public const string Downloading = "downloading";
    public const string Sleeping = "sleeping";
}

/// <summary>
/// One model entry from the llama.cpp router (pinned llama extension: LlamaModelInfo).
/// </summary>
public sealed record LlamaModelInfo
{
    public required string Id { get; init; }
    public IReadOnlyList<string>? Aliases { get; init; }
    public required LlamaModelStatus Status { get; init; }
    public LlamaArchitecture? Architecture { get; init; }
    public string? Source { get; init; }
    public LlamaModelMeta? Meta { get; init; }
}

/// <summary>Router-reported model lifecycle status.</summary>
public sealed record LlamaModelStatus
{
    public required string Value { get; set; }
    public IReadOnlyList<string>? Args { get; set; }
    public bool Failed { get; set; }
    public int? ExitCode { get; set; }
    public IReadOnlyDictionary<string, LlamaProgressRange>? Progress { get; set; }
}

/// <summary>done/total pair inside a download progress object.</summary>
public sealed record LlamaProgressRange(long Done, long Total);

/// <summary>Model architecture modalities.</summary>
public sealed record LlamaArchitecture
{
    public IReadOnlyList<string>? InputModalities { get; init; }
    public IReadOnlyList<string>? OutputModalities { get; init; }
}

/// <summary>GGUF metadata reported by the router.</summary>
public sealed record LlamaModelMeta
{
    public int? NCtx { get; init; }
    public int? NCtxTrain { get; init; }
    public long? Size { get; init; }
    public string? Ftype { get; init; }
}

/// <summary>Progress snapshot for load/download operations (pinned: LlamaProgress).</summary>
public sealed record LlamaProgress(string Message, double? Ratio = null, string? Detail = null);

/// <summary>
/// HTTP client for the llama.cpp router API (pinned llama extension: LlamaClient).
/// Server URL normalization, request timeout, and error shapes mirror the pinned client.
/// Load/download completion is detected by polling the model list; the pinned SSE event
/// stream is not consumed (documented difference — polling is authoritative there too).
/// </summary>
public sealed class LlamaClient
{
    internal const int RequestTimeoutMs = 15_000;
    private const int UnloadPollMs = 100;
    private const int LoadPollMs = 250;
    private const int DownloadPollMs = 500;

    /// <summary>Normalized server URL (no trailing slash, no /v1 suffix).</summary>
    public string ServerUrl { get; }

    private readonly string? _apiKey;

    public LlamaClient(string serverUrl, string? apiKey = null)
    {
        ServerUrl = LlamaUrls.Normalize(serverUrl);
        _apiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey;
    }

    private async Task<JsonElement> RequestAsync(
        string path,
        HttpMethod? method = null,
        string? body = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeoutMs);
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, $"{ServerUrl}{path}");
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        if (_apiKey is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using var response = await _http.SendAsync(request, timeoutCts.Token);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonElement? json = null;
        if (payload.Length > 0)
        {
            try
            {
                json = JsonDocument.Parse(payload).RootElement.Clone();
            }
            catch (JsonException)
            {
                json = null;
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(LlamaErrorText(json, $"llama.cpp returned HTTP {(int)response.StatusCode}"));
        }

        return json ?? throw new InvalidOperationException("llama.cpp returned an empty response");
    }

    private static readonly System.Net.Http.HttpClient _http = new();

    private static string LlamaErrorText(JsonElement? payload, string fallback)
    {
        if (payload is { } element &&
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String &&
            string.IsNullOrEmpty(message.GetString()) is false)
        {
            return message.GetString()!;
        }

        return fallback;
    }

    /// <summary>Lists models from the router (pinned list). Throws when the payload is not a router catalog.</summary>
    public async Task<IReadOnlyList<LlamaModelInfo>> ListAsync(bool reload = false, CancellationToken cancellationToken = default)
    {
        var element = await RequestAsync($"/models{(reload ? "?reload=1" : string.Empty)}", cancellationToken: cancellationToken);
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("llama.cpp returned an invalid model catalog");
        }

        var models = new List<LlamaModelInfo>();
        foreach (var item in data.EnumerateArray())
        {
            var model = ParseModelInfo(item);
            if (model is null)
            {
                throw new InvalidOperationException("Server is not running in llama.cpp router mode");
            }

            models.Add(model);
        }

        return models;
    }

    /// <summary>Server properties; models_autoload decides preset routability (pinned props).</summary>
    public async Task<bool> GetModelsAutoloadAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync("/props", cancellationToken: cancellationToken);
        return payload.ValueKind == JsonValueKind.Object &&
               payload.TryGetProperty("models_autoload", out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    /// <summary>Asks the router to load a model (pinned load).</summary>
    public Task LoadAsync(string model, CancellationToken cancellationToken = default) =>
        RequestAsync("/models/load", HttpMethod.Post, JsonSerializer.Serialize(new { model }), cancellationToken);

    /// <summary>Asks the router to unload a model (pinned unload).</summary>
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) =>
        RequestAsync("/models/unload", HttpMethod.Post, JsonSerializer.Serialize(new { model }), cancellationToken);

    /// <summary>Starts a model download (pinned download).</summary>
    public Task DownloadAsync(string model, CancellationToken cancellationToken = default) =>
        RequestAsync("/models", HttpMethod.Post, JsonSerializer.Serialize(new { model }), cancellationToken);

    /// <summary>Unloads and polls until the model is gone or unloaded (pinned unloadAndWait).</summary>
    public async Task UnloadAndWaitAsync(string model, CancellationToken cancellationToken = default)
    {
        await UnloadAsync(model, cancellationToken);
        while (true)
        {
            var models = await ListAsync(cancellationToken: cancellationToken);
            var entry = models.FirstOrDefault(candidate => candidate.Id == model);
            if (entry is null || entry.Status.Value == LlamaModelStatusValues.Unloaded)
            {
                return;
            }

            await Task.Delay(UnloadPollMs, cancellationToken);
        }
    }

    /// <summary>
    /// Loads a model and polls until it is loaded or fails (pinned loadAndWait, polling
    /// variant: the pinned SSE event signals are not available, the list poll is
    /// authoritative).
    /// </summary>
    public async Task<LlamaModelInfo> LoadAndWaitAsync(
        string model,
        Action<LlamaProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        await LoadAsync(model, cancellationToken);
        onProgress?.Invoke(new LlamaProgress("Loading model"));
        while (true)
        {
            var entry = (await ListAsync(cancellationToken: cancellationToken)).FirstOrDefault(candidate => candidate.Id == model);
            if (entry is not null && entry.Status.Value == LlamaModelStatusValues.Loaded)
            {
                return entry;
            }

            if (entry is { Status.Failed: true })
            {
                throw new InvalidOperationException(
                    entry.Status.ExitCode is { } exitCode
                        ? $"Model exited with code {exitCode}"
                        : "Model failed to load");
            }

            if (entry is { Status.Value: not (LlamaModelStatusValues.Loading or LlamaModelStatusValues.Unloaded) } other)
            {
                onProgress?.Invoke(new LlamaProgress($"Loading {model} ({other.Status.Value})"));
            }

            await Task.Delay(LoadPollMs, cancellationToken);
        }
    }

    /// <summary>
    /// Downloads a model and polls until the download settles (pinned downloadAndWait,
    /// polling variant). Returns the refreshed catalog after completion.
    /// </summary>
    public async Task<IReadOnlyList<LlamaModelInfo>> DownloadAndWaitAsync(
        string model,
        Action<LlamaProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        await DownloadAsync(model, cancellationToken);
        onProgress?.Invoke(new LlamaProgress("Downloading model"));
        var sawDownloading = false;
        var polls = 0;
        while (true)
        {
            var models = await ListAsync(cancellationToken: cancellationToken);
            polls++;
            var entry = models.FirstOrDefault(candidate => candidate.Id == model);
            if (entry is { Status.Value: LlamaModelStatusValues.Downloading })
            {
                sawDownloading = true;
                var progress = ParseDownloadProgress(entry.Status.Progress);
                if (progress is not null)
                {
                    onProgress?.Invoke(progress);
                }
            }
            else if (entry is not null && (sawDownloading || polls >= 2))
            {
                return await ListAsync(reload: true, cancellationToken: cancellationToken);
            }

            await Task.Delay(DownloadPollMs, cancellationToken);
        }
    }

    /// <summary>
    /// Parses the router's per-file download progress into one aggregate (pinned
    /// parseDownloadProgress): sums done/total across files, reports bytes via FormatBytes.
    /// </summary>
    internal static LlamaProgress? ParseDownloadProgress(IReadOnlyDictionary<string, LlamaProgressRange>? progress)
    {
        if (progress is null)
        {
            return null;
        }

        long done = 0;
        long total = 0;
        foreach (var range in progress.Values)
        {
            done += range.Done;
            total += range.Total;
        }

        if (total <= 0)
        {
            return null;
        }

        return new LlamaProgress("Downloading model", (double)done / total, $"{FormatBytes(done)} / {FormatBytes(total)}");
    }

    /// <summary>Binary size formatting (pinned formatBytes: KiB/MiB/GiB/TiB, 10+ one decimal).</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KiB", "MiB", "GiB", "TiB"];
        var value = bytes / 1024.0;
        var unit = units[0];
        for (var index = 1; index < units.Length && value >= 1024; index++)
        {
            value /= 1024;
            unit = units[index];
        }

        return $"{(value >= 10 ? value.ToString("F1") : value.ToString("F2"))} {unit}";
    }

    internal static LlamaModelInfo? ParseModelInfo(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("status", out var statusElement) ||
            statusElement.ValueKind != JsonValueKind.Object ||
            !statusElement.TryGetProperty("value", out var statusValue) ||
            statusValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var status = new LlamaModelStatus { Value = statusValue.GetString()! };
        if (statusElement.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            status.Args = args.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray();
        }

        if (statusElement.TryGetProperty("failed", out var failed) && failed.ValueKind == JsonValueKind.True)
        {
            status.Failed = true;
        }

        if (statusElement.TryGetProperty("exit_code", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number)
        {
            status.ExitCode = exitCode.GetInt32();
        }

        if (statusElement.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Object)
        {
            var ranges = new Dictionary<string, LlamaProgressRange>(StringComparer.Ordinal);
            foreach (var property in progress.EnumerateObject())
            {
                var value = property.Value;
                if (value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.Number &&
                    value.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number)
                {
                    ranges[property.Name] = new LlamaProgressRange(done.GetInt64(), total.GetInt64());
                }
            }

            status.Progress = ranges.Count > 0 ? ranges : null;
        }

        LlamaArchitecture? architecture = null;
        if (element.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object)
        {
            architecture = new LlamaArchitecture
            {
                InputModalities = StringArray(arch, "input_modalities"),
                OutputModalities = StringArray(arch, "output_modalities"),
            };
        }

        LlamaModelMeta? meta = null;
        if (element.TryGetProperty("meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object)
        {
            meta = new LlamaModelMeta
            {
                NCtx = IntProperty(metaElement, "n_ctx"),
                NCtxTrain = IntProperty(metaElement, "n_ctx_train"),
                Size = LongProperty(metaElement, "size"),
                Ftype = StringProperty(metaElement, "ftype"),
            };
        }

        return new LlamaModelInfo
        {
            Id = id.GetString()!,
            Aliases = StringArray(element, "aliases"),
            Status = status,
            Architecture = architecture,
            Source = StringProperty(element, "source"),
            Meta = meta,
        };
    }

    private static IReadOnlyList<string>? StringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
        return values.Length > 0 ? values : null;
    }

    private static string? StringProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? IntProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetInt32()
            : null;

    private static long? LongProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetInt64()
            : null;
}

/// <summary>
/// llama.cpp server URL helpers (pinned normalizeLlamaServerUrl / llamaInferenceUrl).
/// The router serves the OpenAI-compatible surface under /v1; server URLs are stored
/// without the /v1 suffix.
/// </summary>
public static class LlamaUrls
{
    /// <summary>Default llama.cpp router URL (pinned DEFAULT_LLAMA_SERVER_URL).</summary>
    public const string DefaultServerUrl = "http://127.0.0.1:8080";

    /// <summary>Env var holding a configured server URL (pinned LLAMA_BASE_URL).</summary>
    public const string BaseUrlEnvironmentVariable = "LLAMA_BASE_URL";

    /// <summary>Env var holding an optional server API key (pinned LLAMA_API_KEY).</summary>
    public const string ApiKeyEnvironmentVariable = "LLAMA_API_KEY";

    /// <summary>
    /// Normalizes a server URL: http(s) only, no hash/query, trailing slashes and a
    /// trailing /v1 segment removed (pinned normalizeLlamaServerUrl).
    /// </summary>
    public static string Normalize(string value)
    {
        Uri url;
        try
        {
            url = new Uri(value.Trim(), UriKind.Absolute);
        }
        catch (UriFormatException exception)
        {
            throw new InvalidOperationException($"Invalid llama.cpp server URL: {value}", exception);
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Server URL must use http or https");
        }

        var path = url.AbsolutePath;
        while (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
        {
            path = path[..^1];
        }

        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
            while (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            {
                path = path[..^1];
            }
        }

        var builder = new UriBuilder(url)
        {
            Path = path.Length == 0 ? "/" : path,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var normalized = builder.Uri.ToString();
        return normalized.TrimEnd('/');
    }

    /// <summary>The OpenAI-compatible inference base URL for a server URL (pinned llamaInferenceUrl).</summary>
    public static string InferenceUrl(string serverUrl) => $"{Normalize(serverUrl)}/v1";
}
