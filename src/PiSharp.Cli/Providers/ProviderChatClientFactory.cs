using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Anthropic.Credentials;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.VirtualModels;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli;

/// <summary>Chooses an actual provider request protocol, not merely an endpoint alias.</summary>
public static class ProviderChatClientFactory
{
    public static string GetProtocolId(ProviderProfile provider, PiSharp.Runtime.Providers.ModelDescriptor model) =>
        provider.ExtensionProviderHandle is not null ? PiSharpExtensionProviderContract.ChatApi :
        model.Api ?? provider.Api ?? (provider.Id switch
        {
            "openai" or "xai" => "openai-responses",
            "anthropic" => "anthropic-messages",
            "mistral" => "mistral-conversations",
            _ => "openai-completions"
        });

    public static string ResolveProtocol(ModelSelection selection)
    {
        if (selection.Model.Api == VirtualModelContract.Api) return VirtualModelContract.Api;
        if (selection.Provider.ExtensionProviderHandle is not null)
            return PiSharpExtensionProviderContract.ChatApi;
        var protocol = GetProtocolId(selection.Provider, selection.Model);
        if (protocol == "google-generative-ai" && selection.Provider.Id != "google")
            throw new NotSupportedException($"Model '{selection.Provider.Id}/{selection.Model.Id}' requires unsupported API '{protocol}'.");
        if (protocol == "google-vertex" && selection.Provider.Id != "google-vertex")
            throw new NotSupportedException($"Model '{selection.Provider.Id}/{selection.Model.Id}' requires unsupported API '{protocol}'.");
        if (protocol == "bedrock-converse-stream" && selection.Provider.Id != "amazon-bedrock")
            throw new NotSupportedException($"Model '{selection.Provider.Id}/{selection.Model.Id}' requires unsupported API '{protocol}'.");
        if (protocol is not ("openai-responses" or "openai-completions" or "anthropic-messages" or "mistral-conversations" or "azure-openai-responses" or "openai-codex-responses" or "google-generative-ai" or "google-vertex" or "bedrock-converse-stream" or "pi-messages"))
            throw new NotSupportedException($"Model '{selection.Provider.Id}/{selection.Model.Id}' requires unsupported API '{protocol}'.");
        if (protocol == "azure-openai-responses")
            _ = AzureOpenAiEndpoint.RequireConfigured(selection.Connection.Endpoint ?? selection.Provider.Endpoint);
        if (selection.Provider.Id == "azure")
        {
            if (protocol != "openai-completions")
                throw new NotSupportedException("The built-in azure provider uses Foundry Chat Completions; Azure Responses uses azure-openai-responses.");
            _ = AzureOpenAiEndpoint.RequireConfigured(selection.Connection.Endpoint ?? selection.Provider.Endpoint);
        }
        // The official OpenAI identity must never be redirected by catalog metadata to an alternate endpoint.
        if (selection.Provider.Id == "openai" && !ProviderModelRuntime.IsOfficialOpenAiEndpoint(selection.Provider.Endpoint))
            throw new InvalidOperationException("The built-in OpenAI identity requires the official endpoint.");
        return protocol;
    }

