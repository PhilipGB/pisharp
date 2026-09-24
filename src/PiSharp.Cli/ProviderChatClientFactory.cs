using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace PiSharp.Cli;

/// <summary>Chooses an actual provider request protocol, not merely an endpoint alias.</summary>
public static class ProviderChatClientFactory
{
    public static string ResolveProtocol(ModelSelection selection)
    {
        var protocol = selection.Model.Api ?? (selection.Provider.Id switch
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

    public static IChatClient Create(ModelSelection selection)
    {
        var protocol = ResolveProtocol(selection);
        if (protocol == "anthropic-messages")
            return new AnthropicClient { ApiKey = selection.ApiKey, BaseUrl = selection.Provider.Endpoint.ToString() }
                .AsIChatClient(selection.Model.Id);
        var options = new OpenAIClientOptions();
        if (selection.Connection.Endpoint is not null) options.Endpoint = selection.Connection.Endpoint;
        var client = new OpenAIClient(new ApiKeyCredential(selection.ApiKey), options);
        // The Responses adapter in the pinned OpenAI/MEAI SDK is still marked experimental.
#pragma warning disable OPENAI001
        return protocol == "openai-responses"
            ? new StatelessResponsesChatClient(client.GetResponsesClient().AsIChatClient(selection.Model.Id))
            : client.GetChatClient(selection.Model.Id).AsIChatClient();
#pragma warning restore OPENAI001
    }
}
