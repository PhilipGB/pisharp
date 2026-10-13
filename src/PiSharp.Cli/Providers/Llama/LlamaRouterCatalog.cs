using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Runtime.Classifiers;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

internal sealed record LlamaRouterCacheEntry(
    [property: JsonPropertyName("models")] IReadOnlyList<LlamaRouterModelInfo> Models,
    [property: JsonPropertyName("contextWindows")] IReadOnlyDictionary<string, int> ContextWindows,
    [property: JsonPropertyName("chatTemplates")] IReadOnlyDictionary<string, string> ChatTemplates,
    [property: JsonPropertyName("routerAutoload")] bool RouterAutoload = false);

internal sealed record LlamaRouterCatalogResult(
    IReadOnlyList<ModelDescriptor> Models,
    IReadOnlyList<ClassifierModel> Classifiers,
    LlamaRouterCacheEntry Cache);

internal static class LlamaRouterCatalog
{
    private const string Provider = "llama.cpp";
    private const string ChatApi = "openai-completions";
    private const string TokenClassifierApi = "llama-cpp-classify";
    private const string DecisionClassifierApi = "typesafe-system-one";
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static async Task<LlamaRouterCatalogResult> RefreshAsync(LlamaRouterClient client,
        LlamaRouterCacheEntry? previous, CancellationToken cancellationToken)
    {
        var models = await client.ListAsync(cancellationToken).ConfigureAwait(false);
        var autoload = false;
        if (models.Any(model => model.Status.Value == "unloaded" && model.Source == "preset" && model.Status.Failed != true))
        {
            try { autoload = (await client.GetPropsAsync(null, cancellationToken).ConfigureAwait(false)).ModelsAutoload == true; }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException) { }
        }