    public static IChatClient Create(ModelSelection selection, ProviderRetrySettings? retrySettings = null,
        int? httpIdleTimeoutMs = null)
    {
        if (selection.Model.Api == VirtualModelContract.Api) return new UnroutedVirtualModelChatClient(selection.Model);
        var protocol = ResolveProtocol(selection);
        var providerMaxRetries = retrySettings?.MaxRetries ?? ProviderRetrySettings.DefaultMaxRetries;
        var idleTimeout = httpIdleTimeoutMs ?? UserSettings.DefaultHttpIdleTimeoutMs;
        var configuredTimeout = retrySettings?.TimeoutMs;
        if (idleTimeout < 0 || configuredTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(retrySettings), "Provider timeouts cannot be negative.");
        var requestTimeout = configuredTimeout ?? (idleTimeout == 0 ? int.MaxValue : idleTimeout);
        var sdkTimeout = TimeSpan.FromMilliseconds(requestTimeout);
        var samplingSupported = protocol is "openai-completions" or "openai-responses" or "azure-openai-responses";
        var samplingContext = new SamplingParametersRequestContext();
        IChatClient providerClient;
        ProviderToolCallDeltaCapture? argumentCapture = null;
        if (protocol == PiSharpExtensionProviderContract.ChatApi)
        {
            var handle = selection.Provider.ExtensionProviderHandle ?? throw new InvalidOperationException(
                $"Extension provider '{selection.Provider.Id}' from '{selection.Provider.ExtensionPath}' is no longer registered.");
            var extension = handle.Definition ?? throw new InvalidOperationException(
                $"Extension provider '{selection.Provider.Id}' from '{selection.Provider.ExtensionPath}' is no longer registered.");
            try
            {
                providerClient = extension.CreateChatClient(new(selection.Provider.Id, selection.Model.Id,
                    extension.Endpoint, selection.Authenticated && selection.AuthSource != "not required" &&
                    selection.ApiKey is not ("not-needed" or "not-configured") ? selection.ApiKey : null)
                {
                    ProviderWire = new NativeExtensionProviderWireBridge(selection.Provider.Id, selection.Model.Id,
                        protocol)
                }) ??
                    throw new InvalidOperationException("Provider factory returned no chat client.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                var safe = SecretRedactor.Redact(error.Message, selection.ApiKey);
                throw new InvalidOperationException($"Extension provider '{selection.Provider.Id}' for model " +
                    $"'{selection.Model.Id}' from '{selection.Provider.ExtensionPath}' failed to create a chat client: {safe}");
            }
            providerClient = handle.TrackClient(new ProviderSecretRedactingChatClient(providerClient, selection.ApiKey));
        }
        else if (protocol == "bedrock-converse-stream")
        {
            providerClient = BedrockConverseChatClientFactory.Create(selection);
        }
        else if (protocol == "pi-messages")
        {
            providerClient = PiMessagesChatClientFactory.Create(selection);
        }
        else if (protocol == "mistral-conversations")
        {
            providerClient = MistralConversationsChatClientFactory.Create(selection, configuredTimeout ?? 60_000);
        }
        else if (protocol == "google-generative-ai")
        {
            providerClient = GoogleGenAiChatClientFactory.Create(selection);
        }
        else if (protocol == "google-vertex")
        {
            providerClient = GoogleVertexChatClientFactory.Create(selection);
        }
        else if (protocol == "openai-codex-responses")
        {
            providerClient = OpenAiCodexResponsesClientFactory.Create(selection, sdkTimeout);
        }
        else if (protocol == "anthropic-messages")
        {
            argumentCapture = new ProviderToolCallDeltaCapture();
            var transcript = new AnthropicTranscriptRequest();
            var baseUrl = selection.Connection.Endpoint?.ToString() ?? selection.Provider.Endpoint.ToString();
            var options = new ClientOptions
            {
                ApiKey = selection.AnthropicAuthToken is null && selection.AnthropicWorkloadIdentity is null
                    ? selection.ApiKey : null,
                AuthToken = selection.AnthropicAuthToken,
                BaseUrl = baseUrl,
                MaxRetries = 0,
                Timeout = sdkTimeout,
                Handlers = [new AnthropicSystemMessageHandler(transcript), argumentCapture.CreateHandler(),
                    ProviderWireHookHandler.CreateAnthropic(selection)]
            };
            if (selection.AnthropicIsOAuthToken)
            {
                options.ExtraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["accept"] = "application/json",
                    ["anthropic-dangerous-direct-browser-access"] = "true",
                    ["x-app"] = "cli"
                };
            }
            if (selection.AnthropicWorkloadIdentity is { } federation)
            {
                options.Credentials = new WorkloadIdentityCredentials(new WorkloadIdentityOptions
                {
                    FederationRuleId = federation.FederationRuleId,
                    OrganizationId = federation.OrganizationId,
                    ServiceAccountId = federation.ServiceAccountId,
                    WorkspaceId = federation.WorkspaceId,
                    IdentityTokenProvider = new FileIdentityTokenProvider(federation.IdentityTokenFile),
                    BaseUrl = baseUrl
                });
            }
            var anthropicClient = new AmbientCredentialsDisabledAnthropicClient(options);
            anthropicClient.HttpClient.Timeout = Timeout.InfiniteTimeSpan;
            providerClient = new AnthropicTranscriptChatClient(new AnthropicThinkingSignatureClient(anthropicClient.Beta.AsIChatClient(selection.Model.Id,
                selection.Model.MaxOutputTokens ?? 16384,
                thinkingMode: selection.Model.Id is "claude-sonnet-4-6" or "claude-opus-4-6"
                    ? AnthropicThinkingMode.Adaptive : AnthropicThinkingMode.Extended),
                AllowsEmptyThinkingSignature(selection.Model.Compatibility)), selection.Model, transcript, selection.AnthropicIsOAuthToken);
        }
        else
        {
            var options = new OpenAIClientOptions
            {
                NetworkTimeout = sdkTimeout,
                RetryPolicy = new ClientRetryPolicy(0)
            };
            if (protocol == "azure-openai-responses")
                options.Endpoint = AzureOpenAiEndpoint.RequireConfigured(
                    selection.Connection.Endpoint ?? selection.Provider.Endpoint);
            else if (selection.Connection.Endpoint is not null) options.Endpoint = selection.Connection.Endpoint;
            if (protocol == "openai-responses")
                options.Transport = new HttpClientPipelineTransport(
                    new HttpClient(new ProviderSamplingRequestHandler(samplingContext!,
                        ProviderWireHookHandler.CreateWithActivity(selection, protocol,
                            new HttpClientHandler())), disposeHandler: true));
            OpenAiToolCallDeltaCapture? toolCallCapture = null;
            MistralChatRequestContext? mistralContext = null;
            AzureOpenAiRequestContext? azureContext = null;
            if (protocol == "azure-openai-responses")
            {
                azureContext = new AzureOpenAiRequestContext();
                var azureOptions = selection.Provider.AzureOpenAi ??
                    new AzureOpenAiProviderOptions("v1", new Dictionary<string, string>(StringComparer.Ordinal));
                options.Transport = new HttpClientPipelineTransport(new HttpClient(
                    new ProviderSamplingRequestHandler(samplingContext!,
                        new AzureOpenAiRequestHandler(selection.ApiKey, azureOptions, azureContext,
                            ProviderWireHookHandler.CreateWithActivity(selection, protocol,
                                new HttpClientHandler { AllowAutoRedirect = false }))),
                    disposeHandler: true));
            }
            if (protocol is "openai-completions" or "mistral-conversations")
            {
                HttpMessageHandler? handler = null;
                if (protocol == "mistral-conversations")
                {
                    mistralContext = new MistralChatRequestContext();
                    handler = new MistralChatCompatibilityHandler(mistralContext,
                        ProviderWireHookHandler.CreateWithActivity(selection, protocol,
                            new HttpClientHandler()));
                }
                else
                {
                    var azureFoundry = selection.Provider.Id == "azure";
                    HttpMessageHandler innerHandler = new OpenAiCompletionsRequestCompatibilityHandler(selection.Model,
                        selection.Provider.Id, selection.Connection.Endpoint ?? selection.Provider.Endpoint,
                        selection.Provider.Compatibility,
                        ProviderWireHookHandler.CreateWithActivity(selection, protocol,
                            new HttpClientHandler { AllowAutoRedirect = !azureFoundry }));
                    if (azureFoundry)
                    {
                        var azureOptions = selection.Provider.AzureOpenAi ??
                            new AzureOpenAiProviderOptions("v1", new Dictionary<string, string>(StringComparer.Ordinal));
                        innerHandler = new AzureFoundryChatCompletionsRequestHandler(azureOptions, innerHandler);
                    }
                    handler = new ProviderSamplingRequestHandler(samplingContext!, innerHandler);
                }
                toolCallCapture = new OpenAiToolCallDeltaCapture(handler);
                options.Transport = toolCallCapture.Transport;
            }
            var client = new OpenAIClient(new ApiKeyCredential(selection.ApiKey), options);
            // The Responses adapter in the pinned OpenAI/MEAI SDK is still marked experimental.
#pragma warning disable OPENAI001
            var chat = protocol is "openai-responses" or "azure-openai-responses"
                ? new StatelessResponsesChatClient(client.GetResponsesClient().AsIChatClient(selection.Model.Id))
                : client.GetChatClient(selection.Model.Id).AsIChatClient();
#pragma warning restore OPENAI001
            providerClient = chat;
            argumentCapture = toolCallCapture?.Capture;
            if (mistralContext is not null)
                providerClient = new MistralChatOptionsClient(providerClient, selection.Model, mistralContext);
            if (azureContext is not null)
                providerClient = new AzureOpenAiChatOptionsClient(providerClient, azureContext);
        }
        providerClient = new ModelSamplingParametersChatClient(providerClient, selection.Model, samplingContext,
            samplingSupported);
        var streamRequestTimeout = protocol == "mistral-conversations" ? int.MaxValue : requestTimeout;
        var timeoutClient = new ProviderRequestTimeoutChatClient(new ProviderToolArgumentChatClient(providerClient, argumentCapture), streamRequestTimeout, idleTimeout);
        return new ProviderRetryChatClient(timeoutClient, providerMaxRetries,
            retrySettings?.MaxRetryDelayMs ?? ProviderRetrySettings.DefaultMaxRetryDelayMs);
    }

    private sealed class AmbientCredentialsDisabledAnthropicClient(ClientOptions options) : AnthropicClient(options)
    {
        protected override bool ShouldAutoResolveCredentials => false;
    }

    private static bool AllowsEmptyThinkingSignature(System.Text.Json.JsonElement? compatibility) =>
        compatibility is { ValueKind: System.Text.Json.JsonValueKind.Object } value &&
        value.TryGetProperty("allowEmptySignature", out var allowEmpty) &&
        allowEmpty.ValueKind == System.Text.Json.JsonValueKind.True;
}
