using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Cli;

/// <summary>
/// Bridges the provider-neutral Microsoft.Extensions.AI IChatClient surface to the
/// model runtime, mirroring pinned createOpenAICompletionsProvider + streamProxy:
/// resolves auth/endpoint/base URL per request from the CURRENT model selection, so
/// /model switches take effect on the very next request without rebuilding the agent.
///
/// The OpenAI SDK binds the model to the ChatClient instance, so the SDK client is cached
/// per (endpoint, credential, headers) — but the model id, max output tokens, and
/// reasoning effort are applied per request via ChatOptions (ModelId, MaxOutputTokens)
/// and RawRepresentationFactory (full Pi reasoning level set, max output pin), which the
/// adapter forwards verbatim while still merging tools.
/// </summary>
internal sealed class ModelRuntimeChatClient : IChatClient, IDisposable
{
    /// <summary>The only provider API PiSharp can execute (pinned: only this API is supported).</summary>
    private static readonly HashSet<string> SupportedApis = new(StringComparer.Ordinal)
    {
        "openai-completions",
    };

    private readonly ModelRuntime _runtime;
    private readonly Func<CurrentModelSelection?> _currentSelection;
    private readonly Func<long> _idleTimeoutMs;
    private readonly HttpClient _httpClient;
    private readonly PipelineTransport _transport;
    private readonly object _gate = new();
    private readonly List<CachedClient> _clients = [];

    /// <summary>
    /// Creates the bridge. <paramref name="currentSelection"/> must always return the
    /// live selection (typically () => modelState.Current) so model switches apply
    /// immediately; <paramref name="idleTimeoutMs"/> mirrors pinned httpIdleTimeoutMs
    /// (0 disables the watchdog).
    /// </summary>
    public ModelRuntimeChatClient(
        ModelRuntime runtime,
        Func<CurrentModelSelection?> currentSelection,
        Func<long> idleTimeoutMs,
        HttpMessageHandler? innerHandler = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _currentSelection = currentSelection ?? throw new ArgumentNullException(nameof(currentSelection));
        _idleTimeoutMs = idleTimeoutMs ?? throw new ArgumentNullException(nameof(idleTimeoutMs));
        // The error-capture handler wraps the caller's handler (tests substitute a recorder;
        // production passes null and the handler is terminal).
        _httpClient = innerHandler is null
            ? new HttpClient(new ProviderErrorCaptureHandler(), disposeHandler: true)
            : new HttpClient(new ProviderErrorCaptureHandler(innerHandler), disposeHandler: true);
        _transport = new HttpClientPipelineTransport(_httpClient);
    }

    /// <summary>Service lookup hook (M.E.AI IChatClient.GetService); no services are exposed.</summary>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Release the shared transport (and, with it, the handler chain that captures
        // provider errors); the cached SDK clients share it and are disposed explicitly.
        lock (_gate)
        {
            foreach (var entry in _clients)
            {
                (entry.Client as IDisposable)?.Dispose();
            }

            _clients.Clear();
        }

        _httpClient.Dispose();
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (client, adjusted) = await PrepareAsync(options, cancellationToken).ConfigureAwait(false);
        var timeoutMs = _idleTimeoutMs();
        if (timeoutMs <= 0)
        {
            return await client.GetResponseAsync(messages, adjusted, cancellationToken).ConfigureAwait(false);
        }