        var selectable = models.Where(model => IsSelectable(model, autoload)).ToArray();
        var chatModels = selectable.Where(IsChatModel).ToArray();
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        var loaded = chatModels.Where(model => model.Status.Value == "loaded").ToArray();
        var properties = await Task.WhenAll(loaded.Select(async model =>
            (model.Id, Props: await client.GetPropsAsync(model.Id, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
        foreach (var (id, props) in properties)
            if (!string.IsNullOrEmpty(props.ChatTemplate)) templates[id] = props.ChatTemplate;

        var contextWindows = previous?.ContextWindows is null
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(previous.ContextWindows, StringComparer.Ordinal);
        var projected = Project(selectable, client.ServerUrl, contextWindows, templates);
        var ids = selectable.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var key in contextWindows.Keys.Where(key => !ids.Contains(key)).ToArray()) contextWindows.Remove(key);
        foreach (var model in projected.Models)
            if (model.ContextLength is > 0) contextWindows[model.Id] = model.ContextLength.Value;
        foreach (var model in projected.Classifiers)
            if (model.ContextWindow > 0) contextWindows[model.Id] = model.ContextWindow;

        return projected with
        {
            Cache = new LlamaRouterCacheEntry(models, contextWindows,
                new Dictionary<string, string>(templates, StringComparer.Ordinal), autoload)
        };
    }

    public static LlamaRouterCatalogResult Restore(LlamaRouterCacheEntry? cache, Uri serverUrl)
    {
        if (cache is null)
            return new([], [], new LlamaRouterCacheEntry([], new Dictionary<string, int>(), new Dictionary<string, string>()));
        var selectable = cache.Models.Where(model => IsSelectable(model, cache.RouterAutoload)).ToArray();
        var projected = Project(selectable, serverUrl, cache.ContextWindows, cache.ChatTemplates);
        return projected with { Cache = cache };
    }

    private static LlamaRouterCatalogResult Project(IReadOnlyList<LlamaRouterModelInfo> models, Uri serverUrl,
        IReadOnlyDictionary<string, int> cachedContextWindows,
        IReadOnlyDictionary<string, string> chatTemplates)
    {
        var inferenceUrl = new Uri(serverUrl.AbsoluteUri.TrimEnd('/') + "/v1");
        var chat = models.Where(IsChatModel).Select(model => ToChatModel(model, inferenceUrl,
            ContextWindowOf(model, cachedContextWindows.GetValueOrDefault(model.Id)),
            model.Status.Value == "loaded" && chatTemplates.TryGetValue(model.Id, out var template) &&
            template.Contains("enable_thinking", StringComparison.Ordinal))).ToArray();
        var classifiers = models.Select(model => ToClassifierModel(model, serverUrl, inferenceUrl,
            ContextWindowOf(model, cachedContextWindows.GetValueOrDefault(model.Id)))).ToArray();
        return new(chat, classifiers, new LlamaRouterCacheEntry(models,
            new Dictionary<string, int>(cachedContextWindows, StringComparer.Ordinal),
            new Dictionary<string, string>(chatTemplates, StringComparer.Ordinal)));
    }

    private static bool IsSelectable(LlamaRouterModelInfo model, bool routerAutoload) =>
        model.Status.Value is "loaded" or "sleeping" ||
        routerAutoload && model.Status.Value == "unloaded" && model.Status.Failed != true && model.Source == "preset";

    private static bool IsChatModel(LlamaRouterModelInfo model)
    {
        var outputs = model.Architecture?.OutputModalities;
        return outputs?.Contains("decisions", StringComparer.Ordinal) != true ||
            outputs.Contains("text", StringComparer.Ordinal);
    }

    private static ModelDescriptor ToChatModel(LlamaRouterModelInfo model, Uri inferenceUrl, int contextWindow,
        bool reasoning)
    {
        var thinkingMap = reasoning
            ? JsonSerializer.SerializeToElement(new Dictionary<string, string?>
            {
                ["off"] = "off",
                ["minimal"] = null,
                ["low"] = null,
                ["medium"] = "medium",
                ["high"] = null,
                ["xhigh"] = null
            }, s_json)
            : (JsonElement?)null;
        var compatibilityValues = new Dictionary<string, object>
        {
            ["supportsStore"] = false,
            ["supportsDeveloperRole"] = false,
            ["supportsReasoningEffort"] = false,
            ["supportsUsageInStreaming"] = true,
            ["supportsStrictMode"] = false,
            ["maxTokensField"] = "max_tokens"
        };
        if (reasoning) compatibilityValues["thinkingFormat"] = "qwen-chat-template";
        var compatibility = JsonSerializer.SerializeToElement(compatibilityValues, s_json);
        var input = model.Architecture?.InputModalities?.Contains("image", StringComparer.Ordinal) == true
            ? new[] { "text", "image" } : ["text"];
        return new ModelDescriptor(model.Id, Provider, contextWindow, model.Status.Value, reasoning,
            new ModelPricing(0, 0, 0, CachedWrite: 0), Provider,
            Name: model.Id, MaxOutputTokens: contextWindow, Input: input, Api: ChatApi,
            BaseUrl: inferenceUrl.AbsoluteUri.TrimEnd('/'), ThinkingLevelMap: thinkingMap,
            Compatibility: compatibility);
    }

    private static ClassifierModel ToClassifierModel(LlamaRouterModelInfo model, Uri serverUrl,
        Uri inferenceUrl, int contextWindow)
    {
        var decision = model.Architecture?.OutputModalities?.Contains("decisions", StringComparer.Ordinal) == true;
        return new ClassifierModel(Provider, model.Id, decision ? DecisionClassifierApi : TokenClassifierApi,
            decision ? inferenceUrl : serverUrl, contextWindow, new ModelPricing(0, 0, 0), Name: model.Id,
            Input: ["text"]);
    }

    private static int ContextWindowOf(LlamaRouterModelInfo model, int? cached)
    {
        if (model.Metadata?.ContextWindow is > 0) return model.Metadata.ContextWindow.Value;
        var arguments = model.Status.Args ?? [];
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index] is not ("--ctx-size" or "-c" or "-ctx")) continue;
            if (int.TryParse(arguments[index + 1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var configured) && configured > 0)
                return configured;
        }
        if (cached is > 0) return cached.Value;
        return model.Metadata?.TrainingContextWindow is > 0 ? model.Metadata.TrainingContextWindow.Value : 128000;
    }
}

internal sealed class LlamaRouterCatalogStore(string agentDirectory)
{
    private const int MaxCacheBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(Path.GetFullPath(agentDirectory), "llama-router-cache.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<LlamaRouterCacheEntry?> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            var info = new FileInfo(_path);
            if (info.LinkTarget is not null) throw new InvalidDataException("Refusing a symbolic-link llama router cache.");
            if (info.Length > MaxCacheBytes) throw new InvalidDataException("llama router cache exceeds 1MB.");
            var bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            var cache = JsonSerializer.Deserialize<LlamaRouterCacheEntry>(bytes, s_json);
            if (cache?.Models is null || cache.ContextWindows is null || cache.ChatTemplates is null)
                throw new InvalidDataException("Invalid llama router cache.");
            return cache;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid llama router cache.", error); }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(LlamaRouterCacheEntry cache, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && new FileInfo(_path).LinkTarget is not null)
                throw new InvalidDataException("Refusing a symbolic-link llama router cache.");
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(cache, s_json), cancellationToken)
                .ConfigureAwait(false);
            if (new FileInfo(temporary).Length > MaxCacheBytes) throw new InvalidDataException("llama router cache exceeds 1MB.");
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }
}
