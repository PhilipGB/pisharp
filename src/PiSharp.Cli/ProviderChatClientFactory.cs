using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Anthropic.Credentials;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Cli;

/// <summary>Chooses an actual provider request protocol, not merely an endpoint alias.</summary>
public static class ProviderChatClientFactory
{
    public static string GetProtocolId(ProviderProfile provider, PiSharp.Runtime.Providers.ModelDescriptor model) =>
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
        IChatClient providerClient;
        if (protocol == "bedrock-converse-stream")
        {
            providerClient = BedrockConverseChatClientFactory.Create(selection);
        }
        else if (protocol == "pi-messages")
        {
            providerClient = PiMessagesChatClientFactory.Create(selection);
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
                Handlers = [new AnthropicSystemMessageHandler(transcript), new ProviderWireActivityHandler()]
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
                    new HttpClient(new ProviderWireActivityHandler(new HttpClientHandler()), disposeHandler: true));
            OpenAiToolCallDeltaCapture? toolCallCapture = null;
            MistralChatRequestContext? mistralContext = null;
            AzureOpenAiRequestContext? azureContext = null;
            if (protocol == "azure-openai-responses")
            {
                azureContext = new AzureOpenAiRequestContext();
                var azureOptions = selection.Provider.AzureOpenAi ??
                    new AzureOpenAiProviderOptions("v1", new Dictionary<string, string>(StringComparer.Ordinal));
                options.Transport = new HttpClientPipelineTransport(new HttpClient(
                    new AzureOpenAiRequestHandler(selection.ApiKey, azureOptions, azureContext,
                        new ProviderWireActivityHandler(new HttpClientHandler { AllowAutoRedirect = false })),
                    disposeHandler: true));
            }
            if (protocol is "openai-completions" or "mistral-conversations")
            {
                HttpMessageHandler? handler = null;
                if (protocol == "mistral-conversations")
                {
                    mistralContext = new MistralChatRequestContext();
                    handler = new MistralChatCompatibilityHandler(mistralContext,
                        new ProviderWireActivityHandler(new HttpClientHandler()));
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
            providerClient = toolCallCapture is null ? chat : new OpenAiCompletionsToolCallDeltaClient(chat, toolCallCapture);
            if (mistralContext is not null)
                providerClient = new MistralChatOptionsClient(providerClient, selection.Model, mistralContext);
            if (azureContext is not null)
                providerClient = new AzureOpenAiChatOptionsClient(providerClient, azureContext);
        }
        var timeoutClient = new ProviderRequestTimeoutChatClient(providerClient, requestTimeout, idleTimeout);
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
