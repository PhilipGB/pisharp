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
            _ => "openai-completions"
        });
        if (protocol is not ("openai-responses" or "openai-completions" or "anthropic-messages"))
            throw new NotSupportedException($"Model '{selection.Provider.Id}/{selection.Model.Id}' requires unsupported API '{protocol}'.");
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
        if (protocol == "anthropic-messages")
        {
            var anthropicClient = new AnthropicClient
            {
                ApiKey = selection.ApiKey,
                BaseUrl = selection.Connection.Endpoint?.ToString() ?? selection.Provider.Endpoint.ToString(),
                MaxRetries = providerMaxRetries,
                Timeout = sdkTimeout,
                Handlers = [new ProviderWireActivityHandler()]
            };
            anthropicClient.HttpClient.Timeout = Timeout.InfiniteTimeSpan;
            var anthropicChat = anthropicClient.AsIChatClient(selection.Model.Id,
                selection.Model.MaxOutputTokens ?? 16384,
                thinkingMode: selection.Model.Id is "claude-sonnet-4-6" or "claude-opus-4-6"
                    ? AnthropicThinkingMode.Adaptive : AnthropicThinkingMode.Extended);
            return new ProviderRequestTimeoutChatClient(anthropicChat, requestTimeout, idleTimeout);
        }
        var options = new OpenAIClientOptions();
        options.NetworkTimeout = sdkTimeout;
        options.RetryPolicy = new ClientRetryPolicy(providerMaxRetries);
        if (selection.Connection.Endpoint is not null) options.Endpoint = selection.Connection.Endpoint;
        if (protocol == "openai-responses")
            options.Transport = new HttpClientPipelineTransport(
                new HttpClient(new ProviderWireActivityHandler(new HttpClientHandler()), disposeHandler: true));
        OpenAiToolCallDeltaCapture? toolCallCapture = null;
        if (protocol == "openai-completions")
        {
            toolCallCapture = new OpenAiToolCallDeltaCapture();
            options.Transport = toolCallCapture.Transport;
        }
        var client = new OpenAIClient(new ApiKeyCredential(selection.ApiKey), options);
        // The Responses adapter in the pinned OpenAI/MEAI SDK is still marked experimental.
#pragma warning disable OPENAI001
        var chat = protocol == "openai-responses"
            ? new StatelessResponsesChatClient(client.GetResponsesClient().AsIChatClient(selection.Model.Id))
            : client.GetChatClient(selection.Model.Id).AsIChatClient();
#pragma warning restore OPENAI001
        var providerClient = toolCallCapture is null ? chat : new OpenAiCompletionsToolCallDeltaClient(chat, toolCallCapture);
        return new ProviderRequestTimeoutChatClient(providerClient, requestTimeout, idleTimeout);
    }
}
