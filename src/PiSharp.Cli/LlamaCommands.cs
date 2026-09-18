using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Cli;

/// <summary>
/// Interactive /llama (pinned extensions/llama, text variant): manages the llama.cpp
/// router — lists models with lifecycle status, loads/unloads models, and downloads
/// GGUF files from Hugging Face. The pinned TUI is replaced by numbered prompts and
/// line-oriented progress (documented difference); server operations and catalog sync
/// follow the pinned flows.
/// </summary>
internal static class LlamaCommands
{
    private const int CatalogSyncTimeoutMs = 15_000;

    private sealed record ModelOption(LlamaModelInfo Model, string Label);

    /// <summary>
    /// /llama. Resolves the configured server (stored credential env URL or LLAMA_BASE_URL
    /// via the provider auth) and runs the manage loop until the user closes it.
    /// </summary>
    public static async Task HandleLlamaAsync(SessionController sessions, CancellationToken cancellationToken)
    {
        var runtime = sessions.ModelRuntime;
        var configured = await runtime.GetAuthAsync(BuiltinProviders.LlamaCppProviderId, cancellationToken: cancellationToken);
        if (configured?.Auth.BaseUrl is not { } baseUrl)
        {
            Console.WriteLine($"Configure llama.cpp with /login {BuiltinProviders.LlamaCppProviderId}");
            return;
        }

        var client = new LlamaClient(baseUrl);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<LlamaModelInfo> catalog;
            try
            {
                catalog = await client.ListAsync(cancellationToken: cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.WriteLine(ConnectionErrorMessage(error));
                return;
            }

            var action = await ShowModelsAsync(client.ServerUrl, catalog, cancellationToken);
            if (action is CloseAction)
            {
                return;
            }

            if (action is ReListAction)
            {
                continue;
            }

            try
            {
                if (action is DownloadAction)
                {
                    await DownloadModelAsync(runtime, client, cancellationToken);
                }
                else if (action is ModelAction { Model: var model })
                {
                    if (IsModelLoaded(model))
                    {
                        await UnloadModelAsync(runtime, client, model, cancellationToken);
                    }
                    else if (model.Status.Value == LlamaModelStatusValues.Unloaded)
                    {
                        await LoadModelAsync(runtime, client, catalog, model, cancellationToken);
                    }
                    else
                    {
                        Console.WriteLine($"{model.Id} is {model.Status.Value}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Cancelled.");
                return;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
            }
        }
    }

    private abstract record ManagerAction;
    private sealed record CloseAction : ManagerAction;
    private sealed record ReListAction : ManagerAction;
    private sealed record DownloadAction : ManagerAction;
    private sealed record ModelAction(LlamaModelInfo Model) : ManagerAction;

    /// <summary>Numbered model list plus download/close (pinned showModels, text variant).</summary>
    private static async Task<ManagerAction> ShowModelsAsync(
        string serverUrl,
        IReadOnlyList<LlamaModelInfo> catalog,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"llama.cpp server: {serverUrl}");
        for (var i = 0; i < catalog.Count; i++)
        {
            var model = catalog[i];
            var description = ModelDescription(model);
            Console.WriteLine($"  {i + 1,2}. {model.Id}{(description.Length > 0 ? $"  [{description}]" : string.Empty)}");
        }

        var optionsCount = catalog.Count + 2;
        Console.WriteLine($"  {catalog.Count + 1,2}. Download model from Hugging Face");
        Console.WriteLine($"  {catalog.Count + 2,2}. Close");
        var input = await PromptAsync($"Select an action [1-{optionsCount} or d]: ", cancellationToken);
        if (string.IsNullOrWhiteSpace(input) ||
            string.Equals(input, "c", StringComparison.OrdinalIgnoreCase))
        {
            return new CloseAction();
        }

        if (string.Equals(input, "d", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, (catalog.Count + 1).ToString(), StringComparison.Ordinal))
        {
            return new DownloadAction();
        }

        if (string.Equals(input, (catalog.Count + 2).ToString(), StringComparison.Ordinal))
        {
            return new CloseAction();
        }

        if (!int.TryParse(input, out var selected) || selected < 1 || selected > catalog.Count)
        {
            Console.WriteLine("Invalid selection.");
            return new ReListAction();
        }

        return new ModelAction(catalog[selected - 1]);
    }

    private static bool IsModelLoaded(LlamaModelInfo model) =>
        model.Status.Value is LlamaModelStatusValues.Loaded or LlamaModelStatusValues.Sleeping;

    /// <summary>Pinned unloadModel: confirm, unload until settled, sync, report.</summary>
    private static async Task UnloadModelAsync(
        ModelRuntime runtime,
        LlamaClient client,
        LlamaModelInfo model,
        CancellationToken cancellationToken)
    {
        if (!await ConfirmAsync("Unload model?", model.Id, cancellationToken))
        {
            return;
        }

        await client.UnloadAndWaitAsync(model.Id, cancellationToken);
        await SyncCatalogAsync(runtime, client, cancellationToken);
        Console.WriteLine($"Unloaded {model.Id}");
    }

    /// <summary>
    /// Pinned loadModel: when other models are loaded, choose replace vs keep; load with
    /// progress; restore the replaced models on cancel or failure.
    /// </summary>
    private static async Task LoadModelAsync(
        ModelRuntime runtime,
        LlamaClient client,
        IReadOnlyList<LlamaModelInfo> catalog,
        LlamaModelInfo target,
        CancellationToken cancellationToken)
    {
        var loaded = catalog.Where(model => model.Id != target.Id && IsModelLoaded(model)).ToArray();
        var replace = false;
        if (loaded.Length > 0)
        {
            var choice = await SelectAsync(
                $"{loaded.Length} model{(loaded.Length == 1 ? " is" : "s are")} loaded",
                ["Unload all and load", "Keep loaded and load", "Cancel"],
                cancellationToken);
            if (choice is null || choice == "Cancel")
            {
                return;
            }

            replace = choice == "Unload all and load";
        }

        if (replace)
        {
            foreach (var model in loaded)
            {
                await client.UnloadAndWaitAsync(model.Id, cancellationToken);
            }
        }

        try
        {
            var lastProgress = string.Empty;
            await client.LoadAndWaitAsync(
                target.Id,
                progress =>
                {
                    if (!string.Equals(progress.Message, lastProgress, StringComparison.Ordinal))
                    {
                        lastProgress = progress.Message;
                        Console.WriteLine(progress.Message);
                    }
                },
                cancellationToken);
            await SyncCatalogAsync(runtime, client, cancellationToken);
            var refreshed = await client.ListAsync(cancellationToken: cancellationToken);
            var loadedModel = refreshed.FirstOrDefault(model => model.Id == target.Id);
            Console.WriteLine(
                loadedModel is { Status.Value: LlamaModelStatusValues.Loaded }
                    ? $"Loaded {target.Id}"
                    : $"Load started for {target.Id}");
        }
        catch
        {
            if (replace)
            {
                try
                {
                    await RestoreLoadedAsync(client, loaded, cancellationToken);
                }
                catch
                {
                    // Preserve the original load error (pinned behavior).
                }
            }

            throw;
        }
    }

    private static async Task RestoreLoadedAsync(
        LlamaClient client,
        IReadOnlyList<LlamaModelInfo> loaded,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("Restoring previously loaded models");
        foreach (var model in loaded)
        {
            await client.LoadAndWaitAsync(model.Id, cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Pinned downloadModel: HF search, gated-model notice, quantization selection,
    /// download with progress, catalog sync.
    /// </summary>
    private static async Task DownloadModelAsync(
        ModelRuntime runtime,
        LlamaClient client,
        CancellationToken cancellationToken)
    {
        var huggingFace = new HuggingFaceClient(await HuggingFaceClient.FindHuggingFaceToken());
        var query = await PromptAsync("Search Hugging Face (GGUF): ", cancellationToken);
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var results = await huggingFace.SearchAsync(query, cancellationToken);
        if (results.Count == 0)
        {
            Console.WriteLine("No models found.");
            return;
        }

        for (var i = 0; i < results.Count; i++)
        {
            Console.WriteLine($"  {i + 1,2}. {results[i].Id}  ({results[i].Downloads} downloads)");
        }

        var selection = await PromptAsync("Select a model (blank to cancel): ", cancellationToken);
        if (!int.TryParse(selection, out var index) || index < 1 || index > results.Count)
        {
            return;
        }

        var parsed = ParseHuggingFaceModel(results[index - 1].Id);
        Console.WriteLine($"Loading model details ({parsed.Repository})");
        var details = await huggingFace.DetailsAsync(parsed.Repository, cancellationToken);
        if (details.Gated is not null)
        {
            var approval = details.Gated == "manual" ? "Manual approval is required" : "Accept the access terms";
            var choice = await SelectAsync(
                $"Hugging Face access required\n{details.Id}\n\n{approval} at:\nhttps://huggingface.co/{details.Id}\n\nThe llama.cpp server needs HF_TOKEN with access.",
                ["Continue", "Back"],
                cancellationToken);
            if (choice != "Continue")
            {
                return;
            }
        }

        var quantization = parsed.Quantization;
        if (string.IsNullOrEmpty(quantization) && details.Quantizations.Count > 0)
        {
            var labels = details.Quantizations
                .Select(entry => QuantizationLabel(entry))
                .ToArray();
            var choice = await SelectAsync($"Select quantization\n{details.Id}", labels, cancellationToken);
            if (choice is null)
            {
                return;
            }

            quantization = details.Quantizations[Array.IndexOf(labels, choice)].Name;
        }

        var modelId = string.IsNullOrEmpty(quantization) ? details.Id : $"{details.Id}:{quantization}";
        var lastProgress = string.Empty;
        try
        {
            await client.DownloadAndWaitAsync(
                modelId,
                progress =>
                {
                    var line = progress.Detail is null ? progress.Message : $"{progress.Message}… {progress.Detail}";
                    if (!string.Equals(line, lastProgress, StringComparison.Ordinal))
                    {
                        lastProgress = line;
                        Console.WriteLine(line);
                    }
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        await SyncCatalogAsync(runtime, client, cancellationToken);
        Console.WriteLine($"Downloaded {modelId}");
    }

    /// <summary>
    /// Pinned syncCatalog: force a live provider refresh (15 s cap, allowNetwork even when
    /// the CLI is offline) and surface the pinned timeout/error messages.
    /// </summary>
    internal static async Task SyncCatalogAsync(
        ModelRuntime runtime,
        LlamaClient client,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CatalogSyncTimeoutMs);
        var result = await runtime.RefreshAsync(new ModelsRefreshOptions
        {
            Providers = [BuiltinProviders.LlamaCppProviderId],
            Force = true,
            AllowNetwork = true,
            CancellationToken = cts.Token,
        });
        if (result.Aborted)
        {
            throw new InvalidOperationException("Model catalog refresh timed out.");
        }

        if (result.Errors.TryGetValue(BuiltinProviders.LlamaCppProviderId, out var error))
        {
            throw error;
        }
    }

    /// <summary>Pinned connectionErrorMessage: fetch failures read as an unreachable server.</summary>
    internal static string ConnectionErrorMessage(Exception error)
    {
        var text = $"{error.GetType().Name} {error.Message}".ToLowerInvariant();
        return text.Contains("httprequest", StringComparison.Ordinal) ||
               text.Contains("timed out", StringComparison.Ordinal) ||
               text.Contains("task was canceled", StringComparison.Ordinal)
            ? "Could not connect to the server."
            : error.Message;
    }

    /// <summary>Pinned modelDescription: loaded/sleeping flag plus context when loaded.</summary>
    internal static string ModelDescription(LlamaModelInfo model)
    {
        var details = new List<string>();
        var loaded = model.Status.Value is LlamaModelStatusValues.Loaded or LlamaModelStatusValues.Sleeping;
        if (loaded)
        {
            details.Add("loaded");
        }
        else if (model.Status.Value != LlamaModelStatusValues.Unloaded)
        {
            details.Add(model.Status.Value);
        }

        var context = loaded ? ContextLabel(model) : null;
        if (context is not null)
        {
            details.Add($"{context} context");
        }

        return string.Join(" · ", details);
    }

    /// <summary>Pinned contextLabel: n_ctx (k-rounded) from metadata, else --ctx-size args.</summary>
    internal static string? ContextLabel(LlamaModelInfo model)
    {
        var context = model.Meta?.NCtx ?? model.Meta?.NCtxTrain;
        if (context is { } value && value > 0)
        {
            return value >= 1000 ? $"{Math.Round(value / 1000.0)}k" : value.ToString();
        }

        var args = model.Status.Args ?? [];
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] is not ("--ctx-size" or "-c" or "-ctx"))
            {
                continue;
            }

            if (int.TryParse(args[i + 1], out var argValue) && argValue > 0)
            {
                return argValue >= 1000 ? $"{Math.Round(argValue / 1000.0)}k" : argValue.ToString();
            }
        }

        return null;
    }

    private static string QuantizationLabel(HuggingFaceQuantization entry)
    {
        var parts = new List<string>();
        if (entry.Size is { } size)
        {
            parts.Add(LlamaClient.FormatBytes(size));
        }

        if (entry.Name == "Q4_K_M")
        {
            parts.Add("recommended");
        }

        return parts.Count > 0 ? $"{entry.Name} · {string.Join(" · ", parts)}" : entry.Name;
    }

    /// <summary>Pinned parseHuggingFaceModel: repository plus optional :quantization suffix.</summary>
    internal static (string Repository, string? Quantization) ParseHuggingFaceModel(string value)
    {
        var colon = value.IndexOf(':', value.IndexOf('/') + 1);
        return colon < 0
            ? (value, null)
            : (value[..colon], value[(colon + 1)..]);
    }

    private static async Task<string> PromptAsync(string message, CancellationToken cancellationToken)
    {
        Console.Write(message);
        var input = await Task.Run(() => Console.ReadLine(), cancellationToken);
        if (input is null)
        {
            throw new OperationCanceledException("Prompt closed.");
        }

        return input.Trim();
    }

    private static async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
    {
        var input = await PromptAsync($"{title} {message} (y/N): ", cancellationToken);
        return input.Length > 0 && (input[0] == 'y' || input[0] == 'Y');
    }

    private static async Task<string?> SelectAsync(
        string title,
        IReadOnlyList<string> options,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(title);
        for (var i = 0; i < options.Count; i++)
        {
            Console.WriteLine($"  {i + 1,2}. {options[i]}");
        }

        var input = await PromptAsync($"Select an option (blank to cancel): ", cancellationToken);
        return int.TryParse(input, out var selected) && selected >= 1 && selected <= options.Count
            ? options[selected - 1]
            : null;
    }
}

/// <summary>
/// Hugging Face API client for GGUF model discovery (pinned extensions/llama/huggingface.ts):
/// search sorted by downloads, quantization aggregation from .gguf siblings (shards merged,
/// mmproj sidecars skipped), gated-model flag, and rate-limit error shaping.
/// </summary>
internal sealed class HuggingFaceClient
{
    private const string DefaultBaseUrl = "https://huggingface.co";
    private const int SearchLimit = 20;
    private const int RequestTimeoutMs = 15_000;

    private static readonly Regex QuantizationPattern = new(
        @"(?:^|[-_.])((?:UD-)?(?:IQ\d(?:_[A-Z0-9]+)+|Q\d(?:_[A-Z0-9]+)+|BF16|F16|F32|MXFP\d(?:_[A-Z0-9]+)*))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShardSuffixPattern = new(
        @"-\d{5}-of-\d{5}$",
        RegexOptions.Compiled);

    private readonly string? _token;
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    public HuggingFaceClient(string? token, string baseUrl = DefaultBaseUrl)
        : this(token, baseUrl, null)
    {
    }

    /// <summary>Test constructor with an injectable transport (RecordingHandler-style fakes).</summary>
    internal HuggingFaceClient(string? token, string baseUrl, HttpClient? httpClient)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _baseUrl = baseUrl.TrimEnd('/');
        _http = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Resolves an HF token (pinned findHuggingFaceToken): HF_TOKEN env, then token files at
    /// HF_TOKEN_PATH, HF_HOME/token, XDG_CACHE_HOME/huggingface/token, ~/.cache/huggingface/token.
    /// </summary>
    internal static Task<string?> FindHuggingFaceToken()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("HF_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return Task.FromResult<string?>(fromEnvironment);
        }

        var candidates = new Queue<string>(new[]
        {
            Environment.GetEnvironmentVariable("HF_TOKEN_PATH"),
            Environment.GetEnvironmentVariable("HF_HOME") is { } home
                ? Path.Combine(home, "token")
                : null,
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { } xdg
                ? Path.Combine(xdg, "huggingface", "token")
                : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "token"),
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))!);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (candidates.Count > 0)
        {
            var path = candidates.Dequeue();
            if (!seen.Add(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var token = File.ReadAllText(path).Trim();
                if (token.Length > 0)
                {
                    return Task.FromResult<string?>(token);
                }
            }
            catch
            {
                // Unreadable token paths are skipped, as pinned.
            }
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>GGUF model search sorted by downloads (pinned search).</summary>
    public async Task<IReadOnlyList<HuggingFaceModel>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var queryText = $"search={Uri.EscapeDataString(query)}&filter=gguf&sort=downloads&direction=-1&limit={SearchLimit}";
        var payload = await RequestAsync($"/api/models?{queryText}", cancellationToken);
        if (payload.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Hugging Face returned invalid search results");
        }

        var models = new List<HuggingFaceModel>();
        foreach (var value in payload.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            models.Add(new HuggingFaceModel(
                id.GetString()!,
                value.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Number
                    ? downloads.GetInt64()
                    : 0));
        }

        return models;
    }

    /// <summary>Model details with aggregated GGUF quantizations (pinned details).</summary>
    public async Task<HuggingFaceModelDetails> DetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        var encodedId = string.Join("/", id.Split('/').Select(Uri.EscapeDataString));
        var payload = await RequestAsync($"/api/models/{encodedId}?blobs=true", cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Hugging Face returned invalid model details");
        }

        var sizes = new Dictionary<string, (long Total, bool Complete)>(StringComparer.Ordinal);
        if (payload.TryGetProperty("siblings", out var siblings) && siblings.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in siblings.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("rfilename", out var rfilename) ||
                    rfilename.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var full = rfilename.GetString()!;
                if (!full.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var filename = full.Split('/').Last();
                if (filename.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stem = ShardSuffixPattern.Replace(filename[..^5], string.Empty);
                var match = QuantizationPattern.Match(stem);
                if (!match.Success)
                {
                    continue;
                }

                var quantization = match.Groups[1].Value.ToUpperInvariant();
                var current = sizes.TryGetValue(quantization, out var existing)
                    ? existing
                    : (Total: 0L, Complete: true);
                if (value.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number)
                {
                    current = (current.Total + size.GetInt64(), current.Complete);
                }
                else
                {
                    current = (current.Total, false);
                }

                sizes[quantization] = current;
            }
        }

        var quantizations = sizes
            .Select(pair => new HuggingFaceQuantization(pair.Key,
                pair.Value.Complete ? pair.Value.Total : (long?)null))
            .OrderBy(entry => entry.Name == "Q4_K_M" ? 0 : 1)
            .ThenBy(entry => entry.Size ?? long.MaxValue)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();

        string? gated = null;
        if (payload.TryGetProperty("gated", out var gatedElement) && gatedElement.ValueKind == JsonValueKind.String)
        {
            var value = gatedElement.GetString();
            if (value == "auto" || value == "manual")
            {
                gated = value;
            }
        }

        return new HuggingFaceModelDetails(
            payload.TryGetProperty("id", out var modelId) && modelId.ValueKind == JsonValueKind.String
                ? modelId.GetString()!
                : id,
            gated,
            quantizations);
    }

    private async Task<JsonElement> RequestAsync(string path, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeoutMs);
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
        if (_token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }

        using var response = await _http.SendAsync(request, timeoutCts.Token);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonElement? json = null;
        if (body.Length > 0)
        {
            try
            {
                json = JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException)
            {
                json = null;
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ErrorText(json, response, $"Hugging Face returned HTTP {(int)response.StatusCode}"));
        }

        return json ?? throw new InvalidOperationException("Hugging Face returned an empty response");
    }

    private static string ErrorText(JsonElement? payload, HttpResponseMessage response, string fallback)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = ParseRateLimitDelay(
                response.Headers.TryGetValues("Retry-After", out var retryAfter) ? retryAfter.FirstOrDefault() : null,
                response.Headers.TryGetValues("Ratelimit", out var ratelimit) ? ratelimit.FirstOrDefault() : null);
            return delay is { } seconds
                ? $"Hugging Face rate limit reached; retry in {seconds}s"
                : "Hugging Face rate limit reached";
        }

        if (payload is { } element &&
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(error.GetString()))
        {
            return error.GetString()!;
        }

        return fallback;
    }

    private static int? ParseRateLimitDelay(string? retryAfter, string? ratelimit)
    {
        if (int.TryParse(retryAfter, out var seconds))
        {
            return seconds;
        }

        var match = System.Text.RegularExpressions.Regex.Match(ratelimit ?? string.Empty, @"(?:^|;)t=(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}

/// <summary>One HF search result (pinned HuggingFaceModel).</summary>
internal sealed record HuggingFaceModel(string Id, long Downloads);

/// <summary>One aggregated GGUF quantization with total shard size (pinned HuggingFaceQuantization).</summary>
internal sealed record HuggingFaceQuantization(string Name, long? Size);

/// <summary>HF model details: gated flag plus quantizations sorted for selection (pinned HuggingFaceModelDetails).</summary>
internal sealed record HuggingFaceModelDetails(string Id, string? Gated, IReadOnlyList<HuggingFaceQuantization> Quantizations);
