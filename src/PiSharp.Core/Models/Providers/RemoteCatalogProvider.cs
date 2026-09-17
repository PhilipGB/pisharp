using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Core.Models.Providers;

/// <summary>
/// pi.dev remote catalog overlay (pinned Pi: withRemoteCatalog). Layers a persisted,
/// ETag-validated remote model list on top of a static built-in provider. The overlay
/// is restored from the models store before any network access; a 304 only moves the
/// freshness window, 404/501 clear the overlay, and transient errors keep the cached
/// body and validator.
/// </summary>
public static class RemoteCatalogProvider
{
    public const string DefaultCatalogBaseUrl = "https://pi.dev";
    public const int RefreshIntervalMs = 4 * 60 * 60 * 1000;
    private const int AttemptTimeoutMs = 4_000;

    private static readonly HttpClient SharedClient = new();

    /// <summary>Wraps a built-in provider with the pi.dev remote catalog overlay.</summary>
    public static ProviderSpec WithRemoteCatalog(
        ProviderSpec provider,
        string? catalogBaseUrl = null,
        long? localGeneratedAt = null)
    {
        var baseUrl = (catalogBaseUrl ?? DefaultCatalogBaseUrl).TrimEnd('/');
        var dynamicModels = new List<ModelInfo>();
        var gate = new object();

        return new ProviderSpec
        {
            Id = provider.Id,
            Name = provider.Name,
            BaseUrl = provider.BaseUrl,
            Headers = provider.Headers,
            Auth = provider.Auth,
            GetModels = () =>
            {
                lock (gate)
                {
                    return MergeModels(provider.GetModels(), dynamicModels);
                }
            },
            RefreshModelsAsync = async context =>
            {
                var restored = RemoteModels(context.Stored, localGeneratedAt)
                    .Where(model => model.Provider == provider.Id)
                    .ToArray();
                var restoredPublished = await context.Publish(new ModelsPublication
                {
                    Update = () =>
                    {
                        lock (gate)
                        {
                            dynamicModels = restored.ToList();
                        }
                    },
                });
                if (!restoredPublished)
                {
                    return;
                }

                if (!context.AllowNetwork || context.CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var stored = context.Stored;
                if (!context.Force &&
                    stored is not null &&
                    (DateTime.UtcNow - EpochFromMs(stored.CheckedAt)).TotalMilliseconds < RefreshIntervalMs)
                {
                    return;
                }

                // Only revalidate when a cached body backs the validator, so a 304 can never
                // leave the overlay empty.
                var validator = stored is { Models.Count: > 0, Etag: not null }
                    ? stored.Etag
                    : null;

                using var timeoutCts = new CancellationTokenSource(AttemptTimeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timeoutCts.Token);
                HttpResponseMessage response;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"{baseUrl}/api/models/providers/{Uri.EscapeDataString(provider.Id)}");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.UserAgent.ParseAdd($"PiSharp/{PiSharpVersion.Version}");
                    if (validator is not null)
                    {
                        request.Headers.TryAddWithoutValidation("If-None-Match", validator);
                    }

                    response = await SharedClient.SendAsync(request, linkedCts.Token);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"Model catalog request failed for {provider.Id}: timeout");
                }
                catch (HttpRequestException exception)
                {
                    throw new InvalidOperationException(
                        $"Model catalog request failed for {provider.Id}: {exception.Message}", exception);
                }

                if (context.CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var checkedAt = DateTime.UtcNow;
                if ((int)response.StatusCode == 304 && stored is not null)
                {
                    await context.Publish(new ModelsPublication
                    {
                        Persist = new ModelsStoreEntry
                        {
                            Models = stored.Models,
                            CheckedAt = EpochMsOf(checkedAt),
                            LastModified = stored.LastModified,
                            Etag = stored.Etag,
                        },
                    });
                    return;
                }

                var status = (int)response.StatusCode;
                if (status == 404 || status == 501)
                {
                    await context.Publish(new ModelsPublication
                    {
                        Persist = new ModelsStoreEntry
                        {
                            Models = stored?.Models ?? [],
                            CheckedAt = EpochMsOf(checkedAt),
                            LastModified = 0,
                        },
                    });
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Transient failure: the cached body and its validator stay valid, so keep the
                    // etag and let the next refresh revalidate instead of downloading the catalog.
                    await context.Publish(new ModelsPublication
                    {
                        Persist = new ModelsStoreEntry
                        {
                            Models = stored?.Models ?? [],
                            CheckedAt = EpochMsOf(checkedAt),
                            LastModified = stored?.LastModified ?? 0,
                            Etag = stored?.Etag,
                        },
                    });
                    throw new InvalidOperationException($"Model catalog request failed for {provider.Id}: {status}");
                }

                string body;
                using (response)
                {
                    body = await response.Content.ReadAsStringAsync(context.CancellationToken);
                }

                var refreshed = ParseCatalog(provider.Id, body, provider.DefaultApi);
                DateTime? lastModifiedHeader = null;
                if (response.Headers.TryGetValues("Last-Modified", out var lastModifiedValues)
                    && lastModifiedValues.FirstOrDefault() is { } lastModifiedValue
                    && DateTime.TryParse(
                        lastModifiedValue,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsedDate))
                {
                    lastModifiedHeader = parsedDate;
                }

                if (context.CancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var entry = new ModelsStoreEntry
                {
                    Models = refreshed,
                    CheckedAt = EpochMsOf(checkedAt),
                    LastModified = lastModifiedHeader is { } headerDate ? EpochMsOf(headerDate) : 0,
                    Etag = response.Headers.ETag?.Tag,
                };
                var published = RemoteModels(entry, localGeneratedAt)
                    .Where(model => model.Provider == provider.Id)
                    .ToArray();
                await context.Publish(new ModelsPublication
                {
                    Persist = entry,
                    Update = () =>
                    {
                        lock (gate)
                        {
                            dynamicModels = published.ToList();
                        }
                    },
                });
            },
            FilterModels = provider.FilterModels,
            DefaultApi = provider.DefaultApi,
        };
    }

