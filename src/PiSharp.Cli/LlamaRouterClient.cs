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

internal sealed record LlamaRouterEvent(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("data")] JsonElement Data);

internal sealed record LlamaRouterProgress(string Message, double? Ratio = null, string? Detail = null);

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

    public async Task<IReadOnlyList<LlamaRouterModelInfo>> ListAsync(CancellationToken cancellationToken,
        bool reload = false)
    {
        using var document = await GetAsync(reload ? "models?reload=1" : "models", cancellationToken).ConfigureAwait(false);
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

    public Task LoadAsync(string model, CancellationToken cancellationToken) =>
        PostAsync("models/load", model, cancellationToken);

    public Task UnloadAsync(string model, CancellationToken cancellationToken) =>
        PostAsync("models/unload", model, cancellationToken);

    public Task DownloadAsync(string model, CancellationToken cancellationToken) =>
        PostAsync("models", model, cancellationToken);

    public async Task UnloadAndWaitAsync(string model, CancellationToken cancellationToken)
    {
        await UnloadAsync(model, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var entry = (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == model);
            if (entry is null || entry.Status.Value == "unloaded") return;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<LlamaRouterModelInfo> LoadAndWaitAsync(string model,
        Action<LlamaRouterProgress> onProgress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onProgress);
        using var watcherCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var eventGate = new object();
        var eventLoaded = false;
        string? eventError = null;
        var watchTask = ObserveEventsAsync(watcherCancellation.Token, model, item =>
        {
            if (item.Event is not ("model_status" or "status_change")) return;
            lock (eventGate)
            {
                if (item.Data.ValueKind == JsonValueKind.Object && item.Data.TryGetProperty("status", out var status) &&
                    status.ValueKind == JsonValueKind.String)
                {
                    if (status.GetString() == "loaded") eventLoaded = true;
                    if (status.GetString() == "unloaded") eventError = "Model failed to load";
                }
            }
            if (ParseLoadProgress(item.Data) is { } progress) onProgress(progress);
        });
        try
        {
            await LoadAsync(model, cancellationToken).ConfigureAwait(false);
            onProgress(new("Loading model"));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = (await ListAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(candidate => candidate.Id == model);
                if (entry?.Status.Value == "loaded") return entry;
                lock (eventGate)
                {
                    if (eventLoaded && entry is null)
                        return new LlamaRouterModelInfo(model, new LlamaRouterModelStatus("loaded"));
                    if (entry?.Status.Failed == true || eventError is not null)
                        throw new InvalidOperationException(entry?.Status.ExitCode is { } exitCode
                            ? $"Model exited with code {exitCode}"
                            : eventError ?? "Model failed to load");
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            watcherCancellation.Cancel();
            await ObserveCancellationAsync(watchTask).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<LlamaRouterModelInfo>> DownloadAndWaitAsync(string model,
        Action<LlamaRouterProgress> onProgress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onProgress);
        using var watcherCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var eventGate = new object();
        var finished = false;
        string? failure = null;
        var sawProgress = false;
        var watchTask = ObserveEventsAsync(watcherCancellation.Token, model, item =>
        {
            lock (eventGate)
            {
                if (item.Event == "download_finished") finished = true;
                if (item.Event == "download_failed") failure = ErrorMessage(item.Data) ?? "Download failed";
                if (item.Event == "download_progress") sawProgress = true;
            }
            if (item.Event == "download_progress" && ParseDownloadProgress(item.Data) is { } progress)
                onProgress(progress);
        });
        try
        {
            await DownloadAsync(model, cancellationToken).ConfigureAwait(false);
            onProgress(new("Downloading model"));
            var polls = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (eventGate)
                    if (failure is not null) throw new InvalidOperationException(failure);
                var models = await ListAsync(cancellationToken).ConfigureAwait(false);
                polls++;
                var entry = models.FirstOrDefault(candidate => candidate.Id == model);
                if (entry?.Status.Value == "downloading")
                {
                    lock (eventGate) sawProgress = true;
                    if (entry.Status.Progress is { } statusProgress && ParseDownloadProgress(statusProgress) is { } progress)
                        onProgress(progress);
                }
                else
                {
                    bool isFinished;
                    bool hasProgress;
                    lock (eventGate) { isFinished = finished; hasProgress = sawProgress; }
                    if (isFinished || entry is not null && (hasProgress || polls >= 2))
                        return await ListAsync(cancellationToken, reload: true).ConfigureAwait(false);
                }
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            watcherCancellation.Cancel();
            await ObserveCancellationAsync(watchTask).ConfigureAwait(false);
        }
    }

    public Task WatchAsync(Action<LlamaRouterEvent> onEvent, CancellationToken cancellationToken) =>
        WatchStreamAsync(onEvent, cancellationToken);

    private async Task WatchStreamAsync(Action<LlamaRouterEvent> onEvent, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("models/sse"));
        if (apiKey is { Length: > 0 })
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content is null)
            throw new HttpRequestException($"llama.cpp SSE returned HTTP {(int)response.StatusCode}.", null,
                response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var data = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                Dispatch();
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal)) data.Add(line[5..].TrimStart());
        }
        Dispatch();

        void Dispatch()
        {
            if (data.Count == 0) return;
            var payload = string.Join("\n", data);
            data.Clear();
            try
            {
                var item = JsonSerializer.Deserialize<LlamaRouterEvent>(payload, s_json);
                if (item is { Model.Length: > 0, Event.Length: > 0 }) onEvent(item);
            }
            catch (JsonException) { }
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var units = new[] { "KiB", "MiB", "GiB", "TiB" };
        var value = bytes / 1024d;
        var unit = units[0];
        for (var index = 1; index < units.Length && value >= 1024; index++)
        {
            value /= 1024;
            unit = units[index];
        }
        return $"{value.ToString(value >= 10 ? "F1" : "F2", System.Globalization.CultureInfo.InvariantCulture)} {unit}";
    }

    private async Task PostAsync(string path, string model, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { model }, s_json),
            System.Text.Encoding.UTF8, "application/json");
        _ = await SendAsync(HttpMethod.Post, path, content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException error) { throw new InvalidDataException("llama.cpp returned invalid JSON.", error); }
    }

    private async Task<byte[]> SendAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(method, Endpoint(path)) { Content = content };
        if (apiKey is { Length: > 0 })
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        var bytes = response.Content is null ? [] :
            await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
        JsonDocument? document = null;
        try { if (bytes.Length > 0) document = JsonDocument.Parse(bytes); }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode)
        {
            var message = document is not null ? ErrorMessage(document.RootElement) : null;
            document?.Dispose();
            throw new HttpRequestException(message ?? $"llama.cpp returned HTTP {(int)response.StatusCode}.", null,
                response.StatusCode);
        }
        document?.Dispose();
        return bytes;
    }

    private async Task ObserveEventsAsync(CancellationToken cancellationToken, string model,
        Action<LlamaRouterEvent> onEvent)
    {
        try
        {
            await WatchStreamAsync(item =>
            {
                if (item.Model == model) onEvent(item);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or
                                          JsonException or OperationCanceledException)
        { }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    internal static LlamaRouterProgress? ParseLoadProgress(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("progress", out var progress) ||
            progress.ValueKind != JsonValueKind.Object) return null;
        var stage = GetString(progress, "current") ?? GetString(progress, "stage");
        var stageRatio = GetNumber(progress, "value");
        if (stageRatio is { } ratio) stageRatio = Math.Clamp(ratio, 0, 1);
        var stages = progress.TryGetProperty("stages", out var stagesValue) && stagesValue.ValueKind == JsonValueKind.Array
            ? stagesValue.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!).ToArray() : [];
        if (stage is not null && stages.Length > 0)
        {
            var index = Array.IndexOf(stages, stage);
            if (index >= 0) stageRatio = (index + (stageRatio ?? 0)) / stages.Length;
        }
        return new(stage is null ? "Loading model" : "Loading " + stage.Replace('_', ' '), stageRatio);
    }

    internal static LlamaRouterProgress? ParseDownloadProgress(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        var files = data.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Object
            ? progress : data;
        long done = 0;
        long total = 0;
        foreach (var property in files.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var fileDone = GetNumber(property.Value, "done");
            var fileTotal = GetNumber(property.Value, "total");
            if (fileDone is null || fileTotal is null) continue;
            done += (long)fileDone.Value;
            total += (long)fileTotal.Value;
        }
        return total <= 0 ? null : new("Downloading model", (double)done / total,
            $"{FormatBytes(done)} / {FormatBytes(total)}");
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double? GetNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : null;

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