        // Non-streaming idle timeout: no response within the window. Pinned only applies
        // header/body idle timing to streams; this is the non-streaming equivalent. The guard
        // cancels the in-flight operation on timeout (a WhenAny race would abandon it) and
        // surfaces it as a status-less HttpRequestException (structurally transient at the
        // turn level), distinct from a caller cancellation (OperationCanceledException).
        var guard = new IdleTimeoutCancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
        try
        {
            return await client.GetResponseAsync(messages, adjusted, guard.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException($"The provider sent no response within {timeoutMs / 1000}s.");
        }
        finally
        {
            await guard.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (client, adjusted) = await PrepareAsync(options, cancellationToken).ConfigureAwait(false);
        var timeoutMs = _idleTimeoutMs();
        var guard = timeoutMs > 0
            ? new IdleTimeoutCancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken)
            : null;
        var token = guard?.Token ?? cancellationToken;
        var enumerator = client.GetStreamingResponseAsync(messages, adjusted, token).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (guard is not null && !cancellationToken.IsCancellationRequested)
                {
                    // The guard's idle window elapsed (the caller did not cancel): the
                    // in-flight operation has been cancelled by the guard. Surface it as a
                    // status-less HttpRequestException (structurally transient at the turn
                    // level), distinct from a caller cancellation.
                    throw new HttpRequestException(
                        $"The provider stopped sending data after {timeoutMs / 1000}s of inactivity.");
                }

                if (!moved)
                {
                    break;
                }

                // Pinned httpIdleTimeoutMs: header/body idle timeout. Each received chunk
                // resets the clock; the next MoveNext then starts a fresh window.
                guard?.Reset();
                yield return enumerator.Current;
            }
        }
        finally
        {
            // Cancel/observe the guard (aborting any in-flight operation) before disposing the
            // enumerator so the underlying HTTP operation unwinds promptly and is observed.
            if (guard is not null)
            {
                await guard.DisposeAsync().ConfigureAwait(false);
            }

            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<(IChatClient Client, ChatOptions Options)> PrepareAsync(
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var selection = _currentSelection();
        if (selection?.Model is not { } model)
        {
            throw new InvalidOperationException("No model is selected. Set one with /model or --model.");
        }

        if (!SupportedApis.Contains(model.Api))
        {
            throw new UnsupportedCapabilityException(model.Provider, model.Api);
        }

        var auth = await _runtime.GetAuthAsync(model, cancellationToken: cancellationToken).ConfigureAwait(false);
        var endpoint = auth?.Auth.BaseUrl ?? model.BaseUrl;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException($"Model {model.Reference} has no resolvable endpoint.");
        }

        Uri endpointUri;
        try
        {
            endpointUri = new Uri(endpoint);
        }
        catch (UriFormatException exception)
        {
            throw new InvalidOperationException($"Model {model.Reference} has an invalid endpoint: {endpoint}", exception);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (auth?.Auth.Headers is { } authHeaders)
        {
            foreach (var (name, value) in authHeaders)
            {
                headers[name] = value;
            }
        }

        // Pinned resolveAuthHeaders: a header-credential Authorization value becomes the
        // bearer for the SDK; any other credential comes through as the API key. An empty
        // key (keyless local server) means no auth at all.
        var apiKey = string.IsNullOrEmpty(auth?.Auth.ApiKey) ? null : auth!.Auth.ApiKey;
        string? bearerToken = null;
        if (headers.TryGetValue("Authorization", out var bearer) &&
            bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            bearerToken = bearer["Bearer ".Length..];
        }

        var client = await GetClientAsync(model, endpointUri, apiKey ?? bearerToken, headers).ConfigureAwait(false);
        var adjusted = CopyOptions(options);

        // Dynamic per-request model binding: overrides whatever the SDK client was built with.
        adjusted.ModelId = model.Id;

        // Pinned sends no max_tokens/max_completion_tokens when metadata is absent (the
        // server default applies); when metadata exists PiSharp pins it (CLI override wins,
        // documented difference).
        var effectiveMaxOutput = selection.EffectiveMaxOutput;
        if (effectiveMaxOutput > 0)
        {
            adjusted.MaxOutputTokens = effectiveMaxOutput;
        }

        string? effort = model.Reasoning
            && !selection.ThinkingLevel.Equals("off", StringComparison.Ordinal)
            ? MapThinkingLevel(model, selection.ThinkingLevel)
            : null;
        if (effort is not null)
        {
            var maxOutput = effectiveMaxOutput;
#pragma warning disable OPENAI001 // The SDK marks reasoning options evaluation-only; the pinned protocol sends reasoning_effort.
            // Raw options win over the adapter's mapping, which lets the full Pi level set
            // (including "minimal") reach the provider verbatim.
            adjusted.RawRepresentationFactory = _ => new OpenAI.Chat.ChatCompletionOptions
            {
                ReasoningEffortLevel = (OpenAI.Chat.ChatReasoningEffortLevel)effort,
                MaxOutputTokenCount = maxOutput > 0 ? maxOutput : null,
            };
#pragma warning restore OPENAI001
        }

        return (client, adjusted);
    }

    private async Task<IChatClient> GetClientAsync(
        ModelInfo model,
        Uri endpoint,
        string? apiKey,
        IReadOnlyDictionary<string, string> headers)
    {
        var fingerprint = FingerprintHeaders(headers);
        lock (_gate)
        {
            foreach (var cached in _clients)
            {
                if (cached.Endpoint == endpoint.ToString() &&
                    cached.ApiKey == apiKey &&
                    cached.HeadersFingerprint == fingerprint)
                {
                    return cached.Client;
                }
            }
        }

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            Transport = _transport,
            // Provider retries are owned by ProviderRetryClient (pinned sets the SDK retry to
            // zero so backoff sleeps stay interruptible and request counts are exact).
            RetryPolicy = new ClientRetryPolicy(0),
        };

        foreach (var (name, value) in headers)
        {
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue; // credential is passed as apiKey/bearer, not a static header
            }

            clientOptions.AddPolicy(new StaticHeaderPolicy(name, value), PipelinePosition.PerCall);
        }

        // The model bound at construction is a placeholder: every request sets
        // ChatOptions.ModelId, which the adapter sends instead (verified against the wire).
