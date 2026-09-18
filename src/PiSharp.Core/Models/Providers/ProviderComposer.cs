using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.Providers;

/// <summary>
/// Provider status for auth source reporting (pinned Pi: AuthStatus).
/// </summary>
public sealed record AuthStatus(bool Configured, string? Source = null, string? Label = null);

/// <summary>
/// Compatibility request config resolved for a model (pinned Pi:
/// CompatibilityRequestConfig).
/// </summary>
public sealed record CompatibilityRequestConfig(IReadOnlyDictionary<string, string>? Headers, bool AuthHeader);

/// <summary>
/// Composition of the built-in layer and the models.json layer (pinned Pi:
/// provider-composer.ts). Composes without reading credentials; structural errors
/// throw so registration/reload reports them immediately.
/// </summary>
public static class ProviderComposer
{
    /// <summary>Applies the models.json layer to a base model list (pinned Pi: applyModelsJson).</summary>
    public static List<ModelInfo> ApplyModelsJson(
        string providerId,
        IReadOnlyList<ModelInfo> baseModels,
        ModelsJsonProvider? config)
    {
        if (config is null)
        {
            return baseModels.ToList();
        }

        if (config.OAuth is not null && config.BaseUrl is null)
        {
            throw new InvalidOperationException($"Provider {providerId}: \"baseUrl\" is required when \"oauth\" is set.");
        }

        var hasOverrides = config.ModelOverrides is { Count: > 0 };
        if (config.Models is not { Count: > 0 } &&
            config.BaseUrl is null &&
            config.Headers is null &&
            config.Compat is null &&
            !hasOverrides &&
            config.ApiKey is null &&
            config.OAuth is null &&
            config.AuthHeader is null)
        {
            throw new InvalidOperationException(
                $"Provider {providerId}: must specify \"baseUrl\", \"headers\", \"compat\", \"modelOverrides\", or \"models\".");
        }

        var models = baseModels
            .Select(model => model with
            {
                // radius providers own their model base URLs per-gateway.
                BaseUrl = config.OAuth == "radius"
                    ? model.BaseUrl
                    : config.BaseUrl ?? model.BaseUrl,
                Compat = ModelCompatReader.Merge(model.Compat, ToRaw(config.Compat)),
            })
            .ToList();

        if (config.Models is not null)
        {
            foreach (var definition in config.Models)
            {
                var existingIndex = models.FindIndex(model => model.Id == definition.Id);
                var defaults = FindModelDefaults(models, definition.Id, definition.Api ?? config.Api);
                var model = ModelFromJson(providerId, definition, config, defaults);
                if (existingIndex >= 0)
                {
                    models[existingIndex] = model;
                }
                else
                {
                    models.Add(model);
                }
            }
        }

        return models;
    }

    private static Dictionary<string, object?>? ToRaw(ModelCompatValue? compat)
        => compat is null ? null : ModelCompatReader.ToDictionary(compat.Values);

    private static ModelInfo ModelFromJson(
        string providerId,
        ModelsJsonModel definition,
        ModelsJsonProvider providerConfig,
        ModelInfo? defaults)
    {
        var api = definition.Api ?? providerConfig.Api ?? defaults?.Api;
        if (api is null)
        {
            throw new InvalidOperationException(
                $"Provider {providerId}, model {definition.Id}: no \"api\" specified. Set at provider or model level.");
        }

        var baseUrl = definition.BaseUrl ?? providerConfig.BaseUrl ?? defaults?.BaseUrl;
        if (baseUrl is null)
        {
            throw new InvalidOperationException($"Provider {providerId}: \"baseUrl\" is required when defining custom models.");
        }

        if (definition.ContextWindow is { } contextWindow && contextWindow <= 0)
        {
            throw new InvalidOperationException($"Provider {providerId}, model {definition.Id}: invalid contextWindow");
        }

        if (definition.MaxTokens is { } maxTokens && maxTokens <= 0)
        {
            throw new InvalidOperationException($"Provider {providerId}, model {definition.Id}: invalid maxTokens");
        }

        var cost = definition.Cost is null
            ? new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 }
            : new ModelCost
            {
                Input = definition.Cost.Input,
                Output = definition.Cost.Output,
                CacheRead = definition.Cost.CacheRead,
                CacheWrite = definition.Cost.CacheWrite,
                Tiers = definition.Cost.Tiers is { } tiers
                    ? tiers.Select(t => new ModelCostTier
                    {
                        InputTokensAbove = t.InputTokensAbove,
                        Input = t.Input,
                        Output = t.Output,
                        CacheRead = t.CacheRead,
                        CacheWrite = t.CacheWrite,
                    }).ToArray()
                    : null,
            };