    /// <summary>
    /// The overlay models to use for the entry: none when local generated data is
    /// newer, otherwise the stored remote list.
    /// </summary>
    internal static IReadOnlyList<ModelInfo> RemoteModels(
        ModelsStoreEntry? entry, long? localGeneratedAt)
    {
        if (entry is null)
        {
            return [];
        }

        if (localGeneratedAt is { } generated && entry.LastModified <= generated)
        {
            return [];
        }

        return entry.Models;
    }

    internal static IReadOnlyList<ModelInfo> MergeModels(
        IReadOnlyList<ModelInfo> baseline, IReadOnlyList<ModelInfo> dynamic)
    {
        var merged = new List<ModelInfo>(baseline);
        foreach (var model in dynamic)
        {
            var index = merged.FindIndex(entry => entry.Id == model.Id);
            if (index >= 0)
            {
                merged[index] = model;
            }
            else
            {
                merged.Add(model);
            }
        }

        return merged;
    }

    /// <summary>
    /// Parses a catalog body: a model array, an object with a "models" array, or an
    /// object map of models. Entries without an "id" are dropped.
    /// </summary>
    internal static IReadOnlyList<ModelInfo> ParseCatalog(
        string providerId, string body, string? defaultApi)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Invalid model catalog for provider \"{providerId}\"", exception);
        }

        List<JsonObject>? entries;
        if (root is JsonArray array)
        {
            entries = array.OfType<JsonObject>().ToList();
        }
        else if (root is JsonObject obj)
        {
            if (obj["models"] is JsonArray modelsArray)
            {
                entries = modelsArray.OfType<JsonObject>().ToList();
            }
            else if (obj.Count > 0)
            {
                entries = obj.Select(kv => kv.Value).OfType<JsonObject>().ToList();
            }
            else
            {
                entries = null;
            }
        }
        else
        {
            entries = null;
        }

        if (entries is null)
        {
            throw new InvalidOperationException($"Invalid model catalog for provider \"{providerId}\"");
        }

        var models = new List<ModelInfo>();
        foreach (var modelObj in entries)
        {
            if (modelObj["id"]?.GetValue<string>() is not { Length: > 0 } id)
            {
                continue;
            }

            var model = ParseModel(id, modelObj, providerId, defaultApi);
            if (model is not null)
            {
                models.Add(model);
            }
        }

        return models;
    }

    private static ModelInfo? ParseModel(
        string id, JsonObject obj, string providerId, string? defaultApi)
    {
        var api = obj["api"]?.GetValue<string>() ?? defaultApi ?? ModelApi.OpenAiCompletions;
        var name = obj["name"]?.GetValue<string>() ?? id;
        var baseUrl = obj["baseUrl"]?.GetValue<string>() ?? string.Empty;

        var input = new List<string> { "text" };
        if (obj["input"] is JsonArray inputArray)
        {
            input = inputArray
                .Select(node => node?.GetValue<string>())
                .Where(value => !string.IsNullOrEmpty(value))
                .Select(value => value!)
                .ToList();
        }

        var cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 };
        if (obj["cost"] is JsonObject costObj)
        {
            cost = new ModelCost
            {
                Input = ReadDouble(costObj, "input"),
                Output = ReadDouble(costObj, "output"),
                CacheRead = ReadDouble(costObj, "cacheRead"),
                CacheWrite = ReadDouble(costObj, "cacheWrite"),
            };
        }

        IReadOnlyDictionary<string, string?>? thinkingLevelMap = null;
        if (obj["thinkingLevelMap"] is JsonObject mapObj)
        {
            thinkingLevelMap = mapObj.ToDictionary(
                e => e.Key,
                e => e.Value?.GetValue<string>(),
                StringComparer.Ordinal);
        }

        return new ModelInfo
        {
            Id = id,
            Name = name,
            Api = api,
            Provider = providerId,
            BaseUrl = baseUrl,
            Reasoning = obj["reasoning"]?.GetValue<bool>() ?? false,
            Input = input,
            Cost = cost,
            ContextWindow = (int)(obj["contextWindow"]?.GetValue<double>() ?? 128_000),
            MaxTokens = (int)(obj["maxTokens"]?.GetValue<double>() ?? 16_384),
            Headers = obj["headers"] is JsonObject headersObj
                ? headersObj.ToDictionary(e => e.Key, e => e.Value?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal)
                : null,
            ThinkingLevelMap = thinkingLevelMap,
        };
    }

    private static double ReadDouble(JsonObject obj, string name)
        => obj[name]?.GetValue<double>() ?? 0;

    internal static DateTime EpochFromMs(long ms)
        => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    internal static long EpochMsOf(DateTime utc)
        => (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
}

/// <summary>PiSharp build version used in the remote catalog User-Agent.</summary>
public static class PiSharpVersion
{
    public const string Version = "0.1.0";
}
