using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace PiSharp.Cli;

/// <summary>Chooses an actual provider request protocol, not merely an endpoint alias.</summary>
public static class ProviderChatClientFactory
{
    public static string ResolveProtocol(ModelSelection selection)
    {
        var protocol = selection.Model.Api ?? selection.Provider.Api ?? (selection.Provider.Id switch
        {
            "openai" or "xai" => "openai-responses",
            "anthropic" => "anthropic-messages",
            "mistral" => "mistral-conversations",
            _ => "openai-completions"
        });
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
            var anthropicClient = new AnthropicClient
            {
                ApiKey = selection.ApiKey,
                BaseUrl = selection.Connection.Endpoint?.ToString() ?? selection.Provider.Endpoint.ToString(),
                MaxRetries = 0,
                Timeout = sdkTimeout,
                Handlers = [new ProviderWireActivityHandler()]
            };
            anthropicClient.HttpClient.Timeout = Timeout.InfiniteTimeSpan;
            providerClient = new AnthropicThinkingSignatureClient(anthropicClient.AsIChatClient(selection.Model.Id,
                selection.Model.MaxOutputTokens ?? 16384,
                thinkingMode: selection.Model.Id is "claude-sonnet-4-6" or "claude-opus-4-6"
                    ? AnthropicThinkingMode.Adaptive : AnthropicThinkingMode.Extended),
                AllowsEmptyThinkingSignature(selection.Model.Compatibility));
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

    private static bool AllowsEmptyThinkingSignature(System.Text.Json.JsonElement? compatibility) =>
        compatibility is { ValueKind: System.Text.Json.JsonValueKind.Object } value &&
        value.TryGetProperty("allowEmptySignature", out var allowEmpty) &&
        allowEmpty.ValueKind == System.Text.Json.JsonValueKind.True;
}