        return new ModelInfo
        {
            Id = definition.Id,
            Name = definition.Name ?? definition.Id,
            Api = api,
            Provider = providerId,
            BaseUrl = baseUrl,
            Reasoning = definition.Reasoning ?? false,
            ThinkingLevelMap = definition.ThinkingLevelMap,
            Input = definition.Input ?? ["text"],
            Cost = cost,
            ContextWindow = (int)(definition.ContextWindow ?? 128_000),
            MaxTokens = (int)(definition.MaxTokens ?? 16_384),
            SamplingParams = definition.SamplingParams,
            Compat = ModelCompatReader.Merge(ToRaw(providerConfig.Compat), ToRaw(definition.Compat)),
        };
    }

    private static ModelInfo? FindModelDefaults(
        IReadOnlyList<ModelInfo> models,
        string modelId,
        string? api)
    {
        return models.FirstOrDefault(model => model.Id == modelId)
            ?? (api is null ? null : models.FirstOrDefault(model => model.Api == api))
            ?? models.FirstOrDefault(model => model.Api == ModelApi.OpenAiCompletions)
            ?? (models.Count > 0 ? models[0] : null);
    }

    /// <summary>Applies a model override; defined values win (pinned Pi: applyModelOverride).</summary>
    public static ModelInfo ApplyModelOverride(ModelInfo model, ModelsJsonModelOverride override_)
    {
        ModelCost? cost = null;
        if (override_.Cost is { } overrideCost)
        {
            cost = new ModelCost
            {
                Input = overrideCost.Input,
                Output = overrideCost.Output,
                CacheRead = overrideCost.CacheRead,
                CacheWrite = overrideCost.CacheWrite,
                Tiers = overrideCost.Tiers is { } tiers
                    ? tiers.Select(t => new ModelCostTier
                    {
                        InputTokensAbove = t.InputTokensAbove,
                        Input = t.Input,
                        Output = t.Output,
                        CacheRead = t.CacheRead,
                        CacheWrite = t.CacheWrite,
                    }).ToArray()
                    : model.Cost.Tiers,
            };
        }

        var thinkingLevelMap = override_.ThinkingLevelMap is null
            ? model.ThinkingLevelMap
            : MergeThinkingLevelMaps(model.ThinkingLevelMap, override_.ThinkingLevelMap);

        var samplingParams = override_.SamplingParams is null
            ? model.SamplingParams
            : model.SamplingParams is null
                ? override_.SamplingParams
                : model.SamplingParams.Concat(override_.SamplingParams)
                    .GroupBy(e => e.Key)
                    .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

        return model with
        {
            Name = override_.Name ?? model.Name,
            Reasoning = override_.Reasoning ?? model.Reasoning,
            ThinkingLevelMap = thinkingLevelMap,
            Input = override_.Input ?? model.Input,
            Cost = cost ?? model.Cost,
            ContextWindow = (int)(override_.ContextWindow ?? model.ContextWindow),
            MaxTokens = (int)(override_.MaxTokens ?? model.MaxTokens),
            SamplingParams = samplingParams,
            Compat = ModelCompatReader.Merge(model.Compat, ToRaw(override_.Compat)),
        };
    }

    private static IReadOnlyDictionary<string, string?> MergeThinkingLevelMaps(
        IReadOnlyDictionary<string, string?>? baseMap,
        IReadOnlyDictionary<string, string?>? overrideMap)
    {
        if (overrideMap is null)
        {
            return baseMap is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : baseMap.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        }

        var merged = new Dictionary<string, string?>(baseMap ?? new Dictionary<string, string?>(), StringComparer.Ordinal);
        foreach (var (level, value) in overrideMap)
        {
            merged[level] = value;
        }

        return merged;
    }

    /// <summary>
    /// Composes the built-in and models.json layers into a ProviderSpec (pinned Pi:
    /// composeModelProvider, minus the extension layer which PiSharp replaces with
    /// direct provider registration).
    /// </summary>
    public static ProviderSpec ComposeModelProvider(string providerId, ProviderSpec? base_, ModelConfig modelConfig)
    {
        var config = modelConfig.GetProvider(providerId);
        var getModels = new Func<List<ModelInfo>>(() =>
        {
            var models = ApplyModelsJson(providerId, base_?.GetModels() ?? [], config);
            if (config?.ModelOverrides is null)
            {
                return models;
            }

            return models
                .Select(model => config.ModelOverrides!.TryGetValue(model.Id, out var override_)
                    ? ApplyModelOverride(model, override_)
                    : model)
                .ToList();
        });

        // Validate eagerly so registration/reload reports structural errors immediately.
        getModels();

        var apiKey = ComposeApiKeyAuth(providerId, base_, config);
        var oauth = ComposeOAuthAuth(providerId, base_, config);
        if (apiKey is null && oauth is null)
        {
            throw new InvalidOperationException($"Provider {providerId}: no authentication method configured.");
        }

        return new ProviderSpec
        {
            Id = providerId,
            Name = config?.Name ?? base_?.Name ?? providerId,
            BaseUrl = config?.BaseUrl ?? base_?.BaseUrl,
            Headers = base_?.Headers,
            Auth = new ProviderAuth(apiKey, oauth),
            GetModels = getModels,
            RefreshModelsAsync = base_?.RefreshModelsAsync,
            FilterModels = base_?.FilterModels,
            DefaultApi = base_?.DefaultApi ?? config?.Api,
        };
    }

    private static ApiKeyAuth? ComposeApiKeyAuth(
        string providerId,
        ProviderSpec? base_,
        ModelsJsonProvider? config)
    {
        var inherited = base_?.Auth.ApiKey;
        var rawKey = config?.ApiKey;
        var oauth = base_?.Auth.OAuth;
        // OAuth-only providers get no fabricated API-key login method.
        if (inherited is null && rawKey is null && oauth is not null)
        {
            return null;
        }

        var rawHeaders = config?.Headers;
        var authHeader = config?.AuthHeader ?? false;

        var name = inherited?.Name ?? "API key";
        var login = inherited?.Login ?? (interaction => Task.Run(async () =>
        {
            var key = await interaction.PromptAsync(new SecretPromptStep("Enter API key"), interaction.Signal);
            return new ApiKeyCredential(key);
        }, interaction.Signal));

        var check = new Func<ApiKeyAuthInput, Task<AuthCheck?>>(async input =>
        {
            if (input.Credential is not null)
            {
                if (inherited?.Check is { } inheritedCheck)
                {
                    return await inheritedCheck(input);
                }

                if (input.Credential.Key is { } key)
                {
                    return new AuthCheck("stored credential", "api_key");
                }

                var inheritedAuth = inherited;
                if (inheritedAuth is null)
                {
                    return null;
                }

                var resolved = await inheritedAuth.ResolveAsync(input);
                return resolved is null ? null : new AuthCheck(resolved.Source, "api_key");
            }

            if (rawKey is not null)
            {
                if (ConfigValue.IsCommand(rawKey))
                {
                    return new AuthCheck("configured API key", "api_key");
                }

                foreach (var name_ in ConfigValue.GetEnvVarNames(rawKey))
                {
                    if (await input.Context.Env(name_) is null)
                    {
                        return null;
                    }
                }

                return new AuthCheck("configured API key", "api_key");
            }

            if (inherited?.Check is { } ambientCheck)
            {
                return await ambientCheck(input);
            }

            var inheritedAuthFinal = inherited;
            if (inheritedAuthFinal is null)
            {
                return null;
            }

            var inheritedResult = await inheritedAuthFinal.ResolveAsync(input);
            return inheritedResult is null ? null : new AuthCheck(inheritedResult.Source, "api_key");
        });

        var resolve = new Func<ApiKeyAuthInput, Task<AuthResult?>>(async input =>
        {
            AuthResult? result;
            if (input.Credential is not null)
            {
                result = inherited is not null
                    ? await inherited.ResolveAsync(input)
                    : input.Credential.Key is { } storedKey
                        ? new AuthResult
                        {
                            Auth = new ModelAuth { ApiKey = storedKey },
                            Source = "stored credential",
                        }
                        : null;
            }
            else if (rawKey is not null)
            {
                var key = ConfigValue.ResolveOrThrow(rawKey, $"API key for provider \"{providerId}\"", null);
                result = inherited is not null
                    ? await inherited.ResolveAsync(input with
                    {
                        Credential = new ApiKeyCredential(key),
                    })
                    : new AuthResult
                    {
                        Auth = new ModelAuth { ApiKey = key },
                        Source = "configured API key",
                    };
            }
            else
            {
                var inheritedAuthElse = inherited;
                result = inheritedAuthElse is null ? null : await inheritedAuthElse.ResolveAsync(input);
            }

            if (result is null)
            {
                return null;
            }

            var headerEnv = ConfigValue.ResolveHeaders(rawHeaders, null);
            return WithConfiguredAuth(result.Auth, headerEnv, authHeader, providerId)
                is { } auth
                    ? result with { Auth = auth }
                    : result;
        });

        return new ComposedApiKeyAuth { Name = name, Login = login, Check = check, ResolveImpl = resolve };
    }

    private static OAuthAuth? ComposeOAuthAuth(
        string providerId,
        ProviderSpec? base_,
        ModelsJsonProvider? config)
    {
        var oauth = base_?.Auth.OAuth;
        if (oauth is null)
        {
            return null;
        }

        var rawHeaders = config?.Headers;
        var authHeader = config?.AuthHeader ?? false;

        return new ComposedOAuthAuth
        {
            Name = oauth.Name,
            IsSubscription = oauth.IsSubscription,
            LoginLabel = oauth.LoginLabel,
            Inner = oauth,
            RawHeaders = rawHeaders,
            AuthHeader = authHeader,
            ProviderId = providerId,
        };
    }

    private static ModelAuth WithConfiguredAuth(
        ModelAuth auth,
        IReadOnlyDictionary<string, string>? headers,
        bool authHeader,
        string providerId)
    {
        var mergedHeaders = auth.Headers is not null || headers is not null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : null;
        if (mergedHeaders is not null)
        {
            if (auth.Headers is not null)
            {
                foreach (var (key, value) in auth.Headers)
                {
                    mergedHeaders[key] = value;
                }
            }

            if (headers is not null)
            {
                foreach (var (key, value) in headers)
                {
                    mergedHeaders[key] = value;
                }
            }
        }

        if (authHeader)
        {
            if (auth.ApiKey is null)
            {
                throw new InvalidOperationException("authHeader requires a resolved API key");
            }

            mergedHeaders ??= new Dictionary<string, string>(StringComparer.Ordinal);
            mergedHeaders["Authorization"] = $"Bearer {auth.ApiKey}";
        }

        return auth with { Headers = mergedHeaders };
    }

    /// <summary>
    /// Raw headers for a model from overrides/definitions (pinned Pi: rawModelHeaders).
    /// </summary>
    public static IReadOnlyDictionary<string, string>? RawModelHeaders(
        ModelInfo model,
        ModelsJsonProvider? config)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config?.ModelOverrides is not null &&
            config.ModelOverrides.TryGetValue(model.Id, out var override_) &&
            override_.Headers is not null)
        {
            foreach (var (key, value) in override_.Headers)
            {
                headers[key] = value;
            }
        }

        var definition = config?.Models?.FirstOrDefault(entry => entry.Id == model.Id);
        if (definition?.Headers is not null)
        {
            foreach (var (key, value) in definition.Headers)
            {
                headers[key] = value;
            }
        }

        return headers.Count > 0 ? headers : null;
    }

    /// <summary>
    /// Resolves configured headers for a model against the environment (pinned Pi:
    /// resolveConfiguredModelHeaders).
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ResolveConfiguredModelHeaders(
        ModelInfo model,
        ModelsJsonProvider? config,
        IReadOnlyDictionary<string, string>? env)
    {
        return ConfigValue.ResolveHeadersOrThrow(
            RawModelHeaders(model, config),
            $"model \"{model.Provider}/{model.Id}\"",
            env);
    }

    /// <summary>
    /// Resolves the compatibility request config for a model (pinned Pi:
    /// resolveCompatibilityRequestConfig).
    /// </summary>
    public static CompatibilityRequestConfig ResolveCompatibilityRequestConfig(
        ModelInfo model,
        ModelsJsonProvider? config)
    {
        var configured = ConfigValue.ResolveHeadersOrThrow(
            MergeDictionaries(config?.Headers, RawModelHeaders(model, config)),
            $"model \"{model.Provider}/{model.Id}\"",
            null);
        return new CompatibilityRequestConfig(
            model.Headers is not null || configured is not null
                ? MergeDictionaries(model.Headers, configured)
                : null,
            config?.AuthHeader ?? false);
    }

    private static IReadOnlyDictionary<string, string>? MergeDictionaries(
        IReadOnlyDictionary<string, string>? a,
        IReadOnlyDictionary<string, string>? b)
    {
        if (a is null && b is null)
        {
            return null;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (a is not null)
        {
            foreach (var (key, value) in a)
            {
                merged[key] = value;
            }
        }

        if (b is not null)
        {
            foreach (var (key, value) in b)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    /// <summary>
    /// Status of configured (non-stored) auth from models.json (pinned Pi:
    /// configuredRequestAuthStatus).
    /// </summary>
    public static AuthStatus? ConfiguredRequestAuthStatus(ModelsJsonProvider? config)
    {
        var value = config?.ApiKey;
        if (value is null)
        {
            return null;
        }

        if (ConfigValue.IsCommand(value))
        {
            return new AuthStatus(true, "models_json_command");
        }

        var names = ConfigValue.GetEnvVarNames(value);
        if (names.Length > 0)
        {
            return ConfigValue.IsConfigured(value)
                ? new AuthStatus(true, "environment", string.Join(", ", names))
                : new AuthStatus(false);
        }

        return new AuthStatus(true, "models_json_key");
    }

    /// <summary>
    /// ApiKeyAuth composed by <see cref="ComposeModelProvider"/>.
    /// </summary>
    private sealed class ComposedApiKeyAuth : ApiKeyAuth
    {
        public required Func<ApiKeyAuthInput, Task<AuthResult?>> ResolveImpl { get; init; }

        public override Task<AuthResult?> ResolveAsync(ApiKeyAuthInput input) => ResolveImpl(input);
    }

    /// <summary>
    /// OAuthAuth composed by <see cref="ComposeModelProvider"/>; wraps the base OAuth
    /// with configured headers and the authHeader rule.
    /// </summary>
    private sealed class ComposedOAuthAuth : OAuthAuth
    {
        public required OAuthAuth Inner { get; init; }
        public IReadOnlyDictionary<string, string>? RawHeaders { get; init; }
        public bool AuthHeader { get; init; }
        public required string ProviderId { get; init; }

        public override Task<OAuthCredential> LoginAsync(IAuthInteraction interaction) => Inner.LoginAsync(interaction);

        public override Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken cancellationToken)
            => Inner.RefreshAsync(credential, cancellationToken);

        public override async Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
        {
            var auth = await Inner.ToAuthAsync(credential);
            var headers = ConfigValue.ResolveHeadersOrThrow(RawHeaders, $"provider \"{ProviderId}\"", null);
            return WithConfiguredAuth(auth, headers, AuthHeader, ProviderId);
        }
    }
}