#pragma warning disable OPENAI001 // Credential-taking constructors are marked evaluation-only by the SDK.
        var client = apiKey is not null
            ? new OpenAI.Chat.ChatClient(model.Id, new ApiKeyCredential(apiKey), clientOptions).AsIChatClient()
            : new OpenAI.Chat.ChatClient(model.Id, new NoAuthPolicy(), clientOptions).AsIChatClient();
#pragma warning restore OPENAI001

        var entry = new CachedClient(endpoint.ToString(), apiKey, fingerprint, client);
        lock (_gate)
        {
            _clients.Add(entry);
        }

        return client;
    }

    /// <summary>
    /// Pinned getReasoningLevelForApi openai-completions: the model's declared levels are
    /// sent verbatim; otherwise a generic model's off/xhigh mapping.
    /// </summary>
    private static string MapThinkingLevel(ModelInfo model, string level)
    {
        if (model.ThinkingLevelMap is { Count: > 0 } map &&
            map.TryGetValue(level, out var mapped) && mapped is not null)
        {
            return mapped;
        }

        return level.Equals("off", StringComparison.Ordinal) ? "disabled" : level;
    }

    private static string FingerprintHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var parts = headers
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToArray();
        return string.Join(";", parts);
    }

    /// <summary>Copies request options the adapter needs; model-specific fields are set in PrepareAsync.</summary>
    private static ChatOptions CopyOptions(ChatOptions? options)
    {
        if (options is null)
        {
            return new ChatOptions();
        }

        return new ChatOptions()
        {
            ConversationId = options.ConversationId,
            Instructions = options.Instructions,
            Temperature = options.Temperature,
            TopP = options.TopP,
            TopK = options.TopK,
            FrequencyPenalty = options.FrequencyPenalty,
            PresencePenalty = options.PresencePenalty,
            MaxOutputTokens = options.MaxOutputTokens,
            Seed = options.Seed,
            StopSequences = options.StopSequences,
            Tools = options.Tools,
            ToolMode = options.ToolMode,
            ResponseFormat = options.ResponseFormat,
            Reasoning = options.Reasoning,
            AdditionalProperties = options.AdditionalProperties is { Count: > 0 } props
                ? new AdditionalPropertiesDictionary(props)
                : new AdditionalPropertiesDictionary(),
        };
    }

    /// <summary>Cached SDK client; shared by every model that hits the same endpoint/credential.</summary>
    private sealed record CachedClient(string Endpoint, string? ApiKey, string HeadersFingerprint, IChatClient Client);

    /// <summary>Adds each configured header to the outgoing request (pinned model.headers).</summary>
    private sealed class StaticHeaderPolicy : PipelinePolicy
    {
        private readonly string _name;
        private readonly string _value;

        public StaticHeaderPolicy(string name, string value)
        {
            _name = name;
            _value = value;
        }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> policies, int policyIndex)
        {
            message.Request.Headers.Add(_name, _value);
            ProcessNext(message, policies, policyIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> policies, int policyIndex)
        {
            message.Request.Headers.Add(_name, _value);
            return ProcessNextAsync(message, policies, policyIndex);
        }
    }

    /// <summary>Advances the pipeline without authentication (pinned: keyless endpoints).</summary>
    private sealed class NoAuthPolicy : AuthenticationPolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> policies, int policyIndex)
            => ProcessNext(message, policies, policyIndex);

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> policies, int policyIndex)
            => ProcessNextAsync(message, policies, policyIndex);
    }
}
